using Binance.Net.Clients;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;

class Program
{
    private static readonly BinanceSocketClient socketClient = new BinanceSocketClient();
    private static List<FundingRateRecord> fundingRateRecords = new List<FundingRateRecord>();
    private static readonly string botToken = "7938765330:AAFC6-bpOiffLaa8iSQwpzl0h3FR_yYT4s4";
    private static readonly string chatId = "7250151162";



    static async Task Main(string[] args)
    {
        _ = SendTelegramMessage("Funding Rate Bot Starting...");




        await StartSubscription();
        Console.ReadKey();
    }

    private static async Task SendTelegramMessage(string message)
    {
        var botClient = new TelegramBotClient(botToken);
        await botClient.SendTextMessageAsync(chatId, message);
    }

    private static async Task StartSubscription()
    {
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
            var tickerSubscriptionResult = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToMarkPriceUpdatesAsync(batch, null, (update) =>
            {
                decimal fundingRatePercentage = (decimal)update.Data.FundingRate * 100;
                var dateTime = update.Data.EventTime.ToString("yyyy-MM-dd HH:mm:ss");
                var symbol = update.Data.Symbol;
                var markPrice = update.Data.MarkPrice;

                var negativeThreshold = GetNegativeThreshold();

                _ = HandleFundingRateAsync(symbol, fundingRatePercentage, dateTime, rate => fundingRatePercentage <= negativeThreshold, markPrice);
            });

            if (!tickerSubscriptionResult.Success)
            {
                Console.WriteLine("Bağlantı başarısız! Yeniden denemeye hazırlanıyor...");
                _ = SendTelegramMessage("Bağlantı başarısız! Yeniden denemeye hazırlanıyor...");
            }
            else
            {
                tickerSubscriptionResult.Data.ConnectionLost += async () =>
                {
                    Console.WriteLine("Bağlantı kayboldu! Yeniden bağlanılıyor...");
                    _ = SendTelegramMessage("Bağlantı kayboldu! Yeniden bağlanılıyor...");
                };

                tickerSubscriptionResult.Data.ConnectionRestored += (reconnectTime) =>
                {
                    Console.WriteLine($"Bağlantı geri geldi: {reconnectTime}");
                    _ = SendTelegramMessage($"Bağlantı geri geldi: {reconnectTime}");
                };
            }
        }

        await Task.Delay(-1);
    }

    private static decimal GetNegativeThreshold()
    {
        return -1m;
    }

    private static async Task HandleFundingRateAsync(string symbol, decimal fundingRatePercentage, string dateTime, Func<decimal, bool> condition, decimal price)
    {
        try
        {
            Console.WriteLine($"Symbol: {symbol} | Funding Rate: {fundingRatePercentage} | Mark Price: {price}");

            var existingRow = fundingRateRecords.Any(r => r.Symbol == symbol);

            if (condition(fundingRatePercentage))
            {
                if (!existingRow)
                {
                    var record = new FundingRateRecord
                    {
                        Timestamp = DateTime.Now,
                        FundingRate = fundingRatePercentage,
                        Price = price,
                        Symbol = symbol
                    };
                    fundingRateRecords.Add(record);

                    string message = $"📅 *Zaman:* `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`\n" +
                                     $"💰 *Fiyat:* `{price} USDT`\n" +
                                     $"🔄 *Funding Rate:* `{fundingRatePercentage:F4} %`\n" +
                                     $"🔹 *Sembol:* `{symbol}`";

                    await SendTelegramMessage(message);

           
                }
            }
            else
            {
                if (existingRow)
                {
                    fundingRateRecords.RemoveAll(r => r.Symbol == symbol);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Hata: {ex.Message}");
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
