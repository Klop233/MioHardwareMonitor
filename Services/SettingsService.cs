using System.IO;
using System.Net;
using System.Net.Sockets;
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

    private readonly object _saveLock = new();
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
                    Normalize(parsed);
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupted file
        }
        var defaults = new AppSettings();
        Normalize(defaults);
        return defaults;
    }

    public static void Normalize(AppSettings parsed)
    {
        if (double.IsNaN(parsed.WindowLeft) || double.IsInfinity(parsed.WindowLeft)) parsed.WindowLeft = 120;
        if (double.IsNaN(parsed.WindowTop) || double.IsInfinity(parsed.WindowTop)) parsed.WindowTop = 80;
        if (double.IsNaN(parsed.WindowWidth) || double.IsInfinity(parsed.WindowWidth)) parsed.WindowWidth = 1280;
        if (double.IsNaN(parsed.WindowHeight) || double.IsInfinity(parsed.WindowHeight)) parsed.WindowHeight = 760;
        if (parsed.ControlWindowLeft.HasValue && (double.IsNaN(parsed.ControlWindowLeft.Value) || double.IsInfinity(parsed.ControlWindowLeft.Value)))
            parsed.ControlWindowLeft = null;
        if (parsed.ControlWindowTop.HasValue && (double.IsNaN(parsed.ControlWindowTop.Value) || double.IsInfinity(parsed.ControlWindowTop.Value)))
            parsed.ControlWindowTop = null;

        parsed.TelemetryIntervalMs = Math.Clamp(parsed.TelemetryIntervalMs, 500, 5000);
        parsed.WindowEdgeMargin = Math.Clamp(parsed.WindowEdgeMargin, 0, 200);
        parsed.WindowWidth = Math.Clamp(parsed.WindowWidth, 280, 7680);
        parsed.WindowHeight = Math.Clamp(parsed.WindowHeight, 180, 4320);
        parsed.WindowPositionPreset = NormalizePositionPreset(parsed.WindowPositionPreset);
        parsed.Theme = NormalizeTheme(parsed.Theme);
        parsed.Skin = SkinCatalog.Normalize(parsed.Skin);
        parsed.HudStyle = NormalizeHudStyle(parsed.HudStyle);
        parsed.ColorScheme = string.Equals(parsed.ColorScheme?.Trim(), "global", StringComparison.OrdinalIgnoreCase) ? "global" : "cn";
        parsed.PerformanceMode = NormalizePerformanceMode(parsed.PerformanceMode);
        parsed.LayoutMode = ApplyFeatureGates(NormalizeLayoutMode(parsed.LayoutMode));
        parsed.CpuViewMode = NormalizeCpuViewMode(parsed.CpuViewMode);
        parsed.CpuDisplayTarget = NormalizeCpuDisplayTarget(parsed.CpuDisplayTarget);
        parsed.ProcessSortBy = string.Equals(parsed.ProcessSortBy, "mem", StringComparison.OrdinalIgnoreCase) ? "mem" : "cpu";
        parsed.MonitorPage = NormalizeMonitorPage(parsed.MonitorPage);
        parsed.ProcessTreeViewMode = NormalizeProcessTreeViewMode(parsed.ProcessTreeViewMode);
        parsed.ProcessTreeSortBy = NormalizeProcessTreeSortBy(parsed.ProcessTreeSortBy);
        if (parsed.ProcessTreePinnedPid < 0) parsed.ProcessTreePinnedPid = 0;
        parsed.ProcessTreeFilter = NormalizeProcessTreeFilter(parsed.ProcessTreeFilter);
        parsed.CustomLayout = NormalizeCustomLayout(parsed.CustomLayout);
        parsed.IgnoredUpdateVersion = (parsed.IgnoredUpdateVersion ?? "").Trim();
        parsed.DonationApiBaseUrl = DonationService.NormalizeBaseUrl(parsed.DonationApiBaseUrl);
        parsed.DirectDisplayFps = Math.Clamp(parsed.DirectDisplayFps, 2, 30);
        parsed.DirectDisplayRotation = DirectDisplaySession.NormalizeRotation(parsed.DirectDisplayRotation);
        parsed.RenderMode = NormalizeRenderMode(parsed.RenderMode);
        parsed.PingTargetHost = NormalizePingTarget(parsed.PingTargetHost);
        parsed.Alerts = NormalizeAlerts(parsed.Alerts);
        parsed.ShowDisplayInTaskbar = false;

        if (parsed.FxAmount < 0 || double.IsNaN(parsed.FxAmount) || double.IsInfinity(parsed.FxAmount)) parsed.FxAmount = 100;
        if (string.IsNullOrWhiteSpace(parsed.FxFromCurrency)) parsed.FxFromCurrency = "USD";
        if (string.IsNullOrWhiteSpace(parsed.FxToCurrency)) parsed.FxToCurrency = "CNY";
        parsed.CustomSymbols ??= new List<WatchlistSymbol>();
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
    }

    public static string NormalizeTheme(string? theme) =>
        string.Equals(theme?.Trim(), "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";

    public static string NormalizeHudStyle(string? style) =>
        string.Equals(style?.Trim(), "flat", StringComparison.OrdinalIgnoreCase) ? "flat" : "instrument";

    public static string NormalizeRenderMode(string? mode) => (mode ?? "safe").Trim().ToLowerInvariant() switch
    {
        "gpu" => "gpu",
        "nodcomp" => "nodcomp",
        _ => "safe"
    };

    public static string NormalizePingTarget(string? host)
    {
        const string fallback = "223.5.5.5";
        if (string.IsNullOrWhiteSpace(host)) return fallback;

        string s = host.Trim();
        int schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];
        int slashIdx = s.IndexOfAny(new[] { '/', '?', '#' });
        if (slashIdx >= 0) s = s[..slashIdx];
        s = s.Trim();

        if (s.Length == 0 || s.Length > 128) return fallback;
        if (IPAddress.TryParse(s, out _)) return s;

        if (s.StartsWith('.') || s.EndsWith('.') || s.StartsWith('-') || s.EndsWith('-'))
            return fallback;

        foreach (char ch in s)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '-'))
                return fallback;
        }
        return s;
    }

    public static AlertSettings NormalizeAlerts(AlertSettings? alerts)
    {
        var src = alerts ?? new AlertSettings();
        int cpuWarn = Math.Clamp(src.CpuTempWarnC, 50, 115);
        int cpuCrit = Math.Clamp(src.CpuTempCritC, Math.Max(55, cpuWarn + 2), 120);
        int gpuWarn = Math.Clamp(src.GpuTempWarnC, 50, 110);
        int gpuCrit = Math.Clamp(src.GpuTempCritC, Math.Max(55, gpuWarn + 2), 115);

        return new AlertSettings
        {
            Enabled = src.Enabled,
            TrayNotifyEnabled = src.TrayNotifyEnabled,
            CooldownSeconds = Math.Clamp(src.CooldownSeconds, 15, 600),
            CpuTempWarnC = cpuWarn,
            CpuTempCritC = cpuCrit,
            GpuTempWarnC = gpuWarn,
            GpuTempCritC = gpuCrit,
            RamUsageWarnPct = Math.Clamp(src.RamUsageWarnPct, 50, 99),
            DiskUsageWarnPct = Math.Clamp(src.DiskUsageWarnPct, 60, 99)
        };
    }

    public static string MaskIpAddress(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return ip ?? "";
        string trimmed = ip.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = parsed.ToString().Split('.');
            if (parts.Length == 4)
            {
                return $"{parts[0]}.•••.•••.{parts[3]}";
            }
        }
        return ip;
    }

    public static string NormalizeLayoutMode(string? mode) => (mode ?? "split").Trim().ToLowerInvariant() switch
    {
        "hardware" => "hardware",
        "market" => "market",
        "custom" => "custom",
        _ => "split"
    };

    /// <summary>Falls back to a hardware-only layout when the market surface is feature-gated off.</summary>
    public static string ApplyFeatureGates(string layoutMode)
    {
        if (!AppFeatures.MarketEnabled && (layoutMode == "market" || layoutMode == "split"))
            return "hardware";
        return layoutMode;
    }

    public static string NormalizePerformanceMode(string? mode) => (mode ?? "balanced").Trim().ToLowerInvariant() switch
    {
        "eco" => "eco",
        "realtime" => "realtime",
        _ => "balanced"
    };

    /// <summary>Minimum sampling interval (ms) for heavy sensors (LibreHardwareMonitor, GPU PDH, processes).</summary>
    public static int HeavySampleIntervalMs(string? mode) => NormalizePerformanceMode(mode) switch
    {
        "eco" => 6000,
        "realtime" => 1000,
        _ => 3000
    };

    public static int RecommendedTelemetryIntervalMs(string? mode) => NormalizePerformanceMode(mode) switch
    {
        "eco" => 3000,
        "realtime" => 500,
        _ => 1500
    };

    public static string NormalizeCpuViewMode(string? mode) => (mode ?? "compact").Trim().ToLowerInvariant() switch
    {
        "cores" => "cores",
        "overall" => "overall",
        _ => "compact"
    };

    public static string NormalizeMonitorPage(string? page) =>
        string.Equals(page?.Trim(), "proctree", StringComparison.OrdinalIgnoreCase) ? "proctree" : "overview";

    public static string NormalizeProcessTreeViewMode(string? mode) =>
        string.Equals(mode?.Trim(), "flat", StringComparison.OrdinalIgnoreCase) ? "flat" : "tree";

    public static string NormalizeProcessTreeSortBy(string? sortBy) => (sortBy ?? "cpu").Trim().ToLowerInvariant() switch
    {
        "mem" => "mem",
        "io" => "io",
        "threads" => "threads",
        "handles" => "handles",
        "pid" => "pid",
        "name" => "name",
        _ => "cpu"
    };

    public static string NormalizeProcessTreeFilter(string? filter)
    {
        string trimmed = (filter ?? "").Trim();
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    public static string NormalizeCpuDisplayTarget(string? target)
    {
        string raw = (target ?? "auto").Trim().ToLowerInvariant();
        if (raw is "auto" or "all" or "combined") return raw;
        if (int.TryParse(raw, out int idx) && idx >= 0 && idx <= 15) return idx.ToString();
        return "auto";
    }

    public static CustomLayoutConfig NormalizeCustomLayout(CustomLayoutConfig? layout)
    {
        var fallback = CustomLayoutConfig.CreatePreset(CustomLayoutConfig.DefaultPreset);
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

        bool isMigration = byId.Count > 0;
        var normalized = new List<DashboardWidgetLayout>();
        foreach (var (id, _, _) in CustomLayoutConfig.AllWidgets)
        {
            bool hasExisting = byId.TryGetValue(id, out var found);
            var src = hasExisting
                ? found!
                : fallback.Widgets.First(x => x.Id == id);

            int colSpan = Math.Clamp(src.ColSpan, 1, cols);
            int rowSpan = Math.Clamp(src.RowSpan, 1, rows);
            int col = Math.Clamp(src.Col, 1, Math.Max(1, cols - colSpan + 1));
            int row = Math.Clamp(src.Row, 1, Math.Max(1, rows - rowSpan + 1));
            bool visible = (!AppFeatures.MarketEnabled && CustomLayoutConfig.IsMarketWidget(id))
                ? false
                : (!hasExisting && isMigration ? false : src.Visible);

            normalized.Add(new DashboardWidgetLayout
            {
                Id = id,
                Visible = visible,
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
        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(Current, JsonOptions);
                string tmpPath = _settingsPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _settingsPath, overwrite: true);
            }
            catch
            {
                // Ignore write errors
            }
        }
    }
}
