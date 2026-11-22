using System.IO;
using System.Text.Json;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _settingsPath;

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "SideScreenHUD");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (parsed != null)
                {
                    parsed.TelemetryIntervalMs = Math.Clamp(parsed.TelemetryIntervalMs, 500, 5000);
                    parsed.WindowEdgeMargin = Math.Clamp(parsed.WindowEdgeMargin, 0, 200);
                    parsed.WindowWidth = Math.Clamp(parsed.WindowWidth, 420, 7680);
                    parsed.WindowHeight = Math.Clamp(parsed.WindowHeight, 300, 4320);
                    parsed.WindowPositionPreset = NormalizePositionPreset(parsed.WindowPositionPreset);
                    parsed.Theme = NormalizeTheme(parsed.Theme);
                    parsed.LayoutMode = NormalizeLayoutMode(parsed.LayoutMode);
                    parsed.CpuViewMode = string.Equals(parsed.CpuViewMode, "overall", StringComparison.OrdinalIgnoreCase) ? "overall" : "cores";
                    parsed.CpuDisplayTarget = NormalizeCpuDisplayTarget(parsed.CpuDisplayTarget);
                    parsed.ProcessSortBy = string.Equals(parsed.ProcessSortBy, "mem", StringComparison.OrdinalIgnoreCase) ? "mem" : "cpu";
                    parsed.CustomLayout = NormalizeCustomLayout(parsed.CustomLayout);
                    parsed.IgnoredUpdateVersion = (parsed.IgnoredUpdateVersion ?? "").Trim();
                    parsed.DonationApiBaseUrl = DonationService.NormalizeBaseUrl(parsed.DonationApiBaseUrl);
                    parsed.DirectDisplayFps = Math.Clamp(parsed.DirectDisplayFps, 2, 30);
                    if (parsed.FxAmount < 0 || double.IsNaN(parsed.FxAmount) || double.IsInfinity(parsed.FxAmount)) parsed.FxAmount = 100;
                    if (string.IsNullOrWhiteSpace(parsed.FxFromCurrency)) parsed.FxFromCurrency = "USD";
                    if (string.IsNullOrWhiteSpace(parsed.FxToCurrency)) parsed.FxToCurrency = "CNY";
                    parsed.CustomSymbols ??= new List<WatchlistSymbol>();
                    if (string.IsNullOrWhiteSpace(parsed.RenderMode)) parsed.RenderMode = "safe";
                    if (string.IsNullOrWhiteSpace(parsed.CryptoExchange)) parsed.CryptoExchange = "binance";
                    if (string.IsNullOrWhiteSpace(parsed.CryptoMarketType)) parsed.CryptoMarketType = "futures";
                    if (parsed.ActiveCategory == "market") parsed.ActiveCategory = "us";
                    parsed.ActiveSymbolId = parsed.ActiveSymbolId switch
                    {
                        "BTCUSDT" => "BTC",
                        "ETHUSDT" => "ETH",
                        "SOLUSDT" => "SOL",
                        "BNBUSDT" => "BNB",
                        "SSE" => "CN_SSE",
                        "IXIC" => "US_IXIC",
                        "GSPC" => "US_GSPC",
                        "NVDA" => "US_NVDA",
                        "AMD" => "US_AMD",
                        "" or null => "BTC",
                        var x => x
                    };
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupted file
        }
        return new AppSettings();
    }

    public static string NormalizeTheme(string? theme) =>
        string.Equals(theme?.Trim(), "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";

    public static string NormalizeLayoutMode(string? mode) => (mode ?? "split").Trim().ToLowerInvariant() switch
    {
        "hardware" => "hardware",
        "market" => "market",
        "custom" => "custom",
        _ => "split"
    };

    public static string NormalizeCpuDisplayTarget(string? target)
    {
        string raw = (target ?? "auto").Trim().ToLowerInvariant();
        if (raw is "auto" or "all" or "combined") return raw;
        if (int.TryParse(raw, out int idx) && idx >= 0 && idx <= 15) return idx.ToString();
        return "auto";
    }

    public static CustomLayoutConfig NormalizeCustomLayout(CustomLayoutConfig? layout)
    {
        var fallback = CustomLayoutConfig.CreatePreset("split");
        if (layout == null || layout.Widgets == null || layout.Widgets.Count == 0)
            return fallback;

        int cols = Math.Clamp(layout.Columns, 4, 16);
        int rows = Math.Clamp(layout.Rows, 2, 16);
        int gap = Math.Clamp(layout.GapPx, 4, 24);

        var byId = new Dictionary<string, DashboardWidgetLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in layout.Widgets)
        {
            if (string.IsNullOrWhiteSpace(w.Id)) continue;
            byId[w.Id.Trim().ToLowerInvariant()] = w;
        }

        var normalized = new List<DashboardWidgetLayout>();
        foreach (var (id, _, _) in CustomLayoutConfig.AllWidgets)
        {
            var src = byId.TryGetValue(id, out var found)
                ? found
                : fallback.Widgets.First(x => x.Id == id);

            int colSpan = Math.Clamp(src.ColSpan, 1, cols);
            int rowSpan = Math.Clamp(src.RowSpan, 1, rows);
            int col = Math.Clamp(src.Col, 1, Math.Max(1, cols - colSpan + 1));
            int row = Math.Clamp(src.Row, 1, Math.Max(1, rows - rowSpan + 1));

            normalized.Add(new DashboardWidgetLayout
            {
                Id = id,
                Visible = src.Visible,
                Col = col,
                Row = row,
                ColSpan = colSpan,
                RowSpan = rowSpan
            });
        }

        return new CustomLayoutConfig
        {
            Columns = cols,
            Rows = rows,
            GapPx = gap,
            Widgets = normalized
        };
    }

    public static string NormalizePositionPreset(string? preset) => (preset ?? "custom").Trim().ToLowerInvariant() switch
    {
        "top-left" => "top-left",
        "top-right" => "top-right",
        "bottom-left" => "bottom-left",
        "bottom-right" => "bottom-right",
        "center" => "center",
        "left-half" => "left-half",
        "right-half" => "right-half",
        "top-half" => "top-half",
        "bottom-half" => "bottom-half",
        _ => "custom"
    };

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
