using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.CommonObjects;
using CryptoExchange.Net.Sockets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;

class Program
{
    private static readonly BinanceSocketClient socketClient = new BinanceSocketClient();
    private static readonly BinanceRestClient client = new BinanceRestClient();
    private static List<FundingRateRecord> fundingRateRecords = new List<FundingRateRecord>();
    private static readonly string botToken = "7938765330:AAFC6-bpOiffLaa8iSQwpzl0h3FR_yYT4s4";
    private static readonly string chatId = "7250151162";
    private static Dictionary<string, DateTime> nonTargetFundingRates = new();
    private static Dictionary<string, DateTime> TargetFundingRates = new();
    private static Dictionary<string, DateTime> IntervalFundingRates = new();

    private static decimal firstDestinition = -1.5m;
    private static decimal secondDestinition = -2m;
    private static decimal speedTrashold = 1;
    private static int topGainerCount = 5;


    private static bool isOrderActive = false;
    private static List<(string Symbol, decimal Change)> topGainers = new();
    static async Task Main(string[] args)
    {
        _ = SendTelegramMessage("Console Uygulaması başlatıldı.");
        Console.WriteLine("Funding Rate Bot Starting...");
        await StartSubscription();
        Console.ReadKey();
    }
    private static decimal CalculateFundingRateSpeed(decimal oldFundingRate, DateTime oldTimestamp, decimal newFundingRate, DateTime newTimestamp)
    {
        // Funding rate farkını hesapla
        decimal rateDifference = Math.Abs(newFundingRate) - Math.Abs(oldFundingRate);

        // Geçen süreyi dakika cinsinden hesapla
        decimal elapsedMinutes = (decimal)(newTimestamp - oldTimestamp).TotalMinutes;

        // Süre sıfır veya negatifse sıfır dön
        if (elapsedMinutes <= 0)
        {
            return 0m;
        }

        // Funding rate hızını hesapla
        decimal rateSpeed = rateDifference / elapsedMinutes;

        return rateSpeed;
    }

    private static async Task SendTelegramMessage(string message)
    {
        var botClient = new TelegramBotClient(botToken);
        await botClient.SendTextMessageAsync(chatId, message);
    }

    private static async Task StartSubscription()
    {
        await startTicker();
        await SubscribeToTickerUpdatesAsync().ContinueWith(t =>
        {
            _ = SendTelegramMessage("Abonelik Başlatılamadı.");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task SubscribeToTickerUpdatesAsync()
    {
        BinanceRestClient client = new BinanceRestClient();
        var symbols = (await client.UsdFuturesApi.ExchangeData.GetBookPricesAsync())
            .Data
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
                decimal fundingRatePercentage = (decimal)(update.Data.FundingRate ?? 0) * 100;
                var dateTime = update.Data.EventTime.ToString("yyyy-MM-dd HH:mm:ss");
                var symbol = update.Data.Symbol;
                var markPrice = update.Data.MarkPrice;

                if ( topGainers.Any(x => x.Symbol.Equals(symbol)))
                {

                    DateTime nextFundingTime = update.Data.NextFundingTime;
                    TimeSpan timeRemaining = nextFundingTime - DateTime.UtcNow;

                    if (timeRemaining.TotalMinutes <= 30)
                    {
                        if (!IntervalFundingRates.ContainsKey(symbol))
                        {
                            IntervalFundingRates[symbol] = DateTime.Now;
                            await SendTelegramMessage($"Scalp geri çekilme fırsatı  - Symbol: {symbol} | Funding Rate: {fundingRatePercentage} | Mark Price: {update.Data.MarkPrice}");
                        }

                    }
                    else
                    {
                        if (IntervalFundingRates.ContainsKey(symbol))
                        {
                            IntervalFundingRates.Remove(symbol);
                        }
                    }
                }


                var negativeThreshold = GetNegativeThreshold();

                await HandleFundingRateAsync(symbol, fundingRatePercentage, dateTime, rate => fundingRatePercentage <= negativeThreshold, markPrice);
            });

            if (!tickerSubscriptionResult.Success)
            {
                Console.WriteLine("Bağlantı başarısız! Yeniden denemeye hazırlanıyor...");
                //_ = SendTelegramMessage("Bağlantı başarısız! Yeniden denemeye hazırlanıyor...");
            }
            else
            {
                tickerSubscriptionResult.Data.ConnectionLost +=  () =>
                {
                    Console.WriteLine("Bağlantı kayboldu! Yeniden bağlanılıyor...");
                    //_ = SendTelegramMessage("Bağlantı kayboldu! Yeniden bağlanılıyor...");
                };

                tickerSubscriptionResult.Data.ConnectionRestored += (reconnectTime) =>
                {
                    Console.WriteLine($"Bağlantı geri geldi: {reconnectTime}");
                    //_ = SendTelegramMessage($"Bağlantı geri geldi: {reconnectTime}");
                };
            }
        }

        await Task.Delay(-1);
    }
    private static async Task startTicker()
    {
        var tickerSubscriptionResult = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllTickerUpdatesAsync(update =>
        {
            var sorted = update.Data
               .OrderByDescending(t => t.PriceChangePercent) // Büyükten küçüğe sırala
               .Take(topGainerCount) // En büyük 5 tanesini al
               .Select(t => (t.Symbol, t.PriceChangePercent)) // Gerekli bilgiyi al
               .ToList();

            topGainers = sorted;

        });
 
    }
    static async Task order()
    {   
       //await SendTelegramMessage("dd");
    }

    private static decimal GetNegativeThreshold()
    {
        return firstDestinition;
    }
    private static async Task CheckVolumeAndMomentumWithFR(string symbol)
    {
        try
        {
            // Son 24 saatlik 1 saatlik mum verilerini çekiyoruz
            var klines = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneHour, limit: 24);

            // Son 1 saatin hacmi
            decimal lastVolume = klines.Data.Last().Volume;

            // Son 23 saatin ortalama hacmi
            decimal averageVolume = klines.Data.Take(23).Average(kline => kline.Volume);

            // Hacim kontrolü: 2 katına çıkmış mı?
            if (lastVolume > 2 * averageVolume)
            {
                Console.WriteLine("Hacim 2 katına çıkmış, işlem yapabilirsin.");

                await SendTelegramMessage($"Hacim 2 katına çıkmış, işlem yapabilirsin.  - Symbol: {symbol}");
            }
            else
            {
                Console.WriteLine("Hacim artmamış, işlem yapma.");
                await SendTelegramMessage($"Hacim artmamış, işlem yapma.  - Symbol: {symbol}");
            }

            // Son 5 dakikalık fiyat verisini alıyoruz
            var klines5Min = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneMinute, limit: 5);

            // Son 5 dakikalık açılış ve kapanış fiyatları
            decimal openPrice = klines5Min.Data.First().OpenPrice;
            decimal closePrice = klines5Min.Data.Last().ClosePrice;

            // Değişim yüzdesi hesaplama
            decimal changePercent = ((closePrice - openPrice) / openPrice) * 100;

            if (changePercent > -1)
            {
                Console.WriteLine("Momentum hala iyi, işlem yapılabilir.");
                await SendTelegramMessage($"Momentum hala iyi, işlem yapılabilir. - Symbol: {symbol}");
            }
            else
            {
                await SendTelegramMessage($"Momentum kötü. - Symbol: {symbol}");

            }

            // Funding rate kontrolü
            var fundingRates =  await client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(symbol);
            var latestFundingRate = fundingRates.Data.Last();

            // Funding rate -2 ve zamanın yarım saatten kısa olması durumunu kontrol et
            DateTime nextFundingTime = latestFundingRate.FundingTime;
            TimeSpan timeRemaining = nextFundingTime - DateTime.UtcNow;

            if (timeRemaining.TotalMinutes <= 30)
            {
                await SendTelegramMessage($"fr time yakın işlem yapma. - Symbol: {symbol}");
            }
            else
            {
                await SendTelegramMessage($"fr time güzel işlem yapabilirsin. - Symbol: {symbol}");
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine("Hata oluştu: " + ex.Message);
        }
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
                    TargetFundingRates[symbol] = DateTime.Now;
                    nonTargetFundingRates.Remove(symbol);
                  
                    await SendTelegramMessage($"firstDestinition geçildi  - Symbol: {symbol}");

                }
                if (nonTargetFundingRates.ContainsKey(symbol) && TargetFundingRates.ContainsKey(symbol))
                {
                     
                    nonTargetFundingRates.Remove(symbol);

                }

                if (fundingRatePercentage <= secondDestinition && 
                    TargetFundingRates.ContainsKey(symbol) &&
                    //topGainers.Any(x => x.Symbol.Equals(symbol)) &&
                    isOrderActive == false
                    )
                {

                    var bulunan = topGainers.FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                    // Change değerini kontrol edip, formatlıyoruz
                    string changeText = !string.IsNullOrEmpty(bulunan.Symbol)
                        ? bulunan.Change.ToString("0.00") + "%"
                        : "Bulunamadı";

                    // Mesajı göndermekte kullanıyoruz:
                    await SendTelegramMessage($"second geçildi  - Symbol: {symbol} | Funding Rate: {fundingRatePercentage} | Mark Price: {price} | Change: {changeText}");
                    await CheckVolumeAndMomentumWithFR(symbol);
                    TargetFundingRates.Remove(symbol);

                }
            }
            else
            {
                if (!nonTargetFundingRates.ContainsKey(symbol))
                {
                    nonTargetFundingRates[symbol] = DateTime.Now;
                   
                }
                else if (TargetFundingRates.ContainsKey(symbol))
                {
                    TargetFundingRates.Remove(symbol);
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
    public decimal FundingRate { get; set; }
    public decimal Price { get; set; }
    public required string Symbol { get; set; }
}