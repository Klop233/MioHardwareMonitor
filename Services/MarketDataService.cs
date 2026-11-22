using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

public sealed class MarketDataService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, QuoteItem> _quotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, KlinePayload Data)> _klineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _quoteLastFetchedUtc = new(StringComparer.OrdinalIgnoreCase);
    private FxRatesPayload? _fxRatesCache;
    private DateTime _fxRatesFetchedUtc = DateTime.MinValue;
    private string _lastCryptoKey = "";

    public static List<WatchlistSymbol> DefaultSymbols { get; } = new()
    {
        // ========== 1. 虚拟货币 (支持 Binance / OKX / Bybit / Bitget / Gate.io，现货 & U本位永续合约) ==========
        new() { Id = "BTC",  DisplayCode = "BTC/USDT",  Name = "比特币",   Category = "crypto", Provider = "crypto", ProviderSymbol = "BTC",  BaseAsset = "BTC",  Decimals = 2 },
        new() { Id = "ETH",  DisplayCode = "ETH/USDT",  Name = "以太坊",   Category = "crypto", Provider = "crypto", ProviderSymbol = "ETH",  BaseAsset = "ETH",  Decimals = 2 },
        new() { Id = "SOL",  DisplayCode = "SOL/USDT",  Name = "Solana",  Category = "crypto", Provider = "crypto", ProviderSymbol = "SOL",  BaseAsset = "SOL",  Decimals = 2 },
        new() { Id = "BNB",  DisplayCode = "BNB/USDT",  Name = "币安币",   Category = "crypto", Provider = "crypto", ProviderSymbol = "BNB",  BaseAsset = "BNB",  Decimals = 2 },
        new() { Id = "XRP",  DisplayCode = "XRP/USDT",  Name = "瑞波币",   Category = "crypto", Provider = "crypto", ProviderSymbol = "XRP",  BaseAsset = "XRP",  Decimals = 4 },
        new() { Id = "DOGE", DisplayCode = "DOGE/USDT", Name = "狗狗币",   Category = "crypto", Provider = "crypto", ProviderSymbol = "DOGE", BaseAsset = "DOGE", Decimals = 5 },
        new() { Id = "SUI",  DisplayCode = "SUI/USDT",  Name = "Sui",     Category = "crypto", Provider = "crypto", ProviderSymbol = "SUI",  BaseAsset = "SUI",  Decimals = 4 },
        new() { Id = "AVAX", DisplayCode = "AVAX/USDT", Name = "雪崩",     Category = "crypto", Provider = "crypto", ProviderSymbol = "AVAX", BaseAsset = "AVAX", Decimals = 2 },
        new() { Id = "LINK", DisplayCode = "LINK/USDT", Name = "Chainlink",Category = "crypto", Provider = "crypto", ProviderSymbol = "LINK", BaseAsset = "LINK", Decimals = 3 },
        new() { Id = "LTC",  DisplayCode = "LTC/USDT",  Name = "莱特币",   Category = "crypto", Provider = "crypto", ProviderSymbol = "LTC",  BaseAsset = "LTC",  Decimals = 2 },

        // ========== 2. 外汇汇率 & 现货黄金 ==========
        new() { Id = "USDCNH", DisplayCode = "USD/CNH",    Name = "美元/离岸人民币", Category = "fx", Provider = "eastmoney", ProviderSymbol = "133.USDCNH", FallbackYahoo = "USDCNY=X", Decimals = 4 },
        new() { Id = "USDCNY", DisplayCode = "USD/CNY",    Name = "美元/在岸人民币", Category = "fx", Provider = "yahoo",     ProviderSymbol = "USDCNY=X",   Decimals = 4 },
        new() { Id = "EURCNY", DisplayCode = "EUR/CNY",    Name = "欧元/人民币",    Category = "fx", Provider = "yahoo",     ProviderSymbol = "EURCNY=X",   Decimals = 4 },
        new() { Id = "JPYCNY", DisplayCode = "100JPY/CNY", Name = "100日元/人民币", Category = "fx", Provider = "yahoo",     ProviderSymbol = "JPYCNY=X",   Decimals = 4, Multiplier = 100.0 },
        new() { Id = "HKDCNY", DisplayCode = "HKD/CNY",    Name = "港元/人民币",    Category = "fx", Provider = "yahoo",     ProviderSymbol = "HKDCNY=X",   Decimals = 4 },
        new() { Id = "GBPCNY", DisplayCode = "GBP/CNY",    Name = "英镑/人民币",    Category = "fx", Provider = "yahoo",     ProviderSymbol = "GBPCNY=X",   Decimals = 4 },
        new() { Id = "EURUSD", DisplayCode = "EUR/USD",    Name = "欧元/美元",      Category = "fx", Provider = "eastmoney", ProviderSymbol = "119.EURUSD", FallbackYahoo = "EURUSD=X", Decimals = 4 },
        new() { Id = "USDJPY", DisplayCode = "USD/JPY",    Name = "美元/日元",      Category = "fx", Provider = "eastmoney", ProviderSymbol = "119.USDJPY", FallbackYahoo = "USDJPY=X", Decimals = 3 },
        new() { Id = "XAUUSD", DisplayCode = "XAU/USD",    Name = "现货黄金",       Category = "fx", Provider = "eastmoney", ProviderSymbol = "122.XAU",    FallbackYahoo = "GC=F",     Decimals = 2 },

        // ========== 3. A 股（东方财富实时前复权 K 线） ==========
        new() { Id = "CN_SSE",    DisplayCode = "上证指数", Name = "000001.SH", Category = "cn", Provider = "eastmoney", ProviderSymbol = "1.000001", FallbackYahoo = "000001.SS", Decimals = 2 },
        new() { Id = "CN_SZSE",   DisplayCode = "深证成指", Name = "399001.SZ", Category = "cn", Provider = "eastmoney", ProviderSymbol = "0.399001", FallbackYahoo = "399001.SZ", Decimals = 2 },
        new() { Id = "CN_CYB",    DisplayCode = "创业板指", Name = "399006.SZ", Category = "cn", Provider = "eastmoney", ProviderSymbol = "0.399006", Decimals = 2 },
        new() { Id = "CN_KC50",   DisplayCode = "科创50",   Name = "000688.SH", Category = "cn", Provider = "eastmoney", ProviderSymbol = "1.000688", Decimals = 2 },
        new() { Id = "CN_HS300",  DisplayCode = "沪深300",  Name = "000300.SH", Category = "cn", Provider = "eastmoney", ProviderSymbol = "1.000300", Decimals = 2 },
        new() { Id = "CN_600519", DisplayCode = "600519",   Name = "贵州茅台",   Category = "cn", Provider = "eastmoney", ProviderSymbol = "1.600519", FallbackYahoo = "600519.SS", Decimals = 2 },
        new() { Id = "CN_300750", DisplayCode = "300750",   Name = "宁德时代",   Category = "cn", Provider = "eastmoney", ProviderSymbol = "0.300750", FallbackYahoo = "300750.SZ", Decimals = 2 },
        new() { Id = "CN_002594", DisplayCode = "002594",   Name = "比亚迪",     Category = "cn", Provider = "eastmoney", ProviderSymbol = "0.002594", FallbackYahoo = "002594.SZ", Decimals = 2 },
        new() { Id = "CN_688981", DisplayCode = "688981",   Name = "中芯国际",   Category = "cn", Provider = "eastmoney", ProviderSymbol = "1.688981", Decimals = 2 },
        new() { Id = "CN_300059", DisplayCode = "300059",   Name = "东方财富",   Category = "cn", Provider = "eastmoney", ProviderSymbol = "0.300059", Decimals = 2 },

        // ========== 4. 美股指数与热门科技股 ==========
        new() { Id = "US_IXIC",  DisplayCode = "NASDAQ",  Name = "纳斯达克综合", Category = "us", Provider = "yahoo", ProviderSymbol = "^IXIC", Decimals = 2 },
        new() { Id = "US_GSPC",  DisplayCode = "S&P 500", Name = "标普500指数",  Category = "us", Provider = "yahoo", ProviderSymbol = "^GSPC", Decimals = 2 },
        new() { Id = "US_DJI",   DisplayCode = "DOW",     Name = "道琼斯工业",   Category = "us", Provider = "yahoo", ProviderSymbol = "^DJI",  Decimals = 2 },
        new() { Id = "US_NVDA",  DisplayCode = "NVDA",    Name = "英伟达",       Category = "us", Provider = "yahoo", ProviderSymbol = "NVDA",  Decimals = 2 },
        new() { Id = "US_AAPL",  DisplayCode = "AAPL",    Name = "苹果",         Category = "us", Provider = "yahoo", ProviderSymbol = "AAPL",  Decimals = 2 },
        new() { Id = "US_TSLA",  DisplayCode = "TSLA",    Name = "特斯拉",       Category = "us", Provider = "yahoo", ProviderSymbol = "TSLA",  Decimals = 2 },
        new() { Id = "US_MSFT",  DisplayCode = "MSFT",    Name = "微软",         Category = "us", Provider = "yahoo", ProviderSymbol = "MSFT",  Decimals = 2 },
        new() { Id = "US_AMD",   DisplayCode = "AMD",     Name = "超威半导体",   Category = "us", Provider = "yahoo", ProviderSymbol = "AMD",   Decimals = 2 },
        new() { Id = "US_META",  DisplayCode = "META",    Name = "Meta",         Category = "us", Provider = "yahoo", ProviderSymbol = "META",  Decimals = 2 },
        new() { Id = "US_GOOGL", DisplayCode = "GOOGL",   Name = "谷歌",         Category = "us", Provider = "yahoo", ProviderSymbol = "GOOGL", Decimals = 2 },
        new() { Id = "US_AMZN",  DisplayCode = "AMZN",    Name = "亚马逊",       Category = "us", Provider = "yahoo", ProviderSymbol = "AMZN",  Decimals = 2 },
        new() { Id = "US_COIN",  DisplayCode = "COIN",    Name = "Coinbase",     Category = "us", Provider = "yahoo", ProviderSymbol = "COIN",  Decimals = 2 },
    };

    public MarketDataService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public List<WatchlistSymbol> GetAllSymbols(List<WatchlistSymbol>? customSymbols)
    {
        var all = new List<WatchlistSymbol>(DefaultSymbols);
        if (customSymbols != null)
        {
            foreach (var c in customSymbols)
            {
                if (!all.Any(x => x.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    // Migrate legacy custom Binance symbols to universal crypto provider
                    if (c.Provider == "binance")
                    {
                        c.Provider = "crypto";
                        c.Category = "crypto";
                        c.BaseAsset ??= c.ProviderSymbol.Replace("USDT", "", StringComparison.OrdinalIgnoreCase);
                    }
                    all.Add(c);
                }
            }
        }
        return all;
    }

    public void ClearCryptoCache()
    {
        foreach (var k in _klineCache.Keys.ToList())
        {
            if (k.StartsWith("CRYPTO:", StringComparison.OrdinalIgnoreCase))
                _klineCache.TryRemove(k, out _);
        }
        foreach (var kv in _quotes.ToList())
        {
            if (kv.Value.Category == "crypto")
                _quotes.TryRemove(kv.Key, out _);
        }
    }

    public async Task<List<QuoteItem>> RefreshQuotesAsync(
        List<WatchlistSymbol> symbols,
        string cryptoExchange = "binance",
        string cryptoMarketType = "futures",
        string? priorityCategory = null,
        bool forceAll = false,
        CancellationToken ct = default)
    {
        cryptoExchange = NormalizeExchange(cryptoExchange);
        cryptoMarketType = NormalizeMarketType(cryptoMarketType);
        string cryptoKey = $"{cryptoExchange}:{cryptoMarketType}";
        if (_lastCryptoKey != cryptoKey)
        {
            _lastCryptoKey = cryptoKey;
            ClearCryptoCache();
        }

        var cryptoSyms = symbols.Where(s => s.Provider == "crypto").ToList();
        var otherSyms = symbols.Where(s => s.Provider != "crypto").ToList();

        var tasks = new List<Task>();

        // 1. Crypto: single ultra-fast batch call per exchange + market type!
        if (cryptoSyms.Count > 0)
        {
            tasks.Add(RefreshCryptoQuotesBatchAsync(cryptoSyms, cryptoExchange, cryptoMarketType, ct));
        }

        // 2. FX / A-Shares / US Stocks: refresh active category aggressively, non-active gently (every 45s)
        var nowUtc = DateTime.UtcNow;
        var toFetch = otherSyms.Where(s =>
        {
            if (forceAll) return true;
            if (!_quotes.ContainsKey(s.Id)) return true;
            if (string.IsNullOrEmpty(priorityCategory) || priorityCategory == "all" || s.Category == priorityCategory || s.Category == "fx")
                return true;
            return !_quoteLastFetchedUtc.TryGetValue(s.Id, out var t) || (nowUtc - t).TotalSeconds >= 45;
        }).ToList();

        using var gate = new SemaphoreSlim(6);
        foreach (var sym in toFetch)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await RefreshSingleNonCryptoQuoteAsync(sym, ct);
                    _quoteLastFetchedUtc[sym.Id] = DateTime.UtcNow;
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        try { await Task.WhenAll(tasks); } catch { }

        return symbols.Select(s =>
        {
            if (_quotes.TryGetValue(s.Id, out var q)) return q;
            return new QuoteItem
            {
                Id = s.Id,
                DisplayCode = FormatCryptoCode(s, cryptoMarketType),
                Name = s.Name,
                Category = s.Category,
                Decimals = s.Decimals,
                Exchange = s.Provider == "crypto" ? cryptoExchange : null,
                MarketType = s.Provider == "crypto" ? cryptoMarketType : null,
                IsCustom = s.IsCustom
            };
        }).ToList();
    }

    public async Task<KlinePayload> FetchKlineAsync(
        WatchlistSymbol symbol,
        string interval,
        string cryptoExchange = "binance",
        string cryptoMarketType = "futures",
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        cryptoExchange = NormalizeExchange(cryptoExchange);
        cryptoMarketType = NormalizeMarketType(cryptoMarketType);

        string cacheKey = symbol.Provider == "crypto"
            ? $"CRYPTO:{cryptoExchange}:{cryptoMarketType}:{symbol.Id}:{interval}"
            : $"{symbol.Id}:{interval}";

        int ttlSec = interval switch
        {
            "1m" => 4,
            "5m" => 10,
            "15m" => 15,
            "1h" => 25,
            "4h" => 45,
            _ => 60
        };

        if (!forceRefresh && _klineCache.TryGetValue(cacheKey, out var cached)
            && (DateTime.UtcNow - cached.FetchedUtc).TotalSeconds < ttlSec)
        {
            return cached.Data;
        }

        KlinePayload? result = null;
        try
        {
            if (symbol.Provider == "crypto")
            {
                result = await FetchCryptoKlineAsync(symbol, interval, cryptoExchange, cryptoMarketType, ct);
            }
            else if (symbol.Provider == "eastmoney")
            {
                result = await FetchEastMoneyKlineAsync(symbol, interval, ct);
                if ((result == null || result.Candles.Count == 0) && !string.IsNullOrWhiteSpace(symbol.FallbackYahoo))
                {
                    result = await FetchYahooKlineAsync(symbol, symbol.FallbackYahoo, interval, ct);
                }
            }
            else
            {
                result = await FetchYahooKlineAsync(symbol, symbol.ProviderSymbol, interval, ct);
            }
        }
        catch (Exception ex)
        {
            result = new KlinePayload
            {
                SymbolId = symbol.Id,
                DisplayCode = FormatCryptoCode(symbol, cryptoMarketType),
                Name = symbol.Name,
                Category = symbol.Category,
                Interval = interval,
                Decimals = symbol.Decimals,
                Exchange = symbol.Provider == "crypto" ? cryptoExchange : null,
                MarketType = symbol.Provider == "crypto" ? cryptoMarketType : null,
                Error = ex.Message
            };
        }

        if (result == null || result.Candles.Count == 0)
        {
            if (_klineCache.TryGetValue(cacheKey, out var stale)) return stale.Data;
            return result ?? new KlinePayload
            {
                SymbolId = symbol.Id,
                DisplayCode = FormatCryptoCode(symbol, cryptoMarketType),
                Name = symbol.Name,
                Category = symbol.Category,
                Interval = interval,
                Decimals = symbol.Decimals,
                Exchange = symbol.Provider == "crypto" ? cryptoExchange : null,
                MarketType = symbol.Provider == "crypto" ? cryptoMarketType : null,
                Error = $"暂无法从 {FormatExchangeLabel(cryptoExchange)} 获取该标的 K 线，请尝试切换交易所或检查网络"
            };
        }

        // Sync accurate 24h/day quote stats + mark price + funding rate into K-line header
        if (_quotes.TryGetValue(symbol.Id, out var q) && q.Price > 0)
        {
            result.LastPrice = result.Candles[^1].Close;
            if (q.PrevClose > 0)
            {
                result.PrevClose = q.PrevClose;
                result.Change = result.LastPrice - q.PrevClose;
                result.ChangePercent = result.Change / q.PrevClose * 100.0;
            }
            if (q.High > 0) result.High24h = q.High;
            if (q.Low > 0) result.Low24h = q.Low;
            result.MarkPrice ??= q.MarkPrice;
            result.FundingRate ??= q.FundingRate;
        }

        _klineCache[cacheKey] = (DateTime.UtcNow, result);
        return result;
    }

    public async Task<FxRatesPayload> FetchFxSpotRatesAsync(CancellationToken ct = default)
    {
        if (_fxRatesCache != null && (DateTime.UtcNow - _fxRatesFetchedUtc).TotalMinutes < 5)
            return _fxRatesCache;

        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 1.0 };
        try
        {
            var json = await _http.GetStringAsync("https://open.er-api.com/v6/latest/USD", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rates", out var rObj))
            {
                string[] keep = { "USD", "CNY", "EUR", "JPY", "HKD", "GBP", "KRW", "AUD", "CAD", "SGD", "CHF", "TWD", "NZD", "THB" };
                foreach (var cur in keep)
                {
                    if (rObj.TryGetProperty(cur, out var val) && val.TryGetDouble(out double d) && d > 0)
                        rates[cur] = d;
                }
            }
        }
        catch { }

        if (_quotes.TryGetValue("USDCNH", out var cnh) && cnh.Price > 0) rates["CNH"] = cnh.Price;
        if (_quotes.TryGetValue("USDCNY", out var cny) && cny.Price > 0) rates["CNY"] = cny.Price;
        if (_quotes.TryGetValue("EURUSD", out var eurusd) && eurusd.Price > 0) rates["EUR"] = 1.0 / eurusd.Price;
        if (_quotes.TryGetValue("USDJPY", out var usdjpy) && usdjpy.Price > 0) rates["JPY"] = usdjpy.Price;

        var payload = new FxRatesPayload
        {
            BaseCurrency = "USD",
            UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Rates = rates
        };
        if (rates.Count > 2)
        {
            _fxRatesCache = payload;
            _fxRatesFetchedUtc = DateTime.UtcNow;
        }
        return _fxRatesCache ?? payload;
    }

    // =========================================================================
    //  CRYPTO MULTI-EXCHANGE QUOTES (Binance / OKX / Bybit / Bitget / Gate.io)
    // =========================================================================
    private async Task RefreshCryptoQuotesBatchAsync(List<WatchlistSymbol> syms, string exchange, string marketType, CancellationToken ct)
    {
        bool isFutures = marketType == "futures";
        try
        {
            switch (exchange)
            {
                case "binance": await FetchBinanceQuotesAsync(syms, isFutures, ct); break;
                case "okx":     await FetchOkxQuotesAsync(syms, isFutures, ct);     break;
                case "bybit":   await FetchBybitQuotesAsync(syms, isFutures, ct);   break;
                case "bitget":  await FetchBitgetQuotesAsync(syms, isFutures, ct);  break;
                case "gate":    await FetchGateQuotesAsync(syms, isFutures, ct);    break;
            }
        }
        catch { }
    }

    private async Task FetchBinanceQuotesAsync(List<WatchlistSymbol> syms, bool isFutures, CancellationToken ct)
    {
        var wanted = syms.ToDictionary(s => GetCryptoBase(s) + "USDT", s => s, StringComparer.OrdinalIgnoreCase);
        var url = isFutures
            ? "https://fapi.binance.com/fapi/v1/ticker/24hr"
            : $"https://api.binance.com/api/v3/ticker/24hr?symbols={Uri.EscapeDataString(JsonSerializer.Serialize(wanted.Keys.ToArray()))}";

        Task<string>? premiumTask = isFutures
            ? _http.GetStringAsync("https://fapi.binance.com/fapi/v1/premiumIndex", ct)
            : null;

        string json = await _http.GetStringAsync(url, ct);

        var fundingMap = new Dictionary<string, (double Mark, double Rate)>(StringComparer.OrdinalIgnoreCase);
        if (premiumTask != null)
        {
            try
            {
                var pJson = await premiumTask;
                using var pDoc = JsonDocument.Parse(pJson);
                foreach (var el in pDoc.RootElement.EnumerateArray())
                {
                    var sName = el.GetProperty("symbol").GetString() ?? "";
                    if (!wanted.ContainsKey(sName)) continue;
                    fundingMap[sName] = (
                        ParseDouble(el.GetProperty("markPrice").GetString()),
                        ParseDouble(el.GetProperty("lastFundingRate").GetString())
                    );
                }
            }
            catch { }
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var sName = el.GetProperty("symbol").GetString() ?? "";
            if (!wanted.TryGetValue(sName, out var sym)) continue;

            double price = ParseDouble(el.GetProperty("lastPrice").GetString());
            double openP = el.TryGetProperty("openPrice", out var op) ? ParseDouble(op.GetString()) : price;
            double prev = el.TryGetProperty("prevClosePrice", out var pc) ? ParseDouble(pc.GetString()) : openP;
            if (prev <= 0) prev = openP;
            double change = ParseDouble(el.GetProperty("priceChange").GetString());
            double pct = ParseDouble(el.GetProperty("priceChangePercent").GetString());
            double high = ParseDouble(el.GetProperty("highPrice").GetString());
            double low = ParseDouble(el.GetProperty("lowPrice").GetString());
            double vol = ParseDouble(el.GetProperty("quoteVolume").GetString());

            double? mark = null, fr = null;
            if (fundingMap.TryGetValue(sName, out var fm))
            {
                mark = fm.Mark > 0 ? fm.Mark : null;
                fr = fm.Rate;
            }

            StoreCryptoQuote(sym, "binance", isFutures ? "futures" : "spot", price, prev, change, pct, high, low, vol, mark, fr);
        }
    }

    private async Task FetchOkxQuotesAsync(List<WatchlistSymbol> syms, bool isFutures, CancellationToken ct)
    {
        string instType = isFutures ? "SWAP" : "SPOT";
        var wanted = syms.ToDictionary(
            s => isFutures ? $"{GetCryptoBase(s)}-USDT-SWAP" : $"{GetCryptoBase(s)}-USDT",
            s => s,
            StringComparer.OrdinalIgnoreCase);

        var json = await _http.GetStringAsync($"https://www.okx.com/api/v5/market/tickers?instType={instType}", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return;

        foreach (var el in data.EnumerateArray())
        {
            var instId = el.GetProperty("instId").GetString() ?? "";
            if (!wanted.TryGetValue(instId, out var sym)) continue;

            double price = ParseDouble(el.GetProperty("last").GetString());
            double open24 = ParseDouble(el.GetProperty("open24h").GetString());
            double high = ParseDouble(el.GetProperty("high24h").GetString());
            double low = ParseDouble(el.GetProperty("low24h").GetString());
            double vol = ParseDouble(el.GetProperty("volCcy24h").GetString());
            double change = price - open24;
            double pct = open24 > 0 ? change / open24 * 100.0 : 0;

            StoreCryptoQuote(sym, "okx", isFutures ? "futures" : "spot", price, open24, change, pct, high, low, vol, null, null);
        }
    }

    private async Task FetchBybitQuotesAsync(List<WatchlistSymbol> syms, bool isFutures, CancellationToken ct)
    {
        string cat = isFutures ? "linear" : "spot";
        var wanted = syms.ToDictionary(s => GetCryptoBase(s) + "USDT", s => s, StringComparer.OrdinalIgnoreCase);

        var json = await _http.GetStringAsync($"https://api.bybit.com/v5/market/tickers?category={cat}", ct);
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("result").GetProperty("list");

        foreach (var el in list.EnumerateArray())
        {
            var sName = el.GetProperty("symbol").GetString() ?? "";
            if (!wanted.TryGetValue(sName, out var sym)) continue;

            double price = ParseDouble(el.GetProperty("lastPrice").GetString());
            double prev = ParseDouble(el.GetProperty("prevPrice24h").GetString());
            double pct = ParseDouble(el.GetProperty("price24hPcnt").GetString()) * 100.0;
            double change = price - prev;
            double high = ParseDouble(el.GetProperty("highPrice24h").GetString());
            double low = ParseDouble(el.GetProperty("lowPrice24h").GetString());
            double vol = ParseDouble(el.GetProperty("turnover24h").GetString());

            double? mark = el.TryGetProperty("markPrice", out var mp) ? ParseNullableDouble(mp.GetString()) : null;
            double? fr = el.TryGetProperty("fundingRate", out var frp) ? ParseNullableDouble(frp.GetString()) : null;

            StoreCryptoQuote(sym, "bybit", isFutures ? "futures" : "spot", price, prev, change, pct, high, low, vol, mark, fr);
        }
    }

    private async Task FetchBitgetQuotesAsync(List<WatchlistSymbol> syms, bool isFutures, CancellationToken ct)
    {
        var wanted = syms.ToDictionary(s => GetCryptoBase(s) + "USDT", s => s, StringComparer.OrdinalIgnoreCase);
        var url = isFutures
            ? "https://api.bitget.com/api/v2/mix/market/tickers?productType=USDT-FUTURES"
            : "https://api.bitget.com/api/v2/spot/market/tickers";

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return;

        foreach (var el in data.EnumerateArray())
        {
            var sName = el.GetProperty("symbol").GetString() ?? "";
            if (!wanted.TryGetValue(sName, out var sym)) continue;

            double price = ParseDouble(el.GetProperty("lastPr").GetString());
            double open24 = el.TryGetProperty("open24h", out var o24) ? ParseDouble(o24.GetString()) : price;
            double high = ParseDouble(el.GetProperty("high24h").GetString());
            double low = ParseDouble(el.GetProperty("low24h").GetString());
            double pct = el.TryGetProperty("change24h", out var c24) ? ParseDouble(c24.GetString()) * 100.0 : (open24 > 0 ? (price - open24) / open24 * 100.0 : 0);
            double change = price - open24;
            double vol = el.TryGetProperty("usdtVolume", out var uv) ? ParseDouble(uv.GetString()) : 0;

            double? mark = el.TryGetProperty("markPrice", out var mp) ? ParseNullableDouble(mp.GetString()) : null;
            double? fr = el.TryGetProperty("fundingRate", out var frp) ? ParseNullableDouble(frp.GetString()) : null;

            StoreCryptoQuote(sym, "bitget", isFutures ? "futures" : "spot", price, open24, change, pct, high, low, vol, mark, fr);
        }
    }

    private async Task FetchGateQuotesAsync(List<WatchlistSymbol> syms, bool isFutures, CancellationToken ct)
    {
        var wanted = syms.ToDictionary(s => GetCryptoBase(s) + "_USDT", s => s, StringComparer.OrdinalIgnoreCase);
        var url = isFutures
            ? "https://api.gateio.ws/api/v4/futures/usdt/tickers"
            : "https://api.gateio.ws/api/v4/spot/tickers";

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string keyProp = isFutures ? "contract" : "currency_pair";
            if (!el.TryGetProperty(keyProp, out var kp)) continue;
            var pair = kp.GetString() ?? "";
            if (!wanted.TryGetValue(pair, out var sym)) continue;

            double price = ParseDouble(el.GetProperty("last").GetString());
            double pct = ParseDouble(el.GetProperty("change_percentage").GetString());
            double prev = Math.Abs(1.0 + pct / 100.0) > 1e-6 ? price / (1.0 + pct / 100.0) : price;
            double change = price - prev;
            double high = ParseDouble(el.GetProperty("high_24h").GetString());
            double low = ParseDouble(el.GetProperty("low_24h").GetString());
            double vol = isFutures
                ? (el.TryGetProperty("volume_24h_quote", out var vq) ? ParseDouble(vq.GetString()) : 0)
                : (el.TryGetProperty("quote_volume", out var sq) ? ParseDouble(sq.GetString()) : 0);

            double? mark = el.TryGetProperty("mark_price", out var mp) ? ParseNullableDouble(mp.GetString()) : null;
            double? fr = el.TryGetProperty("funding_rate", out var frp) ? ParseNullableDouble(frp.GetString()) : null;

            StoreCryptoQuote(sym, "gate", isFutures ? "futures" : "spot", price, prev, change, pct, high, low, vol, mark, fr);
        }
    }

    private void StoreCryptoQuote(
        WatchlistSymbol sym, string exchange, string marketType,
        double price, double prev, double change, double pct, double high, double low, double vol,
        double? markPrice, double? fundingRate)
    {
        var spark = _quotes.TryGetValue(sym.Id, out var old) && old.Exchange == exchange && old.MarketType == marketType
            ? new List<double>(old.Sparkline)
            : new List<double>();

        if (price > 0)
        {
            if (spark.Count == 0 && prev > 0) spark.Add(prev);
            spark.Add(price);
            if (spark.Count > 30) spark.RemoveAt(0);
        }

        _quotes[sym.Id] = new QuoteItem
        {
            Id = sym.Id,
            DisplayCode = FormatCryptoCode(sym, marketType),
            Name = sym.Name,
            Category = sym.Category,
            Price = price,
            PrevClose = prev,
            Change = change,
            ChangePercent = pct,
            High = high,
            Low = low,
            Volume = vol,
            Decimals = AutoDecimals(price, sym.Decimals),
            Source = $"{FormatExchangeLabel(exchange)} {(marketType == "futures" ? "永续" : "现货")}",
            Exchange = exchange,
            MarketType = marketType,
            MarkPrice = markPrice,
            FundingRate = fundingRate,
            UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sparkline = spark,
            IsCustom = sym.IsCustom
        };
    }

    // =========================================================================
    //  CRYPTO MULTI-EXCHANGE K-LINES (Spot & Perpetual Futures)
    // =========================================================================
    private async Task<KlinePayload?> FetchCryptoKlineAsync(WatchlistSymbol sym, string interval, string exchange, string marketType, CancellationToken ct)
    {
        bool isFutures = marketType == "futures";
        string baseAsset = GetCryptoBase(sym);
        List<KlineCandle> candles;
        double? markPrice = null;
        double? fundingRate = null;

        switch (exchange)
        {
            case "binance":
                (candles, markPrice, fundingRate) = await FetchBinanceKlineAsync(baseAsset, interval, isFutures, ct);
                break;
            case "okx":
                (candles, markPrice, fundingRate) = await FetchOkxKlineAsync(baseAsset, interval, isFutures, ct);
                break;
            case "bybit":
                candles = await FetchBybitKlineAsync(baseAsset, interval, isFutures, ct);
                break;
            case "bitget":
                candles = await FetchBitgetKlineAsync(baseAsset, interval, isFutures, ct);
                break;
            case "gate":
                candles = await FetchGateKlineAsync(baseAsset, interval, isFutures, ct);
                break;
            default:
                (candles, markPrice, fundingRate) = await FetchBinanceKlineAsync(baseAsset, interval, isFutures, ct);
                break;
        }

        candles = candles.OrderBy(c => c.TimeSec).ToList();
        string sourceLabel = $"{FormatExchangeLabel(exchange)} · {(isFutures ? "U本位永续" : "现货")}";
        var payload = BuildPayload(sym, interval, sourceLabel, candles);
        payload.DisplayCode = FormatCryptoCode(sym, marketType);
        payload.Exchange = exchange;
        payload.MarketType = marketType;
        payload.MarkPrice = markPrice;
        payload.FundingRate = fundingRate;
        if (payload.LastPrice > 0) payload.Decimals = AutoDecimals(payload.LastPrice, sym.Decimals);

        // Seed quote sparkline from latest candles so tile sparkline looks rich right away!
        if (candles.Count >= 6 && _quotes.TryGetValue(sym.Id, out var q) && q.Sparkline.Count < 8)
        {
            q.Sparkline = candles.Skip(Math.Max(0, candles.Count - 28)).Select(c => c.Close).ToList();
        }
        return payload;
    }

    private async Task<(List<KlineCandle>, double?, double?)> FetchBinanceKlineAsync(string baseAsset, string interval, bool isFutures, CancellationToken ct)
    {
        string bIv = interval is "1m" or "5m" or "15m" or "1h" or "4h" or "1d" or "1w" ? interval : "15m";
        string pair = baseAsset + "USDT";
        string url = isFutures
            ? $"https://fapi.binance.com/fapi/v1/klines?symbol={pair}&interval={bIv}&limit=220"
            : $"https://api.binance.com/api/v3/klines?symbol={pair}&interval={bIv}&limit=220";

        Task<string>? premTask = isFutures
            ? _http.GetStringAsync($"https://fapi.binance.com/fapi/v1/premiumIndex?symbol={pair}", ct)
            : null;

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<KlineCandle>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            long sec = row[0].GetInt64() / 1000;
            var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
            list.Add(new KlineCandle
            {
                TimeSec = sec,
                TimeLabel = FormatTimeLabel(dt, interval),
                Open = ParseDouble(row[1].GetString()),
                High = ParseDouble(row[2].GetString()),
                Low = ParseDouble(row[3].GetString()),
                Close = ParseDouble(row[4].GetString()),
                Volume = ParseDouble(row[5].GetString())
            });
        }

        double? mark = null, fr = null;
        if (premTask != null)
        {
            try
            {
                using var pDoc = JsonDocument.Parse(await premTask);
                mark = ParseNullableDouble(pDoc.RootElement.GetProperty("markPrice").GetString());
                fr = ParseNullableDouble(pDoc.RootElement.GetProperty("lastFundingRate").GetString());
            }
            catch { }
        }
        return (list, mark, fr);
    }

    private async Task<(List<KlineCandle>, double?, double?)> FetchOkxKlineAsync(string baseAsset, string interval, bool isFutures, CancellationToken ct)
    {
        string instId = isFutures ? $"{baseAsset}-USDT-SWAP" : $"{baseAsset}-USDT";
        string bar = interval switch
        {
            "1m" => "1m", "5m" => "5m", "15m" => "15m",
            "1h" => "1H", "4h" => "4H", "1d" => "1D", "1w" => "1W", _ => "15m"
        };

        Task<string>? frTask = isFutures
            ? _http.GetStringAsync($"https://www.okx.com/api/v5/public/funding-rate?instId={instId}", ct)
            : null;

        var json = await _http.GetStringAsync($"https://www.okx.com/api/v5/market/candles?instId={instId}&bar={bar}&limit=200", ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<KlineCandle>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var row in data.EnumerateArray())
            {
                long sec = long.Parse(row[0].GetString() ?? "0") / 1000;
                var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
                list.Add(new KlineCandle
                {
                    TimeSec = sec,
                    TimeLabel = FormatTimeLabel(dt, interval),
                    Open = ParseDouble(row[1].GetString()),
                    High = ParseDouble(row[2].GetString()),
                    Low = ParseDouble(row[3].GetString()),
                    Close = ParseDouble(row[4].GetString()),
                    Volume = ParseDouble(row[5].GetString())
                });
            }
        }

        double? fr = null;
        if (frTask != null)
        {
            try
            {
                using var fDoc = JsonDocument.Parse(await frTask);
                var arr = fDoc.RootElement.GetProperty("data");
                if (arr.GetArrayLength() > 0)
                    fr = ParseNullableDouble(arr[0].GetProperty("fundingRate").GetString());
            }
            catch { }
        }
        return (list, null, fr);
    }

    private async Task<List<KlineCandle>> FetchBybitKlineAsync(string baseAsset, string interval, bool isFutures, CancellationToken ct)
    {
        string cat = isFutures ? "linear" : "spot";
        string iv = interval switch
        {
            "1m" => "1", "5m" => "5", "15m" => "15",
            "1h" => "60", "4h" => "240", "1d" => "D", "1w" => "W", _ => "15"
        };
        var url = $"https://api.bybit.com/v5/market/kline?category={cat}&symbol={baseAsset}USDT&interval={iv}&limit=200";
        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<KlineCandle>();
        if (doc.RootElement.TryGetProperty("result", out var res) && res.TryGetProperty("list", out var rows))
        {
            foreach (var row in rows.EnumerateArray())
            {
                long sec = long.Parse(row[0].GetString() ?? "0") / 1000;
                var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
                list.Add(new KlineCandle
                {
                    TimeSec = sec,
                    TimeLabel = FormatTimeLabel(dt, interval),
                    Open = ParseDouble(row[1].GetString()),
                    High = ParseDouble(row[2].GetString()),
                    Low = ParseDouble(row[3].GetString()),
                    Close = ParseDouble(row[4].GetString()),
                    Volume = ParseDouble(row[5].GetString())
                });
            }
        }
        return list;
    }

    private async Task<List<KlineCandle>> FetchBitgetKlineAsync(string baseAsset, string interval, bool isFutures, CancellationToken ct)
    {
        string url;
        if (isFutures)
        {
            string gran = interval switch
            {
                "1m" => "1m", "5m" => "5m", "15m" => "15m",
                "1h" => "1H", "4h" => "4H", "1d" => "1D", "1w" => "1W", _ => "15m"
            };
            url = $"https://api.bitget.com/api/v2/mix/market/candles?symbol={baseAsset}USDT&productType=USDT-FUTURES&granularity={gran}&limit=200";
        }
        else
        {
            string gran = interval switch
            {
                "1m" => "1min", "5m" => "5min", "15m" => "15min",
                "1h" => "1h", "4h" => "4h", "1d" => "1day", "1w" => "1week", _ => "15min"
            };
            url = $"https://api.bitget.com/api/v2/spot/market/candles?symbol={baseAsset}USDT&granularity={gran}&limit=200";
        }

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<KlineCandle>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var row in data.EnumerateArray())
            {
                long sec = long.Parse(row[0].GetString() ?? "0") / 1000;
                var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
                list.Add(new KlineCandle
                {
                    TimeSec = sec,
                    TimeLabel = FormatTimeLabel(dt, interval),
                    Open = ParseDouble(row[1].GetString()),
                    High = ParseDouble(row[2].GetString()),
                    Low = ParseDouble(row[3].GetString()),
                    Close = ParseDouble(row[4].GetString()),
                    Volume = ParseDouble(row[5].GetString())
                });
            }
        }
        return list;
    }

    private async Task<List<KlineCandle>> FetchGateKlineAsync(string baseAsset, string interval, bool isFutures, CancellationToken ct)
    {
        string gIv = interval == "1w" ? "7d" : (interval is "1m" or "5m" or "15m" or "1h" or "4h" or "1d" ? interval : "15m");
        string pair = $"{baseAsset}_USDT";
        var list = new List<KlineCandle>();

        if (isFutures)
        {
            var json = await _http.GetStringAsync($"https://api.gateio.ws/api/v4/futures/usdt/candlesticks?contract={pair}&interval={gIv}&limit=200", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                long sec = el.GetProperty("t").GetInt64();
                var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
                list.Add(new KlineCandle
                {
                    TimeSec = sec,
                    TimeLabel = FormatTimeLabel(dt, interval),
                    Open = ParseDouble(el.GetProperty("o").GetString()),
                    High = ParseDouble(el.GetProperty("h").GetString()),
                    Low = ParseDouble(el.GetProperty("l").GetString()),
                    Close = ParseDouble(el.GetProperty("c").GetString()),
                    Volume = el.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0
                });
            }
        }
        else
        {
            // Gate Spot array order: [tSec, quoteVol, close, high, low, open, baseVol, closed]
            var json = await _http.GetStringAsync($"https://api.gateio.ws/api/v4/spot/candlesticks?currency_pair={pair}&interval={gIv}&limit=200", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                long sec = long.Parse(row[0].GetString() ?? "0");
                var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
                list.Add(new KlineCandle
                {
                    TimeSec = sec,
                    TimeLabel = FormatTimeLabel(dt, interval),
                    Open = ParseDouble(row[5].GetString()),
                    High = ParseDouble(row[3].GetString()),
                    Low = ParseDouble(row[4].GetString()),
                    Close = ParseDouble(row[2].GetString()),
                    Volume = row.GetArrayLength() > 6 ? ParseDouble(row[6].GetString()) : ParseDouble(row[1].GetString())
                });
            }
        }
        return list;
    }

    // =========================================================================
    //  FX / A-SHARES / US STOCKS (EastMoney + Yahoo Finance)
    // =========================================================================
    private async Task RefreshSingleNonCryptoQuoteAsync(WatchlistSymbol sym, CancellationToken ct)
    {
        if (sym.Provider == "eastmoney")
        {
            if (await TryRefreshEastMoneyQuoteAsync(sym, ct)) return;
            if (!string.IsNullOrWhiteSpace(sym.FallbackYahoo))
                await TryRefreshYahooQuoteAsync(sym, sym.FallbackYahoo, ct);
        }
        else
        {
            await TryRefreshYahooQuoteAsync(sym, sym.ProviderSymbol, ct);
        }
    }

    private async Task<bool> TryRefreshEastMoneyQuoteAsync(WatchlistSymbol sym, CancellationToken ct)
    {
        try
        {
            int fqt = sym.Category == "cn" ? 1 : 0;
            var secid = Uri.EscapeDataString(sym.ProviderSymbol);
            var url15 = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&klt=15&fqt={fqt}&end=20500101&lmt=30";
            var urlDay = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&klt=101&fqt={fqt}&end=20500101&lmt=2";

            var t15 = _http.GetStringAsync(url15, ct);
            var tDay = _http.GetStringAsync(urlDay, ct);
            await Task.WhenAll(t15, tDay);

            using var doc15 = JsonDocument.Parse(t15.Result);
            if (!doc15.RootElement.TryGetProperty("data", out var d15) || d15.ValueKind != JsonValueKind.Object) return false;
            var klines = d15.GetProperty("klines");
            if (klines.GetArrayLength() == 0) return false;

            string resolvedName = sym.Name;
            if (sym.IsCustom && d15.TryGetProperty("name", out var emName) && !string.IsNullOrWhiteSpace(emName.GetString()))
            {
                resolvedName = emName.GetString()!;
            }

            var spark = new List<double>();
            double lastClose = 0;
            foreach (var k in klines.EnumerateArray())
            {
                var parts = (k.GetString() ?? "").Split(',');
                if (parts.Length >= 5)
                {
                    double c = ParseDouble(parts[2]) * sym.Multiplier;
                    if (c > 0) { spark.Add(c); lastClose = c; }
                }
            }

            double prevClose = lastClose, dayHigh = lastClose, dayLow = lastClose;
            using var docDay = JsonDocument.Parse(tDay.Result);
            if (docDay.RootElement.TryGetProperty("data", out var dDay) && dDay.ValueKind == JsonValueKind.Object)
            {
                var dayLines = dDay.GetProperty("klines");
                int cnt = dayLines.GetArrayLength();
                if (cnt >= 1)
                {
                    var latest = (dayLines[cnt - 1].GetString() ?? "").Split(',');
                    if (latest.Length >= 5)
                    {
                        double c = ParseDouble(latest[2]) * sym.Multiplier;
                        if (c > 0) lastClose = c;
                        dayHigh = ParseDouble(latest[3]) * sym.Multiplier;
                        dayLow = ParseDouble(latest[4]) * sym.Multiplier;
                    }
                }
                if (cnt >= 2)
                {
                    var prior = (dayLines[cnt - 2].GetString() ?? "").Split(',');
                    if (prior.Length >= 3)
                    {
                        double pc = ParseDouble(prior[2]) * sym.Multiplier;
                        if (pc > 0) prevClose = pc;
                    }
                }
                else if (dDay.TryGetProperty("preKPrice", out var pkp) && pkp.TryGetDouble(out double pkVal) && pkVal > 0)
                {
                    prevClose = pkVal * sym.Multiplier;
                }
            }

            double change = lastClose - prevClose;
            double pct = prevClose > 0 ? change / prevClose * 100.0 : 0;

            _quotes[sym.Id] = new QuoteItem
            {
                Id = sym.Id,
                DisplayCode = sym.DisplayCode,
                Name = resolvedName,
                Category = sym.Category,
                Price = lastClose,
                PrevClose = prevClose,
                Change = change,
                ChangePercent = pct,
                High = dayHigh,
                Low = dayLow,
                Decimals = sym.Decimals,
                Source = sym.Category == "cn" ? "东方财富 A股" : "EastMoney",
                UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sparkline = spark,
                IsCustom = sym.IsCustom
            };
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> TryRefreshYahooQuoteAsync(WatchlistSymbol sym, string yahooSymbol, CancellationToken ct)
    {
        try
        {
            var enc = Uri.EscapeDataString(yahooSymbol);
            var tIntra = _http.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{enc}?interval=15m&range=2d", ct);
            var tDaily = _http.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{enc}?interval=1d&range=5d", ct);
            await Task.WhenAll(tIntra, tDaily);

            using var docIntra = JsonDocument.Parse(tIntra.Result);
            var resArr = docIntra.RootElement.GetProperty("chart").GetProperty("result");
            if (resArr.ValueKind != JsonValueKind.Array || resArr.GetArrayLength() == 0) return false;
            var res0 = resArr[0];
            var meta = res0.GetProperty("meta");

            double price = meta.TryGetProperty("regularMarketPrice", out var rmp) && rmp.TryGetDouble(out double pVal) ? pVal * sym.Multiplier : 0;
            double dayHigh = meta.TryGetProperty("regularMarketDayHigh", out var rmh) && rmh.TryGetDouble(out double hVal) ? hVal * sym.Multiplier : price;
            double dayLow = meta.TryGetProperty("regularMarketDayLow", out var rml) && rml.TryGetDouble(out double lVal) ? lVal * sym.Multiplier : price;
            double prevClose = meta.TryGetProperty("previousClose", out var pcProp) && pcProp.TryGetDouble(out double pcVal) && pcVal > 0 ? pcVal * sym.Multiplier : 0;

            var spark = new List<double>();
            if (res0.TryGetProperty("indicators", out var ind) && ind.TryGetProperty("quote", out var qArr)
                && qArr.GetArrayLength() > 0 && qArr[0].TryGetProperty("close", out var closes))
            {
                foreach (var c in closes.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double cv) && cv > 0)
                        spark.Add(cv * sym.Multiplier);
                if (spark.Count > 32) spark = spark.Skip(spark.Count - 32).ToList();
                if (price <= 0 && spark.Count > 0) price = spark[^1];
            }

            if (prevClose <= 0)
            {
                try
                {
                    using var docDaily = JsonDocument.Parse(tDaily.Result);
                    var dRes = docDaily.RootElement.GetProperty("chart").GetProperty("result")[0];
                    var dCloses = new List<double>();
                    if (dRes.TryGetProperty("indicators", out var dInd) && dInd.TryGetProperty("quote", out var dq)
                        && dq.GetArrayLength() > 0 && dq[0].TryGetProperty("close", out var dCloseArr))
                    {
                        foreach (var c in dCloseArr.EnumerateArray())
                            if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double cv) && cv > 0)
                                dCloses.Add(cv * sym.Multiplier);
                    }
                    if (dCloses.Count >= 2) prevClose = dCloses[^2];
                    else if (meta.TryGetProperty("chartPreviousClose", out var cpc) && cpc.TryGetDouble(out double cpcVal) && cpcVal > 0)
                        prevClose = cpcVal * sym.Multiplier;
                }
                catch { }
            }

            if (prevClose <= 0) prevClose = price;
            double change = price - prevClose;
            double pct = prevClose > 0 ? change / prevClose * 100.0 : 0;

            _quotes[sym.Id] = new QuoteItem
            {
                Id = sym.Id,
                DisplayCode = sym.DisplayCode,
                Name = sym.Name,
                Category = sym.Category,
                Price = price,
                PrevClose = prevClose,
                Change = change,
                ChangePercent = pct,
                High = dayHigh > 0 ? dayHigh : price,
                Low = dayLow > 0 ? dayLow : price,
                Decimals = sym.Decimals,
                Source = sym.Category == "us" ? "Yahoo 美股" : "Yahoo",
                UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sparkline = spark,
                IsCustom = sym.IsCustom
            };
            return true;
        }
        catch { return false; }
    }

    private async Task<KlinePayload?> FetchEastMoneyKlineAsync(WatchlistSymbol sym, string interval, CancellationToken ct)
    {
        bool aggregate4h = interval == "4h";
        int fqt = sym.Category == "cn" ? 1 : 0;
        int klt = interval switch
        {
            "1m" => 1, "5m" => 5, "15m" => 15, "1h" => 60, "4h" => 60, "1d" => 101, "1w" => 102, _ => 15
        };
        int limit = aggregate4h ? 480 : 220;

        var url = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={Uri.EscapeDataString(sym.ProviderSymbol)}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&klt={klt}&fqt={fqt}&end=20500101&lmt={limit}";
        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        var rawCandles = new List<KlineCandle>();
        foreach (var k in data.GetProperty("klines").EnumerateArray())
        {
            var p = (k.GetString() ?? "").Split(',');
            if (p.Length < 6) continue;
            if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) continue;

            var dto = new DateTimeOffset(dt);
            rawCandles.Add(new KlineCandle
            {
                TimeSec = dto.ToUnixTimeSeconds(),
                TimeLabel = FormatTimeLabel(dto, interval),
                Open = ParseDouble(p[1]) * sym.Multiplier,
                Close = ParseDouble(p[2]) * sym.Multiplier,
                High = ParseDouble(p[3]) * sym.Multiplier,
                Low = ParseDouble(p[4]) * sym.Multiplier,
                Volume = ParseDouble(p[5])
            });
        }

        var finalCandles = aggregate4h ? AggregateCandles(rawCandles, 4 * 3600, "4h") : rawCandles;
        string src = sym.Category == "cn" ? "东方财富 · A股" : "EastMoney";
        var payload = BuildPayload(sym, interval, src, finalCandles);
        if (sym.IsCustom && data.TryGetProperty("name", out var emName) && !string.IsNullOrWhiteSpace(emName.GetString()))
            payload.Name = emName.GetString()!;
        return payload;
    }

    private async Task<KlinePayload?> FetchYahooKlineAsync(WatchlistSymbol sym, string yahooSymbol, string interval, CancellationToken ct)
    {
        bool aggregate4h = interval == "4h";
        var (yInterval, yRange) = interval switch
        {
            "1m" => ("1m", "1d"),
            "5m" => ("5m", "5d"),
            "15m" => ("15m", "5d"),
            "1h" => ("60m", "1mo"),
            "4h" => ("60m", "3mo"),
            "1d" => ("1d", "9mo"),
            "1w" => ("1wk", "3y"),
            _ => ("15m", "5d")
        };

        var enc = Uri.EscapeDataString(yahooSymbol);
        var json = await _http.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{enc}?interval={yInterval}&range={yRange}", ct);
        using var doc = JsonDocument.Parse(json);

        var resArr = doc.RootElement.GetProperty("chart").GetProperty("result");
        if (resArr.ValueKind != JsonValueKind.Array || resArr.GetArrayLength() == 0) return null;
        var res = resArr[0];
        if (!res.TryGetProperty("timestamp", out var tsArr)) return null;

        var q0 = res.GetProperty("indicators").GetProperty("quote")[0];
        var opens = q0.GetProperty("open");
        var highs = q0.GetProperty("high");
        var lows = q0.GetProperty("low");
        var closes = q0.GetProperty("close");
        bool hasVol = q0.TryGetProperty("volume", out var vols);

        int len = tsArr.GetArrayLength();
        var rawCandles = new List<KlineCandle>(len);

        for (int i = 0; i < len; i++)
        {
            if (opens[i].ValueKind != JsonValueKind.Number || closes[i].ValueKind != JsonValueKind.Number) continue;
            double o = opens[i].GetDouble() * sym.Multiplier;
            double c = closes[i].GetDouble() * sym.Multiplier;
            double h = highs[i].ValueKind == JsonValueKind.Number ? highs[i].GetDouble() * sym.Multiplier : Math.Max(o, c);
            double l = lows[i].ValueKind == JsonValueKind.Number ? lows[i].GetDouble() * sym.Multiplier : Math.Min(o, c);
            double v = (hasVol && i < vols.GetArrayLength() && vols[i].ValueKind == JsonValueKind.Number) ? vols[i].GetDouble() : 0;
            if (o <= 0 || c <= 0) continue;

            long sec = tsArr[i].GetInt64();
            var dt = DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime();
            rawCandles.Add(new KlineCandle
            {
                TimeSec = sec,
                TimeLabel = FormatTimeLabel(dt, interval),
                Open = o,
                High = Math.Max(h, Math.Max(o, c)),
                Low = Math.Min(l, Math.Min(o, c)),
                Close = c,
                Volume = v
            });
        }

        var finalCandles = aggregate4h ? AggregateCandles(rawCandles, 4 * 3600, "4h") : rawCandles;
        if (finalCandles.Count > 240) finalCandles = finalCandles.Skip(finalCandles.Count - 240).ToList();
        string src = sym.Category == "us" ? "Yahoo · 美股" : "Yahoo";
        return BuildPayload(sym, interval, src, finalCandles);
    }

    // =========================================================================
    //  HELPERS
    // =========================================================================
    public static string NormalizeExchange(string? ex) => (ex ?? "binance").Trim().ToLowerInvariant() switch
    {
        "okx" => "okx",
        "bybit" => "bybit",
        "bitget" => "bitget",
        "gate" => "gate",
        _ => "binance"
    };

    public static string NormalizeMarketType(string? mt) =>
        string.Equals(mt, "spot", StringComparison.OrdinalIgnoreCase) ? "spot" : "futures";

    public static string FormatExchangeLabel(string? ex) => NormalizeExchange(ex) switch
    {
        "okx" => "OKX",
        "bybit" => "Bybit",
        "bitget" => "Bitget",
        "gate" => "Gate.io",
        _ => "Binance"
    };

    private static string GetCryptoBase(WatchlistSymbol sym)
    {
        string raw = !string.IsNullOrWhiteSpace(sym.BaseAsset) ? sym.BaseAsset : sym.ProviderSymbol;
        raw = raw.Trim().ToUpperInvariant().Replace("/", "").Replace("-", "").Replace("_", "");
        if (raw.EndsWith("USDT") && raw.Length > 4) raw = raw[..^4];
        return raw;
    }

    private static string FormatCryptoCode(WatchlistSymbol sym, string marketType)
    {
        if (sym.Provider != "crypto") return sym.DisplayCode;
        string b = GetCryptoBase(sym);
        return marketType == "futures" ? $"{b}/USDT 永续" : $"{b}/USDT";
    }

    private static int AutoDecimals(double price, int fallback)
    {
        if (price <= 0) return fallback;
        if (price < 0.01) return 6;
        if (price < 1.0) return 5;
        if (price < 10.0) return 4;
        if (price < 100.0) return 3;
        return Math.Min(fallback, 2);
    }

    private static List<KlineCandle> AggregateCandles(List<KlineCandle> input, long bucketSec, string interval)
    {
        var output = new List<KlineCandle>();
        KlineCandle? cur = null;
        long curBucket = -1;

        foreach (var c in input)
        {
            long bucket = (c.TimeSec / bucketSec) * bucketSec;
            if (cur == null || bucket != curBucket)
            {
                if (cur != null) output.Add(cur);
                curBucket = bucket;
                var dt = DateTimeOffset.FromUnixTimeSeconds(bucket).ToLocalTime();
                cur = new KlineCandle
                {
                    TimeSec = bucket,
                    TimeLabel = FormatTimeLabel(dt, interval),
                    Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume
                };
            }
            else
            {
                cur.High = Math.Max(cur.High, c.High);
                cur.Low = Math.Min(cur.Low, c.Low);
                cur.Close = c.Close;
                cur.Volume += c.Volume;
            }
        }
        if (cur != null) output.Add(cur);
        return output;
    }

    private static KlinePayload BuildPayload(WatchlistSymbol sym, string interval, string source, List<KlineCandle> candles)
    {
        double last = candles.Count > 0 ? candles[^1].Close : 0;
        double prev = candles.Count > 1 ? candles[0].Open : last;
        double high = candles.Count > 0 ? candles.Max(c => c.High) : 0;
        double low = candles.Count > 0 ? candles.Min(c => c.Low) : 0;

        return new KlinePayload
        {
            SymbolId = sym.Id,
            DisplayCode = sym.DisplayCode,
            Name = sym.Name,
            Category = sym.Category,
            Interval = interval,
            Decimals = sym.Decimals,
            Source = source,
            LastPrice = last,
            PrevClose = prev,
            Change = last - prev,
            ChangePercent = prev > 0 ? (last - prev) / prev * 100.0 : 0,
            High24h = high,
            Low24h = low,
            UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Candles = candles
        };
    }

    private static string FormatTimeLabel(DateTimeOffset dt, string interval)
    {
        return interval is "1d" or "1w" ? dt.ToString("yyyy-MM-dd") : dt.ToString("MM-dd HH:mm");
    }

    private static double ParseDouble(string? s)
    {
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static double? ParseNullableDouble(string? s)
    {
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
