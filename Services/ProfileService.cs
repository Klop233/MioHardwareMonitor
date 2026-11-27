using System.IO;
using System.Reflection;
using System.Text.Json;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

public sealed class ConfigProfileEnvelope
{
    public int SchemaVersion { get; set; } = 1;
    public string AppName { get; set; } = "MioHardwareMonitor";
    public string AppVersion { get; set; } = "";
    public string ExportedAtUtc { get; set; } = "";
    public bool IncludesDisplayPlacement { get; set; }
    public AppSettings Settings { get; set; } = new();
}

/// <summary>
/// Exports, previews, and imports versioned JSON configuration profiles so users can
/// share layouts, skins, HUD styles, and alert rules safely across machines.
/// </summary>
public static class ProfileService
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (bool Success, string? ErrorMessage) Export(string filePath, AppSettings current, bool includeDisplayPlacement)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (false, "导出文件路径为空。");

        try
        {
            var rawJson = JsonSerializer.Serialize(current, JsonOptions);
            var clone = JsonSerializer.Deserialize<AppSettings>(rawJson, JsonOptions) ?? new AppSettings();
            SettingsService.Normalize(clone);
            clone.ProcessTreePinnedPid = 0;

            if (!includeDisplayPlacement)
            {
                clone.TargetMonitorDevice = null;
                clone.DirectDisplayTargetId = null;
                clone.UseDirectDisplayExclusive = false;
                clone.ControlWindowLeft = null;
                clone.ControlWindowTop = null;
                clone.HasWindowOffset = false;
                clone.WindowLeft = 120;
                clone.WindowTop = 80;
                clone.WindowOffsetX = 80;
                clone.WindowOffsetY = 60;
                clone.WindowWidth = 1280;
                clone.WindowHeight = 760;
            }

            var envelope = new ConfigProfileEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                AppName = "MioHardwareMonitor",
                AppVersion = ReadAppVersion(),
                ExportedAtUtc = DateTime.UtcNow.ToString("O"),
                IncludesDisplayPlacement = includeDisplayPlacement,
                Settings = clone
            };

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string output = JsonSerializer.Serialize(envelope, JsonOptions);
            string tmp = filePath + ".tmp";
            File.WriteAllText(tmp, output);
            File.Move(tmp, filePath, overwrite: true);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"导出配置文件失败：{ex.Message}");
        }
    }

    public static (bool Success, string Summary, string? ErrorMessage) Preview(string filePath)
    {
        var parsed = LoadEnvelope(filePath);
        if (!parsed.Success || parsed.Envelope == null)
            return (false, "", parsed.ErrorMessage);

        var env = parsed.Envelope;
        var s = env.Settings;

        string skinName = SkinCatalog.GetDisplayName(s.Skin);
        bool isDark = string.Equals(s.Theme, "dark", StringComparison.OrdinalIgnoreCase);
        string themeText = isDark
            ? (s.OledTrueBlack ? "深色（OLED 纯黑）" : "深色")
            : "浅色";
        string styleText = s.HudStyle == "flat" ? "简洁平面" : "仪器仪表盘";
        string layoutText = s.LayoutMode switch
        {
            "custom" => "自定义排版",
            "split" => "综合看板",
            "market" => "行情专注",
            _ => "性能专注"
        };

        int visibleCount = s.CustomLayout?.Widgets?.Count(w =>
            w.Visible && (AppFeatures.MarketEnabled || !CustomLayoutConfig.IsMarketWidget(w.Id))) ?? 0;

        string alertText = s.Alerts != null && s.Alerts.Enabled
            ? $"已启用（CPU {s.Alerts.CpuTempWarnC}/{s.Alerts.CpuTempCritC}°C · GPU {s.Alerts.GpuTempWarnC}/{s.Alerts.GpuTempCritC}°C）"
            : "已关闭";
        string placementText = env.IncludesDisplayPlacement ? "包含屏幕/窗口位置" : "仅外观、布局与告警（保留本机屏幕位置）";

        string summary =
            $"皮肤：{skinName} · 主题：{themeText} · 风格：{styleText}\n" +
            $"视图：{layoutText}（自定义模块 {visibleCount} 个） · 告警：{alertText}\n" +
            $"范围：{placementText}";

        return (true, summary, null);
    }

    public static (bool Success, string? ErrorMessage) Import(string filePath, AppSettings target, bool applyDisplayPlacement)
    {
        if (target == null)
            return (false, "目标配置对象无效。");

        var parsed = LoadEnvelope(filePath);
        if (!parsed.Success || parsed.Envelope == null)
            return (false, parsed.ErrorMessage);

        var src = parsed.Envelope.Settings;
        SettingsService.Normalize(src);

        // Apply portable visual, telemetry, layout, and alert settings
        target.Theme = src.Theme;
        target.Skin = src.Skin;
        target.HudStyle = src.HudStyle;
        target.OledTrueBlack = src.OledTrueBlack;
        target.OledPixelShift = src.OledPixelShift;
        target.LayoutMode = src.LayoutMode;
        target.ColorScheme = src.ColorScheme;
        target.PerformanceMode = src.PerformanceMode;
        target.TelemetryIntervalMs = src.TelemetryIntervalMs;
        target.CpuViewMode = src.CpuViewMode;
        target.CpuDisplayTarget = src.CpuDisplayTarget;
        target.ProcessSortBy = src.ProcessSortBy;
        target.MonitorPage = src.MonitorPage;
        target.ProcessTreeViewMode = src.ProcessTreeViewMode;
        target.ProcessTreeSortBy = src.ProcessTreeSortBy;
        target.ProcessTreeAutoFollowTop = src.ProcessTreeAutoFollowTop;
        target.ProcessTreePinnedPid = 0;
        target.ProcessTreeFilter = src.ProcessTreeFilter;
        target.HideMonitorTopBar = src.HideMonitorTopBar;
        target.MinimizeToTray = src.MinimizeToTray;
        target.MaskPublicIp = src.MaskPublicIp;
        target.PingTargetHost = src.PingTargetHost;
        target.Alerts = src.Alerts.Clone();
        target.CustomLayout = src.CustomLayout.Clone();

        // Market/FX preferences
        target.FxAmount = src.FxAmount;
        target.FxFromCurrency = src.FxFromCurrency;
        target.FxToCurrency = src.FxToCurrency;
        target.ActiveSymbolId = src.ActiveSymbolId;
        target.ActiveCategory = src.ActiveCategory;
        target.ActiveInterval = src.ActiveInterval;
        target.CryptoExchange = src.CryptoExchange;
        target.CryptoMarketType = src.CryptoMarketType;
        target.ShowMA = src.ShowMA;
        target.ShowBoll = src.ShowBoll;
        target.ShowVolume = src.ShowVolume;
        target.CustomSymbols = src.CustomSymbols?
            .Select(sym => new WatchlistSymbol
            {
                Id = sym.Id,
                DisplayCode = sym.DisplayCode,
                Name = sym.Name,
                Category = sym.Category,
                Provider = sym.Provider,
                ProviderSymbol = sym.ProviderSymbol,
                BaseAsset = sym.BaseAsset,
                Decimals = sym.Decimals,
                IsCustom = sym.IsCustom
            })
            .ToList() ?? new List<WatchlistSymbol>();

        if (applyDisplayPlacement && parsed.Envelope.IncludesDisplayPlacement)
        {
            target.IsFullScreen = src.IsFullScreen;
            target.TargetMonitorDevice = src.TargetMonitorDevice;
            target.AlwaysOnTop = src.AlwaysOnTop;
            target.WindowPositionPreset = src.WindowPositionPreset;
            target.LockWindowPosition = src.LockWindowPosition;
            target.WindowEdgeMargin = src.WindowEdgeMargin;
            target.WindowOffsetX = src.WindowOffsetX;
            target.WindowOffsetY = src.WindowOffsetY;
            target.HasWindowOffset = src.HasWindowOffset;
            target.WindowLeft = src.WindowLeft;
            target.WindowTop = src.WindowTop;
            target.WindowWidth = src.WindowWidth;
            target.WindowHeight = src.WindowHeight;
            target.PinToAllVirtualDesktops = src.PinToAllVirtualDesktops;
            target.UseDirectDisplayExclusive = src.UseDirectDisplayExclusive;
            target.DirectDisplayTargetId = src.DirectDisplayTargetId;
            target.DirectDisplayFps = src.DirectDisplayFps;
            target.DirectDisplayRotation = src.DirectDisplayRotation;
            target.RenderMode = src.RenderMode;
        }

        SettingsService.Normalize(target);
        return (true, null);
    }

    private static (bool Success, ConfigProfileEnvelope? Envelope, string? ErrorMessage) LoadEnvelope(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return (false, null, "找不到指定的配置文件。");

        try
        {
            string json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (false, null, "配置文件格式无效（根元素不是 JSON 对象）。");

            ConfigProfileEnvelope envelope;
            if (root.TryGetProperty("settings", out var settingsEl) && settingsEl.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<ConfigProfileEnvelope>(json, JsonOptions);
                if (parsed == null || parsed.Settings == null)
                    return (false, null, "配置文件缺少有效的 settings 字段。");
                if (parsed.SchemaVersion > CurrentSchemaVersion)
                    return (false, null, $"该配置文件由更新版本生成（Schema v{parsed.SchemaVersion}），请先升级星澪硬件监控后再导入。");
                envelope = parsed;
            }
            else
            {
                // Tolerant fallback: allow importing a raw settings.json directly
                var rawSettings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (rawSettings == null)
                    return (false, null, "无法解析配置内容。");
                envelope = new ConfigProfileEnvelope
                {
                    SchemaVersion = CurrentSchemaVersion,
                    AppName = "MioHardwareMonitor",
                    IncludesDisplayPlacement = true,
                    Settings = rawSettings
                };
            }

            SettingsService.Normalize(envelope.Settings);
            return (true, envelope, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"读取配置文件失败：{ex.Message}");
        }
    }

    private static string ReadAppVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            var ver = asm.GetName().Version;
            if (ver != null) return $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }
        catch { }
        return "1.6.0";
    }
}
