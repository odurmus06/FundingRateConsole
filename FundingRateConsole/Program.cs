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
    private static decimal firstDestinition = -1.5m;
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

    private static void SpotOnTickerUpdate(DataEvent<IEnumerable<IBinanceTick>> obj)
    {
        foreach (var ticker in obj.Data)
        {
            string symbol = ticker.Symbol;

            //Console.WriteLine($"symbol: {symbol} tarih : {DateTime.Now.ToString("HH:mm:ss")}");


            if (!spotKnownSymbols.Contains(symbol) && symbol.EndsWith("USDT"))
            {
                spotKnownSymbols.Add(symbol);

                _ = SendTelegramMessage($"""
                🆕 *Spot Yeni Coin Listelendi!*

                📈 Sembol: `{symbol}`
                📅 Tarih: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
                🔁 Binance USDT Futures'ta listelendi.

                🚀 İlk fırsatlar için dikkatli ol!

                #Binance #NewListing
                """);
            }
        }
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
        var tickerTask = startTicker();
        var spotStreamTask = socketClient.SpotApi.ExchangeData.SubscribeToAllTickerUpdatesAsync(SpotOnTickerUpdate);
        var subscribeTask = SubscribeToTickerUpdatesAsync();
        var updataTask = updated();
        var futuresStreamTask = socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllTickerUpdatesAsync(FuturesOnTickerUpdate);

        await Task.WhenAll(tickerTask, spotStreamTask, subscribeTask, updataTask, futuresStreamTask);
    }

    private static void FuturesOnTickerUpdate(DataEvent<IEnumerable<IBinance24HPrice>> obj)
    {
        foreach (var ticker in obj.Data)
        {
            string symbol = ticker.Symbol;

            //Console.WriteLine($"symbol: {symbol} tarih : {DateTime.Now.ToString("HH:mm:ss")}");


            if (!futuresKnownSymbols.Contains(symbol) && symbol.EndsWith("USDT"))
            {
                futuresKnownSymbols.Add(symbol);

                _ = SendTelegramMessage($"""
                🆕 *Futures Yeni Coin Listelendi!*

                📈 Sembol: `{symbol}`
                📅 Tarih: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
                🔁 Binance USDT Futures'ta listelendi.

                🚀 İlk fırsatlar için dikkatli ol!

                #Binance #NewListing
                """);
            }
        }
    }

    public static async Task<bool> HacimArtisiVarMi(string symbol)
    {
        try
        {
            var klinesResult = await client.UsdFuturesApi.ExchangeData
                .GetKlinesAsync(symbol, KlineInterval.OneMinute, limit: 10);

            if (!klinesResult.Success || klinesResult.Data.Count() < 10)
            {
                return false;
            }

            var klines = klinesResult.Data.ToList();
            var onceki5Hacim = klines.Take(5).Sum(k => k.Volume);
            var son5Hacim = klines.Skip(5).Sum(k => k.Volume);

            bool hacimArtisi = son5Hacim > 1.5m * onceki5Hacim;

            return hacimArtisi;
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    public static async Task<bool> SpreadVeLikiditeUygunMu(string symbol)
    {
        try
        {
            var orderBookResult = await client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, 5);

            if (!orderBookResult.Success || orderBookResult.Data.Bids.Count() == 0 || orderBookResult.Data.Asks.Count() == 0)
            {
                return false;
            }
            var bid = orderBookResult.Data.Bids.First(); // En yüksek alış
            var ask = orderBookResult.Data.Asks.First(); // En düşük satış
            decimal spread = ask.Price - bid.Price;
            decimal spreadYuzdesi = spread / ((ask.Price + bid.Price) / 2m) * 100m;
            bool spreadUygun = spreadYuzdesi <= 0.2m; // %0.2 altı spread kabul
            bool likiditeYeterli = bid.Quantity >= 500m || ask.Quantity >= 500m;
            return spreadUygun && likiditeYeterli;
        }
        catch (Exception ex)
        {
            return false;
        }
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



                    //DateTime nextFundingTime = update.Data.NextFundingTime;
                    //TimeSpan timeRemaining = nextFundingTime - DateTime.UtcNow;

                    //if (timeRemaining.TotalMinutes <= 2 && fundingRatePercentage < 0)
                    //{
                    //    if (!IntervalFundingRates.ContainsKey(symbol))
                    //    {
                    //        IntervalFundingRates[symbol] = DateTime.Now;

                    //    string message = $"📉 Scalp Geri Çekilme Fırsatı\n" +
                    //           $"🔹 Symbol: {symbol}\n" +
                    //           $"🔹 Funding Rate: {fundingRatePercentage}\n" +
                    //           $"🔹 Mark Price: {update.Data.MarkPrice:F4}\n";

                    //    await SendTelegramMessage(message);
                    //}
                    //}
                    //else
                    //{
                    //    if (IntervalFundingRates.ContainsKey(symbol))
                    //    {
                    //        IntervalFundingRates.TryRemove(symbol, out _);
                    //    }
                    //}


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

    private static async Task startTicker()
    {
        Console.WriteLine("startTicker() Func Starting...");
        var tickerSubscriptionResult = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllTickerUpdatesAsync(update =>
        {
            try
            {
                lock (locker)
                {
                   
                    foreach (var ticker in update.Data)
                    {
                        if (ticker.QuoteVolume < minimumVolume || string.IsNullOrEmpty(ticker.Symbol))
                            continue;

                        var existingIndex = topGainers.FindIndex(x => x.Symbol == ticker.Symbol);
                        var change = ticker.PriceChangePercent;

                        if (existingIndex != -1)
                        {
                            // Güncelle
                            topGainers[existingIndex] = (ticker.Symbol, change);
                        }
                        else
                        {
                            // Eğer yer varsa ekle
                            if (topGainers.Count < topGainerCount)
                            {
                                topGainers.Add((ticker.Symbol, change));
                            }
                            else
                            {
                                // En düşük değişim varsa karşılaştır ve gerekiyorsa değiştir
                                var minChange = topGainers.Min(x => x.Change);
                                if (change > minChange)
                                {
                                    var minIndex = topGainers.FindIndex(x => x.Change == minChange);
                                    topGainers[minIndex] = (ticker.Symbol, change);
                                }
                            }
                        }

                        // Listeyi her defasında sırala
                        topGainers = topGainers
                            .OrderByDescending(x => x.Change)
                            .Take(topGainerCount)
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _ = SendTelegramMessage($"Ticker güncelleme hatası: {ex.Message}");
            }
        });

        if (!tickerSubscriptionResult.Success)
        {
            Console.WriteLine($"Ticker aboneliği başarısız: {tickerSubscriptionResult.Error}");

            _ = SendTelegramMessage($"Ticker aboneliği başarısız: {tickerSubscriptionResult.Error}");

        }
    }

    private static decimal GetNegativeThreshold()
    {
        return firstDestinition;
    }


    private static async Task CheckVolumeAndMomentumWithFR(string symbol, decimal currentPrice, decimal prevPrice, DateTime prevDate)
    {
        try
        {
            // Son 24 saatlik 1 saatlik mum verilerini çekiyoruz
            var klines = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneHour, limit: 24);
            // Son 1 saatin hacmi
            decimal lastVolume = klines.Data.Last().Volume;
            // Son 23 saatin ortalama hacmi
            decimal averageVolume = klines.Data.Take(23).Average(kline => kline.Volume);
            // Hacim kontrolü
            bool isVolumeDoubled = lastVolume > (1.8m * averageVolume);
            bool isVolumeBelowAverage = lastVolume < averageVolume;

            ////////////////////////////////////////////////////////////////7

            var last4Candles = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneHour, limit: 4);
            bool isStrongUptrend = last4Candles.Data.All(c => c.ClosePrice > c.OpenPrice);

            // Funding rate kontrolü
            DateTime nextFundingTime = client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol).Result.Data.NextFundingTime;
            TimeSpan timeRemaining = nextFundingTime - DateTime.UtcNow;
            bool isFundingTimeNear = timeRemaining.TotalMinutes >= 30;

            var BuyVolumeRatio = await GetBuyVolumeRatioFuturesAsync(symbol);
            bool isBuyVolumeRatioBigger = BuyVolumeRatio >= 0.65m;

            var oiHistory = await client.UsdFuturesApi.ExchangeData.GetOpenInterestHistoryAsync(symbol, Binance.Net.Enums.PeriodInterval.OneHour, limit: 2);

            decimal currentOI = oiHistory.Data.Last().SumOpenInterest;
            decimal previousOI = oiHistory.Data.First().SumOpenInterest;
            decimal oiChangePercent = (currentOI - previousOI) / previousOI * 100;
            bool isOIIncreasing = oiChangePercent >= 10;



            decimal changePercent = ((currentPrice - prevPrice) / prevPrice) * 100;
            bool isPriceChangeBigger = changePercent >= 5;



            // Mesaj oluştur
            string message = $"📊 *Long Analizi - {symbol}*\n\n";

            // Hacim
            message += $"💰 *Hacim*: Son saat hacmi: `{lastVolume:N2}`, Ortalama (23 saat): `{averageVolume:N2}`\n";
            message += isVolumeDoubled
                ? "✅ *Hacim 2 katına çıkmış, işlem yapılabilir.*\n"
                : "⚠️ *Hacim artmamış, işlem yapılmamalı.*\n";

            message += $"💰 *Fiyat Değişimi*: Önceki fiyat: `{prevPrice:N4}`, Şu anki fiyat: `{currentPrice:N4}`, Değişim: `{changePercent:N2}%`\n";
            message += isPriceChangeBigger
                ? $"✅ *Fiyat %{changePercent}'dan fazla artmış, güçlü hareket olabilir.*\n"
                : $"⚠️ *Fiyat artışı %{changePercent}'dan az, dikkatli olunmalı.*\n";

            // Momentum
            message += $"\n📈 *Momentum (Son 5 dakika)*\n";
            message += isStrongUptrend
                ? "✅ *Momentum hala iyi, işlem yapılabilir.*\n"
                : "⚠️ *Momentum zayıf.*\n";

            // Buyer
            message += $"\n📈 *Last 500 Trades*: %{BuyVolumeRatio:F2}\n";
            message += isBuyVolumeRatioBigger
                ? "✅ *Piyasada alıcılar iyi, işlem yapılabilir.*\n"
                : "⚠️ *Piyasada alıcılar zayıf.*\n";

            // Buyer
            message += $"\n📈 *Open Interest Değişimi*: %{oiChangePercent:F2}\n";
            message += isOIIncreasing
                ? "✅ *Open Interest %10 artmış, işlem yapılabilir.*\n"
                : "⚠️ *Open Interest artışı zayıf.*\n";

            // Funding Rate
            message += $"\n🕒 *Funding Rate Zamanı*: {timeRemaining.Hours} saat {timeRemaining.Minutes} dakika\n";
            message += isFundingTimeNear
                ? "✅ *Funding time uygun, işlem yapılabilir.*\n"
                : "⚠️ *Funding time çok yakın, işlem yapma.*\n";




            if (isStrongUptrend)
            {
                isOrderActive = true;
                await PlaceOrderAsync(symbol);
                message += $"\n📈 *İşleme girildi* \n";
            }
            else
            {
                message += $"\n📉 *İşleme girilmedi* \n";
            }

            _ = SendTelegramMessage(message);

        }
        catch (Exception ex)
        {
            Console.WriteLine("Hata oluştu: " + ex.Message);
            _ = SendTelegramMessage(ex.Message);

        }
    }

    private static async Task<decimal> GetBuyVolumeRatioFuturesAsync(string symbol, int limit = 500)
    {
        var result = await client.UsdFuturesApi.ExchangeData.GetAggregatedTradeHistoryAsync(symbol, limit: limit);

        if (!result.Success)
        {
            Console.WriteLine($"Futures işlem verisi alınamadı: {result.Error}");
            return 0;
        }

        decimal buyVolume = 0;
        decimal sellVolume = 0;

        foreach (var trade in result.Data)
        {
            if (trade.BuyerIsMaker)
                sellVolume += trade.Quantity;
            else
                buyVolume += trade.Quantity;
        }

        decimal totalVolume = buyVolume + sellVolume;
        if (totalVolume == 0)
            return 0;

        return buyVolume / totalVolume;
    }

    private static async Task<bool> IsLongVolumeDominant(string symbol)
    {
        var tradesResult = await client.UsdFuturesApi.ExchangeData.GetTradeHistoryAsync(symbol, limit: 1000);

        if (!tradesResult.Success)
            throw new Exception("Trade verileri alınamadı: " + tradesResult.Error);

        var trades = tradesResult.Data;

        decimal buyVolume = trades
            .Where(t => !t.BuyerIsMaker)
            .Sum(t => t.QuoteQuantity);

        decimal sellVolume = trades
            .Where(t => t.BuyerIsMaker)
            .Sum(t => t.QuoteQuantity);

        return buyVolume > sellVolume * 1.2m;
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
                    var oi = await client.UsdFuturesApi.ExchangeData.GetOpenInterestAsync(symbol);
                    var ticker = await client.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);

                    var openInterest = oi.Data?.OpenInterest ?? 0;
                    var volume = ticker.Data?.QuoteVolume ?? 0;

                    TargetFundingRates[symbol] = new FundingRateRecord
                    {
                        Timestamp = DateTime.UtcNow,
                        Price = price,
                        Volume = volume,
                        OpenInterest = openInterest
                    };

                    nonTargetFundingRates.TryRemove(symbol, out _);

                    await SendTelegramMessage($"firstDestinition geçildi  - Symbol: {symbol}");

                }
                if (nonTargetFundingRates.ContainsKey(symbol) && TargetFundingRates.ContainsKey(symbol))
                {

                    nonTargetFundingRates.TryRemove(symbol, out _);

                }

                if (fundingRatePercentage <= secondDestinition &&
                    TargetFundingRates.ContainsKey(symbol) &&
                    topGainers.Any(x => x.Symbol.Equals(symbol)) &&
                    isOrderActive == false
                    )
                {

                    var bulunan = topGainers.FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                    // Change değerini kontrol edip, formatlıyoruz
                    string changeText = !string.IsNullOrEmpty(bulunan.Symbol)
                        ? bulunan.Change.ToString("0.00") + "%"
                        : "Bulunamadı";

                    // Mesajı göndermekte kullanıyoruz:
                    var prev = TargetFundingRates[symbol];

                    var oi = await client.UsdFuturesApi.ExchangeData.GetOpenInterestAsync(symbol);
                    var ticker = await client.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);

                    var openInterest = oi.Data?.OpenInterest ?? 0;
                    var volume = ticker.Data?.QuoteVolume ?? 0;

                    decimal priceChange = ((price - prev.Price) / prev.Price) * 100;
                    decimal oiChange = ((openInterest - prev.OpenInterest) / prev.OpenInterest) * 100;
                    decimal volumeChange = ((volume - prev.Volume) / prev.Volume) * 100;

                    // Sinyal yorumu belirleme
                    string yorum;

                    if (priceChange > 0 && oiChange > 0 && volumeChange > 20 && IsLongVolumeDominant(symbol).Result)
                    {
                        yorum = "🔥 Güçlü long sinyali! \n";
                        isOrderActive = true;
                        await PlaceOrderAsync(symbol);
                        yorum += $"\n📈 *İşleme girildi* \n";
                    }
                    else if (priceChange < 0 && oiChange > 0)
                    {
                        yorum = "⚠️ Fiyat düşse de OI artıyor → squeeze olabilir! \n";
                        yorum += $"\n📉 *İşleme girilmedi* \n";
                    }
                    else
                    {
                        yorum = "📉 Sinyal zayıf, işlem yapılmayabilir. \n";
                        yorum += $"\n📉 *İşleme girilmedi* \n";
                    }

                    // Telegram mesajı
                    string telegramMessage = $@"
                        📊 Funding Analizi - {symbol}

                        🕒 Aralık: -1.5% ➜ -2%
                        💰 Fiyat: {price}  
                        📈 Price Change: {priceChange:F2}%
                        📊 OI Change: {oiChange:F2}%
                        🔄 Volume Change: {volumeChange:F2}%

                        📌 Yorum: {yorum}
                        ";

                    await SendTelegramMessage(telegramMessage);
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