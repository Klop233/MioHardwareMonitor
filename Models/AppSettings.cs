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
    public string LayoutMode { get; set; } = "split"; // split | hardware | market | custom
    public string ColorScheme { get; set; } = "cn"; // cn | global
    public int TelemetryIntervalMs { get; set; } = 1000;
    public string CpuViewMode { get; set; } = "cores"; // cores | overall
    public string CpuDisplayTarget { get; set; } = "auto"; // auto | all | combined | "0" | "1" | ...
    public string ProcessSortBy { get; set; } = "cpu"; // cpu | mem
    public bool OledPixelShift { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;

    // Software Update Preferences
    public bool AutoCheckUpdates { get; set; } = true;
    public string IgnoredUpdateVersion { get; set; } = "";

    // Custom Dashboard Layout Editor configuration (12x8 grid by default)
    public CustomLayoutConfig CustomLayout { get; set; } = CustomLayoutConfig.CreatePreset("split");

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

public sealed class DashboardWidgetLayout
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int Col { get; set; } = 1;
    public int Row { get; set; } = 1;
    public int ColSpan { get; set; } = 6;
    public int RowSpan { get; set; } = 2;

    public DashboardWidgetLayout Clone() => new()
    {
        Id = Id,
        Visible = Visible,
        Col = Col,
        Row = Row,
        ColSpan = ColSpan,
        RowSpan = RowSpan
    };
}

public sealed class CustomLayoutConfig
{
    public static readonly (string Id, string Title, string ShortTitle)[] AllWidgets =
    {
        ("cpu", "CPU 监控（支持多路）", "CPU"),
        ("gpu", "GPU 监控", "GPU"),
        ("ram", "系统内存", "内存"),
        ("network", "网络与出口 IPv4", "网络"),
        ("disk", "存储与读写", "存储"),
        ("processes", "高占用进程", "进程"),
        ("kline", "K 线图表", "K 线"),
        ("watchlist", "行情列表", "行情"),
        ("fx", "汇率换算", "汇率")
    };

    public int Columns { get; set; } = 12;
    public int Rows { get; set; } = 8;
    public int GapPx { get; set; } = 12;
    public List<DashboardWidgetLayout> Widgets { get; set; } = new();

    public CustomLayoutConfig Clone() => new()
    {
        Columns = Columns,
        Rows = Rows,
        GapPx = GapPx,
        Widgets = Widgets.Select(w => w.Clone()).ToList()
    };

    public static string GetWidgetTitle(string id)
    {
        foreach (var item in AllWidgets)
        {
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                return item.Title;
        }
        return id;
    }

    public static CustomLayoutConfig CreatePreset(string presetName)
    {
        return (presetName ?? "split").Trim().ToLowerInvariant() switch
        {
            "hardware" => new CustomLayoutConfig
            {
                Columns = 12,
                Rows = 8,
                GapPx = 12,
                Widgets = new List<DashboardWidgetLayout>
                {
                    new() { Id = "cpu",       Visible = true,  Col = 1, Row = 1, ColSpan = 7, RowSpan = 5 },
                    new() { Id = "processes", Visible = true,  Col = 1, Row = 6, ColSpan = 7, RowSpan = 3 },
                    new() { Id = "gpu",       Visible = true,  Col = 8, Row = 1, ColSpan = 5, RowSpan = 2 },
                    new() { Id = "ram",       Visible = true,  Col = 8, Row = 3, ColSpan = 5, RowSpan = 2 },
                    new() { Id = "network",   Visible = true,  Col = 8, Row = 5, ColSpan = 5, RowSpan = 2 },
                    new() { Id = "disk",      Visible = true,  Col = 8, Row = 7, ColSpan = 5, RowSpan = 2 },
                    new() { Id = "kline",     Visible = false, Col = 1, Row = 1, ColSpan = 6, RowSpan = 4 },
                    new() { Id = "watchlist", Visible = false, Col = 7, Row = 1, ColSpan = 6, RowSpan = 4 },
                    new() { Id = "fx",        Visible = false, Col = 1, Row = 8, ColSpan = 6, RowSpan = 1 }
                }
            },
            "portrait" => new CustomLayoutConfig
            {
                Columns = 6,
                Rows = 12,
                GapPx = 10,
                Widgets = new List<DashboardWidgetLayout>
                {
                    new() { Id = "cpu",       Visible = true,  Col = 1, Row = 1,  ColSpan = 6, RowSpan = 3 },
                    new() { Id = "gpu",       Visible = true,  Col = 1, Row = 4,  ColSpan = 3, RowSpan = 2 },
                    new() { Id = "ram",       Visible = true,  Col = 4, Row = 4,  ColSpan = 3, RowSpan = 2 },
                    new() { Id = "network",   Visible = true,  Col = 1, Row = 6,  ColSpan = 3, RowSpan = 2 },
                    new() { Id = "disk",      Visible = true,  Col = 4, Row = 6,  ColSpan = 3, RowSpan = 2 },
                    new() { Id = "kline",     Visible = true,  Col = 1, Row = 8,  ColSpan = 6, RowSpan = 3 },
                    new() { Id = "processes", Visible = true,  Col = 1, Row = 11, ColSpan = 6, RowSpan = 2 },
                    new() { Id = "watchlist", Visible = false, Col = 1, Row = 8,  ColSpan = 6, RowSpan = 3 },
                    new() { Id = "fx",        Visible = false, Col = 1, Row = 12, ColSpan = 6, RowSpan = 1 }
                }
            },
            "bar" => new CustomLayoutConfig
            {
                Columns = 12,
                Rows = 4,
                GapPx = 10,
                Widgets = new List<DashboardWidgetLayout>
                {
                    new() { Id = "cpu",       Visible = true,  Col = 1,  Row = 1, ColSpan = 5, RowSpan = 4 },
                    new() { Id = "gpu",       Visible = true,  Col = 6,  Row = 1, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "ram",       Visible = true,  Col = 9,  Row = 1, ColSpan = 4, RowSpan = 2 },
                    new() { Id = "network",   Visible = true,  Col = 6,  Row = 3, ColSpan = 4, RowSpan = 2 },
                    new() { Id = "disk",      Visible = true,  Col = 10, Row = 3, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "processes", Visible = false, Col = 1,  Row = 1, ColSpan = 4, RowSpan = 2 },
                    new() { Id = "kline",     Visible = false, Col = 1,  Row = 1, ColSpan = 6, RowSpan = 4 },
                    new() { Id = "watchlist", Visible = false, Col = 7,  Row = 1, ColSpan = 6, RowSpan = 4 },
                    new() { Id = "fx",        Visible = false, Col = 1,  Row = 4, ColSpan = 6, RowSpan = 1 }
                }
            },
            "trading" => new CustomLayoutConfig
            {
                Columns = 12,
                Rows = 8,
                GapPx = 12,
                Widgets = new List<DashboardWidgetLayout>
                {
                    new() { Id = "kline",     Visible = true,  Col = 1, Row = 1, ColSpan = 8, RowSpan = 6 },
                    new() { Id = "watchlist", Visible = true,  Col = 9, Row = 1, ColSpan = 4, RowSpan = 6 },
                    new() { Id = "cpu",       Visible = true,  Col = 1, Row = 7, ColSpan = 4, RowSpan = 2 },
                    new() { Id = "network",   Visible = true,  Col = 5, Row = 7, ColSpan = 4, RowSpan = 2 },
                    new() { Id = "fx",        Visible = true,  Col = 9, Row = 7, ColSpan = 4, RowSpan = 2 },
                    new() { Id = "gpu",       Visible = false, Col = 1, Row = 7, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "ram",       Visible = false, Col = 4, Row = 7, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "disk",      Visible = false, Col = 7, Row = 7, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "processes", Visible = false, Col = 1, Row = 7, ColSpan = 6, RowSpan = 2 }
                }
            },
            _ => new CustomLayoutConfig
            {
                Columns = 12,
                Rows = 8,
                GapPx = 12,
                Widgets = new List<DashboardWidgetLayout>
                {
                    new() { Id = "cpu",       Visible = true, Col = 1, Row = 1, ColSpan = 6, RowSpan = 3 },
                    new() { Id = "gpu",       Visible = true, Col = 1, Row = 4, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "ram",       Visible = true, Col = 4, Row = 4, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "network",   Visible = true, Col = 1, Row = 6, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "disk",      Visible = true, Col = 4, Row = 6, ColSpan = 3, RowSpan = 2 },
                    new() { Id = "processes", Visible = true, Col = 1, Row = 8, ColSpan = 6, RowSpan = 1 },
                    new() { Id = "kline",     Visible = true, Col = 7, Row = 1, ColSpan = 6, RowSpan = 4 },
                    new() { Id = "watchlist", Visible = true, Col = 7, Row = 5, ColSpan = 6, RowSpan = 3 },
                    new() { Id = "fx",        Visible = true, Col = 7, Row = 8, ColSpan = 6, RowSpan = 1 }
                }
            }
        };
    }
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
