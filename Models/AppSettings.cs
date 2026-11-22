namespace SideScreenHUD.Models;

public sealed class AppSettings
{
    public bool IsFullScreen { get; set; } = false;
    public string? TargetMonitorDevice { get; set; }
    public bool AlwaysOnTop { get; set; } = true;

    // Fixed Window Position settings (physical pixels, relative to target monitor WorkArea)
    // Presets: custom | top-left | top-right | bottom-left | bottom-right | center | left-half | right-half | top-half | bottom-half
    public string WindowPositionPreset { get; set; } = "custom";
    public bool LockWindowPosition { get; set; } = true;
    public int WindowEdgeMargin { get; set; } = 0;
    public int WindowOffsetX { get; set; } = 80;
    public int WindowOffsetY { get; set; } = 60;
    public bool HasWindowOffset { get; set; } = false;

    public double WindowLeft { get; set; } = 120;
    public double WindowTop { get; set; } = 80;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;

    // Monitor Window chrome & separation options
    public bool HideMonitorTopBar { get; set; } = false;
    public bool ShowDisplayInTaskbar { get; set; } = true;

    // Control (Settings) Window remembered position
    public double? ControlWindowLeft { get; set; }
    public double? ControlWindowTop { get; set; }

    // Cross-GPU rendering & dual-window behavior
    // "safe": --disable-gpu-compositing --disable-direct-composition (immune to mixed AMD/Intel GPU black screen)
    // "nodcomp": --disable-direct-composition
    // "gpu": native Chromium GPU compositing
    public string RenderMode { get; set; } = "safe";
    public bool ShowControlOnStartup { get; set; } = true;
    public bool AutoOpenDisplayOnStartup { get; set; } = true;

    // UI Preferences (MioLab Web UI Spec v1.0)
    public string Theme { get; set; } = "light"; // light | dark
    public string LayoutMode { get; set; } = "split"; // split | hardware | market
    public string ColorScheme { get; set; } = "cn"; // cn | global
    public int TelemetryIntervalMs { get; set; } = 1000;
    public string CpuViewMode { get; set; } = "cores"; // cores | overall
    public string ProcessSortBy { get; set; } = "cpu"; // cpu | mem
    public bool OledPixelShift { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;

    // FX Converter display preferences (configured in Settings Window, displayed read-only on Monitor Window)
    public double FxAmount { get; set; } = 100;
    public string FxFromCurrency { get; set; } = "USD";
    public string FxToCurrency { get; set; } = "CNY";

    // Market & Crypto Exchange Preferences
    public string ActiveSymbolId { get; set; } = "BTC";
    public string ActiveCategory { get; set; } = "crypto"; // fx | crypto | cn | us | all
    public string ActiveInterval { get; set; } = "15m"; // 1m | 5m | 15m | 1h | 4h | 1d | 1w
    public string CryptoExchange { get; set; } = "binance"; // binance | okx | bybit | bitget | gate
    public string CryptoMarketType { get; set; } = "futures"; // spot | futures
    public bool ShowMA { get; set; } = true;
    public bool ShowBoll { get; set; } = false;
    public bool ShowVolume { get; set; } = true;
    public List<WatchlistSymbol> CustomSymbols { get; set; } = new();
}

public sealed class MonitorDescriptor
{
    public int Index { get; set; }
    public string DeviceName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public bool IsPrimary { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WorkLeft { get; set; }
    public int WorkTop { get; set; }
    public int WorkWidth { get; set; }
    public int WorkHeight { get; set; }
    public int RefreshRateHz { get; set; }
    public double ScaleFactor { get; set; } = 1.0;
}
