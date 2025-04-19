using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.CommonObjects;
using CryptoExchange.Net.Sockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;

class Program
{
    // Binance API ve Socket Client
    private static readonly BinanceSocketClient socketClient = new BinanceSocketClient();
    private static readonly BinanceRestClient client = new BinanceRestClient();

    // Funding Rate Verisi ve Koleksiyonlar
    private static List<FundingRateRecord> fundingRateRecords = new List<FundingRateRecord>();
    private static ConcurrentDictionary<string, DateTime> nonTargetFundingRates = new();
    private static ConcurrentDictionary<string, DateTime> TargetFundingRates = new();
    private static ConcurrentDictionary<string, DateTime> IntervalFundingRates = new();
    private static ConcurrentDictionary<string, DateTime> NegativeTwoFundingRates = new();
    // Telegram Bot Bilgileri
    private static readonly string botToken = "7938765330:AAFC6-bpOiffLaa8iSQwpzl0h3FR_yYT4s4";
    private static readonly string chatId = "7250151162";

    // Hedef Değerler ve Eşikler
    private static decimal firstDestinition = -1.5m;
    private static decimal secondDestinition = -2m;
    private static decimal speedTrashold = 1;

    // Top Gainers
    private static List<(string Symbol, decimal Change)> topGainers = new();
    private const int topGainerCount = 5;
    private const decimal minimumVolume = 10_000_000;

    // Order Durumu
    private static bool isOrderActive = false;

    // Diğer Değişkenler
    private static readonly object locker = new();

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
        //await PrintTopGainersLoop();
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
                try
                {
                    decimal fundingRatePercentage = (decimal)(update.Data.FundingRate ?? 0) * 100;
                    var dateTime = update.Data.EventTime.ToString("yyyy-MM-dd HH:mm:ss");
                    var symbol = update.Data.Symbol;
                    var markPrice = update.Data.MarkPrice;

                    if (fundingRatePercentage <= secondDestinition)
                    {
                        if (!NegativeTwoFundingRates.ContainsKey(symbol))
                        {
                            NegativeTwoFundingRates[symbol] = DateTime.Now;
                            await SendTelegramMessage($"-2 geçildi  - Symbol: {symbol}");
                        }
                    }
                    else
                    {
                        if (NegativeTwoFundingRates.ContainsKey(symbol))
                        {
                            NegativeTwoFundingRates.TryRemove(symbol, out _);
                            await SendTelegramMessage($"-2 short fırsatı - Symbol: {symbol}");
                        }
                    }

                        if (topGainers.Any(x => x.Symbol.Equals(symbol)))
                    {
                        DateTime nextFundingTime = update.Data.NextFundingTime;
                        TimeSpan timeRemaining = nextFundingTime - DateTime.UtcNow;

                        if (timeRemaining.TotalMinutes <= 30 && update.Data.FundingRate < 0)
                        {
                            if (!IntervalFundingRates.ContainsKey(symbol))
                            {
                                // Güvenli ekleme
                                IntervalFundingRates.TryAdd(symbol, DateTime.Now);

                                string message = $"⚡ Scalp geri çekilme fırsatı\n\n" +
                                                 $"Symbol: {symbol}\n" +
                                                 $"Funding Rate: %{fundingRatePercentage:F4}\n" +
                                                 $"Mark Price: {markPrice}";

                                if (topGainers.Any())
                                {
                                    message += "\n\n📈 Top Gainers:\n";
                                    foreach (var gainer in topGainers)
                                    {
                                        message += $"- {gainer.Symbol}: %{gainer.Change}\n";
                                    }
                                }

                                await SendTelegramMessage(message);
                            }
                        }
                        else
                        {
                            if (IntervalFundingRates.ContainsKey(symbol))
                            {
                                IntervalFundingRates.TryRemove(symbol, out _);
                            }
                        }
                    }

                    var negativeThreshold = GetNegativeThreshold();

                    await HandleFundingRateAsync(symbol, fundingRatePercentage, dateTime, rate => fundingRatePercentage <= negativeThreshold, markPrice);
                }
                catch (Exception ex)
                {
                    await SendTelegramMessage($"⚠️ Funding callback hatası: {ex.Message}");
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

    private static async Task PrintTopGainersLoop()
    {
        while (true)
        {
            lock (locker)
            {
                Console.Clear();
                Console.WriteLine($"Top Gainers - {DateTime.Now:HH:mm:ss}");
                Console.WriteLine(new string('-', 30));

                foreach (var (symbol, percent) in topGainers)
                    Console.WriteLine($"{symbol,-10} | {percent,6:F2} %");
            }

            await Task.Delay(1000);
        }
    }
    private static async Task startTicker()
    {
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
                 _= SendTelegramMessage($"Ticker güncelleme hatası: {ex.Message}");
            }
        });

        if (!tickerSubscriptionResult.Success)
        {
            Console.WriteLine($"Ticker aboneliği başarısız: {tickerSubscriptionResult.Error}");

            _ = SendTelegramMessage($"Ticker aboneliği başarısız: {tickerSubscriptionResult.Error}");

        }
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

            // Hacim kontrolü
            bool isVolumeDoubled = lastVolume > 2 * averageVolume;

            // Son 5 dakikalık fiyat verisini al
            var klines5Min = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneMinute, limit: 5);

            decimal openPrice = klines5Min.Data.First().OpenPrice;
            decimal closePrice = klines5Min.Data.Last().ClosePrice;
            decimal changePercent = ((closePrice - openPrice) / openPrice) * 100;
            bool isMomentumGood = changePercent > -1;

            // Funding rate kontrolü

            DateTime nextFundingTime = client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol).Result.Data.NextFundingTime;
            TimeSpan timeRemaining = nextFundingTime - DateTime.Now;
            bool isFundingTimeNear = timeRemaining.TotalMinutes <= 30;

            // Mesaj oluştur
            string message = $"📊 *Long Analizi - {symbol}*\n\n";

            // Hacim
            message += $"💰 *Hacim*: Son saat hacmi: `{lastVolume:N2}`, Ortalama (23 saat): `{averageVolume:N2}`\n";
            message += isVolumeDoubled
                ? "✅ *Hacim 2 katına çıkmış, işlem yapılabilir.*\n"
                : "⚠️ *Hacim artmamış, işlem yapılmamalı.*\n";

            // Momentum
            message += $"\n📈 *Momentum (Son 5 dakika)*: %{changePercent:F2}\n";
            message += isMomentumGood
                ? "✅ *Momentum hala iyi, işlem yapılabilir.*\n"
                : "⚠️ *Momentum zayıf.*\n";

            // Funding Rate
            message += $"\n🕒 *Funding Rate Zamanı*: {nextFundingTime:HH:mm}\n";
            message += isFundingTimeNear
                ? "⚠️ *Funding time çok yakın, işlem yapma.*\n"
                : "✅ *Funding time uygun, işlem yapılabilir.*\n";

            await SendTelegramMessage(message);

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
                    nonTargetFundingRates.TryRemove(symbol, out _);

                    await SendTelegramMessage($"firstDestinition geçildi  - Symbol: {symbol}");

                }
                if (nonTargetFundingRates.ContainsKey(symbol) && TargetFundingRates.ContainsKey(symbol))
                {
                     
                    nonTargetFundingRates.TryRemove(symbol, out _);

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
                    TargetFundingRates.TryRemove(symbol, out _);

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
    public decimal FundingRate { get; set; }
    public decimal Price { get; set; }
    public required string Symbol { get; set; }
}