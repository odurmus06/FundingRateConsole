using LiteDB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class LiteDbManager
{
    private static readonly object _lock = new object();
    private static string _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FundingRateBot", "fundingRates.db");
    private static LiteDbManager _instance;
    private LiteDatabase _db;
    private ILiteCollection<FundingRateStruct> _fundingRates;

    private LiteDbManager()
    {
        try
        {
            string directoryPath = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            _db = new LiteDatabase(_dbPath);
            _fundingRates = _db.GetCollection<FundingRateStruct>("fundingrates");
            _fundingRates.EnsureIndex(x => x.Symbol);
        }
        catch (LiteException)
        {
            // 📌 Eğer veritabanı bozulduysa onar
            _db = new LiteDatabase(_dbPath);
            _db.Rebuild();
            Console.WriteLine("✅ Veritabanı onarıldı.");
            _fundingRates = _db.GetCollection<FundingRateStruct>("fundingrates");
        }
    }

    public static LiteDbManager Instance
    {
        get
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new LiteDbManager();
                }
                return _instance;
            }
        }
    }

    // 📌 Yeni veri sadece değişiklik varsa eklenir
    public void AddFundingRateIfChanged(FundingRateStruct newRate)
    {
        lock (_lock)
        {
            var lastRate = _fundingRates.Find(x => x.Symbol == newRate.Symbol)
                                        .OrderByDescending(x => x.Date)
                                        .FirstOrDefault();

            if (lastRate == null || lastRate.FundingRateValue != newRate.FundingRateValue || lastRate.Price != newRate.Price)
            {
                _fundingRates.Insert(newRate);
                Console.WriteLine($"✅ Yeni veri eklendi: {newRate.Symbol}, {newRate.FundingRateValue}, {newRate.Price}");
            }
        }
    }

    public List<FundingRateStruct> GetAllFundingRates()
    {
        lock (_lock)
        {
            return _fundingRates.FindAll().ToList();
        }
    }

    public FundingRateStruct GetFundingRate(string symbol)
    {
        lock (_lock)
        {
            return _fundingRates.FindOne(x => x.Symbol == symbol);
        }
    }

    public bool UpdateFundingRate(FundingRateStruct rate)
    {
        lock (_lock)
        {
            return _fundingRates.Update(rate);
        }
    }

    public bool DeleteFundingRate(string symbol)
    {
        lock (_lock)
        {
            return _fundingRates.DeleteMany(x => x.Symbol == symbol) > 0;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _db.Dispose();
        }
    }
}

// 📌 FundingRate Modeli
public class FundingRateStruct
{
    public int Id { get; set; }  // LiteDB için Primary Key
    public string Symbol { get; set; }
    public decimal FundingRateValue { get; set; }
    public decimal Price { get; set; }
    public DateTime Date { get; set; }
}
