namespace SideScreenHUD.Models;

public sealed class WatchlistSymbol
{
    public string Id { get; set; } = "";             // Unique ID e.g. "BTC", "USDCNH", "CN_600519", "US_NVDA"
    public string DisplayCode { get; set; } = "";    // e.g. "BTC/USDT", "USD/CNH", "600519", "NVDA"
    public string Name { get; set; } = "";           // Readable Chinese / English label
    public string Category { get; set; } = "fx";     // fx | crypto | cn | us
    public string Provider { get; set; } = "yahoo";  // crypto | eastmoney | yahoo
    public string ProviderSymbol { get; set; } = ""; // e.g. "BTC", "1.600519", "NVDA", "133.USDCNH"
    public string? BaseAsset { get; set; }           // For crypto: "BTC", "ETH", "SOL", etc.
    public string? FallbackYahoo { get; set; }
    public int Decimals { get; set; } = 4;
    public double Multiplier { get; set; } = 1.0;
    public bool IsCustom { get; set; } = false;
}

public sealed class QuoteItem
{
    public string Id { get; set; } = "";
    public string DisplayCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "fx";
    public double Price { get; set; }
    public double PrevClose { get; set; }
    public double Change { get; set; }
    public double ChangePercent { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Volume { get; set; }
    public int Decimals { get; set; } = 4;
    public string Source { get; set; } = "";
    public string? Exchange { get; set; }
    public string? MarketType { get; set; }          // spot | futures
    public double? MarkPrice { get; set; }
    public double? FundingRate { get; set; }         // Decimal e.g. 0.0001 = 0.01%
    public long UpdatedMs { get; set; }
    public List<double> Sparkline { get; set; } = new();
    public bool IsCustom { get; set; }
}

public sealed class KlineCandle
{
    public long TimeSec { get; set; }
    public string TimeLabel { get; set; } = "";
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
}

public sealed class KlinePayload
{
    public string SymbolId { get; set; } = "";
    public string DisplayCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Interval { get; set; } = "15m";
    public int Decimals { get; set; } = 4;
    public string Source { get; set; } = "";
    public string? Exchange { get; set; }
    public string? MarketType { get; set; }          // spot | futures
    public double? MarkPrice { get; set; }
    public double? FundingRate { get; set; }
    public double LastPrice { get; set; }
    public double PrevClose { get; set; }
    public double Change { get; set; }
    public double ChangePercent { get; set; }
    public double High24h { get; set; }
    public double Low24h { get; set; }
    public long UpdatedMs { get; set; }
    public string? Error { get; set; }
    public List<KlineCandle> Candles { get; set; } = new();
}

public sealed class FxRatesPayload
{
    public string BaseCurrency { get; set; } = "USD";
    public long UpdatedMs { get; set; }
    public Dictionary<string, double> Rates { get; set; } = new();
}
