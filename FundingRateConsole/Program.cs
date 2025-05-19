using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot.Socket;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.CommonObjects;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Interfaces.CommonClients;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

class Program
{
    // Binance API ve Socket Client
    private static BinanceRestClient client;
    private static BinanceSocketClient socketClient;

    // Funding Rate Verisi ve Koleksiyonlar
    private static ConcurrentDictionary<string, FundingRateRecord> nonTargetFundingRates = new();
    private static ConcurrentDictionary<string, FundingRateRecord> TargetFundingRates = new();
    private static ConcurrentDictionary<string, DateTime> IntervalFundingRates = new();
    private static ConcurrentDictionary<string, DateTime> NegativeTwoFundingRates = new();

    // Telegram Bot Bilgileri
    private static readonly string botToken = "7938765330:AAFC6-bpOiffLaa8iSQwpzl0h3FR_yYT4s4";
    private static readonly string chatId = "7250151162";

    //Binance Api Bilgileri
    private static string apiKey = "77DGU2lh3Up1YytSuyOnuAAq9scplj1KwXTIvgUj969MKbLvcYhMSIBr34g3VE4U";
    private static string apiSecret = "IjP1ZmJXcrRxnep0koHlqnbELxYagXgm295FP0wHG2Ow3QV2jQCasUAyWEmem38l";
    private static string listenKey;
    // Hedef Değerler ve Eşikler
    private static decimal firstDestinition = -0.05m;
    private static decimal secondDestinition = -2m;
    private static decimal speedTrashold = 1;

    // Top Gainers
    private static List<(string Symbol, decimal Change)> topGainers = new();
    private const int topGainerCount = 20;
    private const decimal minimumVolume = 10_000_000;

    // Order Durumu
    private static bool isOrderActive = false;

    // Diğer Değişkenler
    private static readonly object locker = new();

    static HashSet<string> spotKnownSymbols = new();
    static HashSet<string> futuresKnownSymbols = new();
    static async Task Main(string[] args)
    {
        _ = SendTelegramMessage("Console Uygulaması başlatıldı.");
        Console.WriteLine("Funding Rate Bot Starting...");

        client = new BinanceRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(
                apiKey,
                apiSecret
            );
            options.AutoTimestamp = true;
        });

        socketClient = new BinanceSocketClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(
                apiKey,
                apiSecret
            );
        });

        var listenKeyResult = client.UsdFuturesApi.Account.StartUserStreamAsync();
        listenKey = listenKeyResult.Result.Data;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(30));
                await client.UsdFuturesApi.Account.KeepAliveUserStreamAsync(listenKey);
            }
        });

        // Mevcut sembolleri belleğe al
        var spotSymbols = await client.SpotApi.ExchangeData.GetExchangeInfoAsync();
        var futuresSymbols = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

        foreach (var s in spotSymbols.Data.Symbols)
            spotKnownSymbols.Add(s.Name);

        foreach (var s in futuresSymbols.Data.Symbols)
            futuresKnownSymbols.Add(s.Name);

        await StartSubscription();


        Console.ReadLine();
        Console.ReadKey();
    }


    static async Task updated()
    {
        Console.WriteLine("updated() Func Starting...");
        var orderSubscription = await socketClient.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
        listenKey,
        data =>
        {
            Console.WriteLine("Hesap Güncellemesi Geldi!");
        },
          data =>
          {
              Console.WriteLine("Order Güncellemesi Geldi!");
          },
          data =>
          {
              Console.WriteLine("Marjin Güncellemesi Geldi!");
          },
       async data =>
       {
           Console.WriteLine("Listen Key Süresi Doldu, Yenileniyor...");

           if (data.Data.UpdateData.Status == OrderStatus.Filled &&
               data.Data.UpdateData.Side == OrderSide.Buy)
           {
               string symbol = data.Data.UpdateData.Symbol;
               decimal filledPrice = data.Data.UpdateData.AveragePrice;
               decimal quantity = data.Data.UpdateData.Quantity;

               // Fiyat sıfır veya negatifse hata ver
               if (filledPrice <= 0)
               {
                   Console.WriteLine("[Hata] Fiyat alınamadı veya sıfır! Fiyat: " + filledPrice);
                   return;
               }

               // Sembol bilgilerini yeniden al
               var exchangeInfo = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
               var symbolInfo = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);

               if (symbolInfo == null)
               {
                   Console.WriteLine("[Hata] Sembol bilgisi alınamadı!");
                   return;
               }

               decimal tickSize = symbolInfo.PriceFilter.TickSize;

               // TickSize sıfır olamaz, bunu kontrol et
               if (tickSize == 0)
               {
                   Console.WriteLine("[Hata] Tick size sıfır olamaz!");
                   return;
               }

               // Precision bilgilerini al
               int pricePrecision = tickSize.ToString(CultureInfo.InvariantCulture).Split('.').Last().Length;

               // TP ve SL hesaplamaları
               decimal tpPercentage = 3;  // %2
               decimal slPercentage = 6;  // %6

               decimal tpMultiplier = 1 + (tpPercentage / 100);
               decimal slMultiplier = 1 - (slPercentage / 100);

               // TP ve SL fiyatlarını hesapla
               decimal takeProfitPrice = filledPrice * tpMultiplier;
               decimal stopLossPrice = filledPrice * slMultiplier;

               // Sıfıra bölme kontrolü (gerekli işlemleri yapmadan önce)
               if (takeProfitPrice == 0 || stopLossPrice == 0)
               {
                   Console.WriteLine("[Hata] TakeProfit veya StopLoss fiyatı sıfır oldu!");
                   return;
               }

               // TickSize'ye yuvarlama
               takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;
               stopLossPrice = Math.Ceiling(stopLossPrice / tickSize) * tickSize;

               // Precision'a yuvarlama
               takeProfitPrice = Math.Round(takeProfitPrice, pricePrecision);
               stopLossPrice = Math.Round(stopLossPrice, pricePrecision);

               Console.WriteLine($"[TP & SL] TP: {takeProfitPrice}, SL: {stopLossPrice}");

               try
               {
                   // ✅ Take-Profit (TP) - Market Tipinde Tetiklenir
                   var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                       symbol: symbol,
                       side: OrderSide.Sell,
                       type: FuturesOrderType.TakeProfitMarket,
                       quantity: quantity,
                       stopPrice: takeProfitPrice,
                       timeInForce: TimeInForce.GoodTillCanceled,
                       reduceOnly: true
                   );

                   if (!tpOrder.Success)
                   {
                       Console.WriteLine($"[TP Order Hatası] {tpOrder.Error?.Message}");
                       return;
                   }

                   // ✅ Stop-Loss (SL) - Market Tipinde Tetiklenir
                   //var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                   //    symbol: symbol,
                   //    side: OrderSide.Sell,
                   //    type: FuturesOrderType.StopMarket,
                   //    quantity: quantity,
                   //    stopPrice: stopLossPrice,
                   //    timeInForce: TimeInForce.GoodTillCanceled
                   //);

                   //if (!slOrder.Success)
                   //{
                   //    Console.WriteLine($"[SL Order Hatası] {slOrder.Error?.Message}");
                   //    return;
                   //}

                   Console.WriteLine("[Bilgi] TP ve SL emirleri başarıyla yerleştirildi.");
               }
               catch (Exception ex)
               {
                   Console.WriteLine($"[Hata] Asenkron işlemde bir hata oluştu: {ex.Message}");
               }
           }


           if (data.Data.UpdateData.Status == OrderStatus.Filled &&
               data.Data.UpdateData.OriginalType == FuturesOrderType.TakeProfit)
           {
               isOrderActive = false;
           }
       });

    }
    static async Task PlaceOrderAsync(string symbol)
    {
        decimal desiredLeverage = 10;

        // 1. Sembol bilgilerini al
        var exchangeInfo = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
        var symbolInfo = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);

        if (symbolInfo == null)
        {
            Console.WriteLine("Sembol bilgisi alınamadı.");
            isOrderActive = false;
            return;
        }

        // 2. Price & quantity precision
        decimal tickSize = symbolInfo.PriceFilter.TickSize;
        decimal lotSize = symbolInfo.LotSizeFilter.MinQuantity;

        int pricePrecision = tickSize.ToString(CultureInfo.InvariantCulture).Split('.').Last().Length;
        int quantityPrecision = lotSize.ToString(CultureInfo.InvariantCulture).Split('.').Last().Length;

        if (tickSize == 0 || lotSize == 0)
        {
            Console.WriteLine("TickSize veya LotSize sıfır olamaz.");
            isOrderActive = false;

            return;
        }

        // 3. Kaldıraç ayarla
        await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, (int)desiredLeverage);

        // 4. Güncel fiyatı al ve tickSize'a göre ayarla
        var bookPriceResult = await client.UsdFuturesApi.ExchangeData.GetBookPriceAsync(symbol);
        decimal filledPrice = bookPriceResult.Data.BestAskPrice;
        decimal assetPrice = Math.Floor(filledPrice / tickSize) * tickSize;
        assetPrice = Math.Round(assetPrice, pricePrecision);

        // 5. USDT bakiyesini al
        var balanceResult = await client.UsdFuturesApi.Account.GetBalancesAsync();
        decimal usdtBalance = balanceResult.Data.FirstOrDefault(b => b.Asset == "USDT")?.AvailableBalance ?? 0;

        if (usdtBalance <= 0)
        {
            Console.WriteLine("USDT bakiyesi yetersiz.");
            isOrderActive = false;
            return;
        }

        // 6. Kullanılabilir teminat ve pozisyon büyüklüğü hesapla
        decimal usableMargin = usdtBalance * 0.99m;
        decimal quantity = (usableMargin * desiredLeverage) / assetPrice;

        // LotSize hassasiyetine göre miktarı ayarla
        quantity = Math.Floor(quantity / lotSize) * lotSize;
        quantity = Math.Round(quantity, quantityPrecision);

        if (quantity <= 0)
        {
            Console.WriteLine("Geçersiz işlem miktarı.");
            isOrderActive = false;

            return;
        }

        Console.WriteLine($"Mevcut bakiye: {usdtBalance} USDT");
        Console.WriteLine($"Fiyat: {assetPrice}");
        Console.WriteLine($"İşlem miktarı: {quantity}");

        // 7. Emir gönder
        var orderResult = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
            symbol: symbol,
            side: OrderSide.Buy,
            type: FuturesOrderType.Limit,
            price: assetPrice,
            timeInForce: TimeInForce.GoodTillCanceled,
            quantity: quantity
        );

        if (!orderResult.Success)
        {
            Console.WriteLine($"Market Order Hatası: {orderResult.Error?.Message}");
            isOrderActive = false;

            return;
        }

        Console.WriteLine($"İşlem başarıyla gerçekleşti. Emir ID: {orderResult.Data.Id}");
    }
    private static async Task SendTelegramMessage(string message)
    {
        var botClient = new TelegramBotClient(botToken);
        await botClient.SendTextMessageAsync(chatId, message);
    }

    private static async Task StartSubscription()
    {
        var subscribeTask = SubscribeToTickerUpdatesAsync();
        var updataTask = updated();
        await Task.WhenAll(subscribeTask, updataTask);
    }


    private static async Task SubscribeToTickerUpdatesAsync()
    {
        Console.WriteLine("SubscribeToTickerUpdatesAsync() Func Starting...");
        BinanceRestClient client = new BinanceRestClient();
        var symbols = (await client.UsdFuturesApi.ExchangeData.GetBookPricesAsync())
            .Data
            .Where(x => x.Symbol.EndsWith("USDT")) // yalnızca USDT pariteleri
            .Select(x => x.Symbol)
            .ToList();

        int batchSize = 20;
        var symbolBatches = symbols.Select((symbol, index) => new { symbol, index })
                                   .GroupBy(x => x.index / batchSize)
                                   .Select(g => g.Select(x => x.symbol).ToList())
                                   .ToList();

        foreach (var batch in symbolBatches)
        {
            var tickerSubscriptionResult = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToMarkPriceUpdatesAsync(batch, null, async (update) =>
            {
                try
                {
                    decimal fundingRatePercentage = (decimal)(update.Data.FundingRate ?? 0) * 100;
                    var dateTime = update.Data.EventTime.ToString("yyyy-MM-dd HH:mm:ss");
                    var symbol = update.Data.Symbol;
                    var markPrice = update.Data.MarkPrice;
                    var negativeThreshold = GetNegativeThreshold();

                    await HandleFundingRateAsync(symbol, fundingRatePercentage, dateTime, rate => fundingRatePercentage <= negativeThreshold, markPrice);
                }
                catch (Exception ex)
                {
                    await SendTelegramMessage($"⚠️ Funding callback hatası: {ex.StackTrace}");
                }
            });

            if (!tickerSubscriptionResult.Success)
            {
                Console.WriteLine("❌ Bağlantı başarısız! Yeniden denemeye hazırlanıyor...");
                await SendTelegramMessage("❌ Bağlantı başarısız! Yeniden denemeye hazırlanıyor...");
            }
            else
            {
                tickerSubscriptionResult.Data.ConnectionLost += async () =>
                {
                    Console.WriteLine("🔌 Bağlantı kayboldu! Yeniden bağlanılıyor...");
                    await SendTelegramMessage("🔌 Bağlantı kayboldu! Yeniden bağlanılıyor...");
                };

                tickerSubscriptionResult.Data.ConnectionRestored += async (reconnectTime) =>
                {
                    Console.WriteLine($"🔁 Bağlantı geri geldi: {reconnectTime}");
                    await SendTelegramMessage($"🔁 Bağlantı geri geldi: {reconnectTime}");
                };
            }
        }
        await Task.Delay(-1); // sonsuz çalışması için
    }

    private static decimal GetNegativeThreshold()
    {
        return firstDestinition;
    }

    private static async Task HandleFundingRateAsync(string symbol, decimal fundingRatePercentage, string dateTime, Func<decimal, bool> condition, decimal price)
    {
        try
        {
            //Console.WriteLine($"Symbol: {symbol} | Funding Rate: {fundingRatePercentage} | Mark Price: {price}");

            if (condition(fundingRatePercentage))
            {
                if (nonTargetFundingRates.ContainsKey(symbol))
                {


                    TargetFundingRates[symbol] = new FundingRateRecord
                    {
                        Timestamp = DateTime.UtcNow,
                        Price = price,
                        Volume = 0,
                        OpenInterest = 0
                    };


                    var funding = await client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(
                        symbol: symbol,
                        startTime: DateTime.UtcNow.AddDays(-3),
                        endTime: DateTime.UtcNow,
                        limit: 100
                    );

                    if (funding.Success)
                    {
                        var estimatedAdjustedFundingFloor = funding.Data.Min(x => x.FundingRate);

                        if (fundingRatePercentage <= estimatedAdjustedFundingFloor * 0.95m)
                        {
                            await SendTelegramMessage($"""
                            🚨 *Short Squeeze Fırsatı Tespit Edildi!*

                            📌 *Symbol:* `{symbol}`
                            💰 *Fiyat:* {price:F4} USDT
                            📉 *Funding Rate:* {fundingRatePercentage:P4}
                            🔻 *Min Floor Değeri:* {estimatedAdjustedFundingFloor:P4}

                            📈 Funding rate kritik seviyeye yaklaştı ve squeeze ihtimali arttı. Yakından izlenmeli!
                            """);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Funding verisi alınamadı: " + funding.Error);
                    }



                    nonTargetFundingRates.TryRemove(symbol, out _);

                 

                }
                if (nonTargetFundingRates.ContainsKey(symbol) && TargetFundingRates.ContainsKey(symbol))
                {

                    nonTargetFundingRates.TryRemove(symbol, out _);

                }

                if (fundingRatePercentage <= secondDestinition &&
                    TargetFundingRates.ContainsKey(symbol) &&
                    isOrderActive == false
                    )
                {



                    await SendTelegramMessage("second");
                    TargetFundingRates.TryRemove(symbol, out _);
                }
            }
            else
            {
                if (!nonTargetFundingRates.ContainsKey(symbol))
                {
                    nonTargetFundingRates[symbol] = new FundingRateRecord();

                }
                else if (TargetFundingRates.ContainsKey(symbol))
                {
                    TargetFundingRates.TryRemove(symbol, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _ = SendTelegramMessage(ex.Message);
        }
    }
}

public class FundingRateRecord
{
    public DateTime Timestamp { get; set; }
    public decimal OpenInterest { get; set; }
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
}