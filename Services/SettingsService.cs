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
                    parsed.CpuViewMode = string.Equals(parsed.CpuViewMode, "overall", StringComparison.OrdinalIgnoreCase) ? "overall" : "cores";
                    parsed.ProcessSortBy = string.Equals(parsed.ProcessSortBy, "mem", StringComparison.OrdinalIgnoreCase) ? "mem" : "cpu";
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
