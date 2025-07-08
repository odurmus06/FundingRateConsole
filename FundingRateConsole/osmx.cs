using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace premiumIndexBot
{

    public partial class main : Form
    {
        private readonly HttpClient httpClient = new HttpClient();
        public main()
        {
            InitializeComponent();
            dataGridView1.AutoGenerateColumns = true;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
        }
        private async Task<List<PremiumIndex>> FetchPremiumIndexDataAsync()
        {
            try
            {
                string url = "https://fapi.binance.com/fapi/v1/premiumIndex";
                var response = await httpClient.GetStringAsync(url);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var data = JsonSerializer.Deserialize<List<PremiumIndex>>(response, options);

                return data;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Hata: " + ex.Message);
                return new List<PremiumIndex>();
            }
        }
        private async void button1_Click(object sender, EventArgs e)
        {
            await load();
        }
        async Task load()
        {
            var allData = await FetchPremiumIndexDataAsync();

            var longSignals = new List<PremiumIndex>();
            var shortSignals = new List<PremiumIndex>();

            foreach (var item in allData)
            {

                if (decimal.TryParse(item.EstimatedSettlePrice, out var estimated) &&
                    decimal.TryParse(item.MarkPrice, out var mark) &&
                    decimal.TryParse(item.IndexPrice, out var index) &&
                    decimal.TryParse(item.LastFundingRate, out var funding))
                {
                    if (estimated == 0 || mark == 0 || index == 0) continue; // ⛔ 0 değerli satırları atla
                    if (estimated > mark && mark > index)
                        longSignals.Add(item);
                    else if ((estimated < mark && mark < index) && funding < 0)
                        shortSignals.Add(item);
                }
            }

            // Grupları estimated vs mark % farkına göre sırala
            longSignals = longSignals.OrderByDescending(x => x.EstimatedVsMarkPct).ToList();
            shortSignals = shortSignals.OrderBy(x => x.EstimatedVsMarkPct).ToList();

            // Grupları birleştir
            var combined = longSignals.Concat(shortSignals).ToList();

            dataGridView1.DataSource = shortSignals;

            // Sadece istenen kolonları göster
            foreach (DataGridViewColumn col in dataGridView1.Columns)
            {
                col.Visible = false;
            }

            dataGridView1.Columns[nameof(PremiumIndex.Symbol)].Visible = true;
            dataGridView1.Columns[nameof(PremiumIndex.MarkPrice)].Visible = true;
            dataGridView1.Columns[nameof(PremiumIndex.IndexPrice)].Visible = true;
            dataGridView1.Columns[nameof(PremiumIndex.EstimatedSettlePrice)].Visible = true;
            dataGridView1.Columns[nameof(PremiumIndex.EstimatedVsMarkPct)].Visible = true;
            dataGridView1.Columns[nameof(PremiumIndex.ServerTime)].Visible = true;

            // Kolon başlıklarını düzenle (isteğe bağlı)
            dataGridView1.Columns[nameof(PremiumIndex.EstimatedVsMarkPct)].HeaderText = "% Fark";
            dataGridView1.Columns[nameof(PremiumIndex.ServerTime)].HeaderText = "Zaman";
            dataGridView1.Columns[nameof(PremiumIndex.EstimatedVsMarkPct)].DefaultCellStyle.Format = "0.00'%'";
            // Renklendirme
            foreach (DataGridViewRow row in dataGridView1.Rows)
            {
                if (row.DataBoundItem is PremiumIndex item &&
                    decimal.TryParse(item.EstimatedSettlePrice, out var estimated) &&
                    decimal.TryParse(item.MarkPrice, out var mark) &&
                    decimal.TryParse(item.IndexPrice, out var index) &&
                    decimal.TryParse(item.LastFundingRate, out var funding))
                {
                    if (estimated > mark && mark > index)
                        row.DefaultCellStyle.BackColor = Color.LightGreen;
                    else if ((estimated < mark && mark < index) && funding < 0)
                        row.DefaultCellStyle.BackColor = Color.LightCoral;
                }
            }
        }

        private void chk_interval_CheckedChanged(object sender, EventArgs e)
        {
            if (chk_interval.Checked)
            {
                if (int.TryParse(txt_interval.Text, out int seconds) && seconds > 0)
                {
                    timer_interval.Interval = seconds * 1000; // saniyeyi milisaniyeye çevir
                    timer_interval.Start();
                }
                else
                {
                    MessageBox.Show("Lütfen geçerli bir saniye değeri girin.");
                    chk_interval.Checked = false;
                }
            }
            else
            {
                timer_interval.Stop();
            }
        }

        private void timer_interval_Tick(object sender, EventArgs e)
        {
            _ = load();
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

