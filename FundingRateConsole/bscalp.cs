using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace bscalp_clean
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();

        // 🌍 Global minimum bilgisi
        private static GlobalMinimum globalMin = null;

        static async Task Main(string[] args)
        {
            while (true)
            {
                await LoadAsync();
                await Task.Delay(1000); // Her saniyede bir yenile
            }
        }

        private static async Task LoadAsync()
        {
            var allData = await FetchPremiumIndexDataAsync();

            var shortSignals = new List<PremiumIndex>();

            foreach (var item in allData)
            {
                if (decimal.TryParse(item.EstimatedSettlePrice, out var estimated) &&
                    decimal.TryParse(item.MarkPrice, out var mark) &&
                    decimal.TryParse(item.IndexPrice, out var index) &&
                    decimal.TryParse(item.LastFundingRate, out var funding))
                {
                    if (estimated == 0 || mark == 0 || index == 0) continue;

                    if ((estimated < mark && mark < index) && funding < 0)
                        shortSignals.Add(item);
                }
            }

            var filtered = shortSignals
                .Where(x => x.EstimatedVsMarkPct < -0.9m)
                .OrderBy(x => x.EstimatedVsMarkPct)
                .ToList();

            Console.Clear();
            Console.WriteLine($"Short sinyalleri < -%0.9  ({DateTime.Now:T})");

            if (filtered.Count == 0)
            {
                Console.WriteLine("Uygun sinyal bulunamadı.");
            }
            else
            {
                foreach (var signal in filtered)
                {
                    Console.WriteLine($"Symbol: {signal.Symbol,-12} EstimatedVsMarkPct: {signal.EstimatedVsMarkPct:F2}%");

                    // 🔻 Global minimum kontrolü
                    if (globalMin == null || signal.EstimatedVsMarkPct < globalMin.EstimatedVsMarkPct)
                    {
                        globalMin = new GlobalMinimum
                        {
                            Symbol = signal.Symbol,
                            EstimatedVsMarkPct = signal.EstimatedVsMarkPct,
                            MarkPrice = decimal.TryParse(signal.MarkPrice, out var mp) ? mp : 0,
                            Timestamp = DateTime.Now
                        };
                    }
                }
            }

            // 🌍 Global minimum bilgisi
            if (globalMin != null)
            {
                Console.WriteLine("");
                Console.WriteLine("");
                Console.WriteLine($"En düşük global EstimatedVsMarkPct:");
                Console.WriteLine($"   Symbol     : {globalMin.Symbol}");
                Console.WriteLine($"   Value      : {globalMin.EstimatedVsMarkPct:F2}%");
                Console.WriteLine($"   Mark Price : {globalMin.MarkPrice:F4}");
                Console.WriteLine($"   Updated At : {globalMin.Timestamp:T}");
            }
        }

        private static async Task<List<PremiumIndex>> FetchPremiumIndexDataAsync()
        {
            try
            {
                string url = "https://fapi.binance.com/fapi/v1/premiumIndex";
                var response = await httpClient.GetStringAsync(url);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var data = JsonSerializer.Deserialize<List<PremiumIndex>>(response, options);

                return data ?? new List<PremiumIndex>();
            }
            catch
            {
                return new List<PremiumIndex>();
            }
        }
    }

    public class PremiumIndex
    {
        public string Symbol { get; set; }
        public string MarkPrice { get; set; }
        public string IndexPrice { get; set; }
        public string EstimatedSettlePrice { get; set; }
        public string LastFundingRate { get; set; }
        public long Time { get; set; }

        public decimal EstimatedVsMarkPct
        {
            get
            {
                if (decimal.TryParse(EstimatedSettlePrice, out var est) &&
                    decimal.TryParse(MarkPrice, out var mark) && mark != 0)
                {
                    return (est - mark) / mark * 100;
                }
                return 0;
            }
        }

        public DateTime ServerTime => DateTimeOffset.FromUnixTimeMilliseconds(Time).LocalDateTime;
    }

    public class GlobalMinimum
    {
        public string Symbol { get; set; }
        public decimal EstimatedVsMarkPct { get; set; }
        public decimal MarkPrice { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
