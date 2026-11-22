using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SideScreenHUD.Models;
using SideScreenHUD.Native;
using SideScreenHUD.Services;

namespace SideScreenHUD;

public partial class ControlWindow : Window
{
    private static readonly (string Code, string Label)[] FxCurrencies =
    {
        ("USD", "USD 美元"),
        ("CNY", "CNY 人民币"),
        ("CNH", "CNH 离岸人民币"),
        ("EUR", "EUR 欧元"),
        ("JPY", "JPY 日元"),
        ("HKD", "HKD 港元"),
        ("GBP", "GBP 英镑"),
        ("KRW", "KRW 韩元"),
        ("AUD", "AUD 澳元"),
        ("CAD", "CAD 加元"),
        ("SGD", "SGD 新元"),
        ("CHF", "CHF 瑞郎"),
        ("TWD", "TWD 台币")
    };

    private readonly AppController _controller;
    private HwndSource? _hwndSource;
    private bool _suppressEvents;
    private bool _allowClose;
    private bool _initialPlaced;
    private string? _appliedTheme;
    private List<MonitorDescriptor> _monitors = new();
    private readonly System.Windows.Controls.Button[] _presetButtons;
    private readonly System.Windows.Controls.Button[] _navButtons;

    public ControlWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        _presetButtons = new[]
        {
            BtnPresetTopLeft, BtnPresetTopHalf, BtnPresetTopRight,
            BtnPresetLeftHalf, BtnPresetCenter, BtnPresetRightHalf,
            BtnPresetBottomLeft, BtnPresetBottomHalf, BtnPresetBottomRight
        };

        _navButtons = new[] { BtnNavWindow, BtnNavMonitor, BtnNavMarket };

        PopulateFxCurrencyCombos();
        ApplyThemeResources(_controller.Settings.Theme);
        UpdateAddSymbolPlaceholders();

        SourceInitialized += ControlWindow_SourceInitialized;
        Loaded += (_, _) =>
        {
            PlaceControlWindowOnce();
            RefreshFromController();
        };
        Closing += ControlWindow_Closing;
        _controller.StateChanged += Controller_StateChanged;
    }

    private void PopulateFxCurrencyCombos()
    {
        ComboFxFrom.Items.Clear();
        ComboFxTo.Items.Clear();
        foreach (var (code, label) in FxCurrencies)
        {
            ComboFxFrom.Items.Add(new ComboBoxItem { Content = label, Tag = code });
            ComboFxTo.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        }
    }

    private void ApplyThemeResources(string? theme)
    {
        string norm = SettingsService.NormalizeTheme(theme);
        bool isDark = norm == "dark";
        if (_appliedTheme == norm) return;
        _appliedTheme = norm;

        if (isDark)
        {
            SetBrush("BgPage", 0x0F, 0x11, 0x15);
            SetBrush("BgSurface", 0x17, 0x1A, 0x21);
            SetBrush("BgHover", 0x1F, 0x23, 0x2B);
            SetBrush("BorderToken", 0x2A, 0x2F, 0x3A);
            SetBrush("BorderStrong", 0x5E, 0x68, 0x78);
            SetBrush("TextMuted", 0xA1, 0xA9, 0xB6);
            SetBrush("TextMain", 0xE6, 0xE8, 0xEC);
            SetBrush("Accent", 0x3D, 0x6F, 0xE0);
            SetBrush("AccentHover", 0x33, 0x60, 0xCC);
            SetBrush("AccentText", 0x7B, 0xA3, 0xF2);
            SetBrush("AccentSubtle", 0x1B, 0x2A, 0x45);
            SetBrush("Success", 0x4C, 0xC3, 0x8A);
            SetBrush("SuccessSubtle", 0x15, 0x29, 0x1F);
            SetBrush("Danger", 0xF0, 0x70, 0x5F);
            SetBrush("DangerSubtle", 0x33, 0x19, 0x1A);
        }
        else
        {
            SetBrush("BgPage", 0xF7, 0xF8, 0xFA);
            SetBrush("BgSurface", 0xFF, 0xFF, 0xFF);
            SetBrush("BgHover", 0xF1, 0xF3, 0xF5);
            SetBrush("BorderToken", 0xE4, 0xE7, 0xEC);
            SetBrush("BorderStrong", 0x8A, 0x94, 0xA3);
            SetBrush("TextMuted", 0x5B, 0x64, 0x72);
            SetBrush("TextMain", 0x1F, 0x29, 0x37);
            SetBrush("Accent", 0x2B, 0x5F, 0xD9);
            SetBrush("AccentHover", 0x23, 0x4D, 0xB5);
            SetBrush("AccentText", 0x2B, 0x5F, 0xD9);
            SetBrush("AccentSubtle", 0xEA, 0xF1, 0xFB);
            SetBrush("Success", 0x1F, 0x7A, 0x4A);
            SetBrush("SuccessSubtle", 0xE7, 0xF4, 0xEC);
            SetBrush("Danger", 0xB4, 0x23, 0x18);
            SetBrush("DangerSubtle", 0xFD, 0xEC, 0xEA);
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
        }
    }

    private void SetBrush(string key, byte r, byte g, byte b)
    {
        Resources[key] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
    }

    private void ControlWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            bool isDark = SettingsService.NormalizeTheme(_controller.Settings.Theme) == "dark";
            Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        }
    }

    private void PlaceControlWindowOnce()
    {
        if (_initialPlaced) return;
        _initialPlaced = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32Interop.GetWindowRect(hwnd, out var curRect)) return;

        var s = _controller.Settings;
        int targetX;
        int targetY;

        if (s.ControlWindowLeft.HasValue &&
            s.ControlWindowTop.HasValue &&
            _controller.DisplayManager.IsPointVisibleOnAnyMonitor(s.ControlWindowLeft.Value, s.ControlWindowTop.Value))
        {
            targetX = (int)Math.Round(s.ControlWindowLeft.Value);
            targetY = (int)Math.Round(s.ControlWindowTop.Value);
        }
        else
        {
            var mon = _controller.DisplayManager.ResolveControlWindowMonitor(s.TargetMonitorDevice);
            if (mon == null) return;
            targetX = mon.WorkLeft + Math.Max(0, (mon.WorkWidth - curRect.Width) / 2);
            targetY = mon.WorkTop + Math.Max(0, (mon.WorkHeight - curRect.Height) / 2);
        }

        Win32Interop.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            targetX,
            targetY,
            0,
            0,
            Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE);

        _controller.SyncControlWindowTopMost();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_EXITSIZEMOVE)
        {
            if (Win32Interop.GetWindowRect(hwnd, out var rect) && !Win32Interop.IsIconic(hwnd))
            {
                _controller.SaveControlWindowPosition(rect.Left, rect.Top);
            }
        }
        return IntPtr.Zero;
    }

    public void ForceClose()
    {
        _allowClose = true;
        _controller.StateChanged -= Controller_StateChanged;
        try
        {
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }
        catch { }
        Close();
    }

    private void Controller_StateChanged()
    {
        RefreshFromController();
    }

    private void BtnNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tab) return;

        PanelWindow.Visibility = tab == "window" ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitor.Visibility = tab == "monitor" ? Visibility.Visible : Visibility.Collapsed;
        PanelMarket.Visibility = tab == "market" ? Visibility.Visible : Visibility.Collapsed;

        var normalStyle = (Style)FindResource("NavButton");
        var activeStyle = (Style)FindResource("ActiveNavButton");
        foreach (var b in _navButtons)
        {
            b.Style = ReferenceEquals(b, btn) ? activeStyle : normalStyle;
        }
    }

    public void RefreshFromController()
    {
        _suppressEvents = true;
        try
        {
            var s = _controller.Settings;
            ApplyThemeResources(s.Theme);

            _monitors = _controller.DisplayManager.GetMonitors();
            var targetMon = _controller.CurrentDisplayMonitor
                            ?? _controller.TargetMonitor
                            ?? _monitors.FirstOrDefault();

            Win32Interop.RECT effectiveRect = targetMon != null
                ? DisplayManager.CalculateFixedWindowRect(targetMon, s)
                : new Win32Interop.RECT
                {
                    Left = s.WindowOffsetX,
                    Top = s.WindowOffsetY,
                    Right = s.WindowOffsetX + (int)s.WindowWidth,
                    Bottom = s.WindowOffsetY + (int)s.WindowHeight
                };

            // Status bar
            if (_controller.IsDisplayOpen)
            {
                StatusBadge.SetResourceReference(Border.BackgroundProperty, "SuccessSubtle");
                StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                StatusBadgeText.Text = "运行中";
                BtnOpenOrRecreate.Content = "重建监控窗口";
                BtnCloseDisplay.IsEnabled = true;

                var mon = _controller.CurrentDisplayMonitor;
                string modeStr = s.IsFullScreen
                    ? "占用整块屏幕"
                    : $"固定窗口 · {FormatPreset(s.WindowPositionPreset)} {effectiveRect.Width}×{effectiveRect.Height}";
                StatusDetailText.Text = mon != null
                    ? $"{mon.FriendlyName} · {modeStr}"
                    : modeStr;
            }
            else
            {
                StatusBadge.SetResourceReference(Border.BackgroundProperty, "BorderToken");
                StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                StatusBadgeText.Text = "已关闭";
                BtnOpenOrRecreate.Content = "打开监控窗口";
                BtnCloseDisplay.IsEnabled = false;
                StatusDetailText.Text = "监控窗口未打开，点击右侧「打开监控窗口」启动";
            }

            // Monitors combo
            ComboMonitors.Items.Clear();
            int selectedMonIdx = 0;
            for (int i = 0; i < _monitors.Count; i++)
            {
                var m = _monitors[i];
                ComboMonitors.Items.Add(new ComboBoxItem { Content = m.FriendlyName, Tag = m.DeviceName });
                if (string.Equals(m.DeviceName, s.TargetMonitorDevice, StringComparison.OrdinalIgnoreCase))
                    selectedMonIdx = i;
            }
            if (ComboMonitors.Items.Count > 0)
                ComboMonitors.SelectedIndex = selectedMonIdx;

            if (targetMon != null)
            {
                MonitorGeometryHint.Text =
                    $"工作区 {targetMon.WorkWidth} × {targetMon.WorkHeight} px · 全屏 {targetMon.Width} × {targetMon.Height} px · 缩放 {Math.Round(targetMon.ScaleFactor * 100)}%";
            }

            // Display Mode & Fixed Window Position Panel
            RadioFullScreen.IsChecked = s.IsFullScreen;
            RadioWindowed.IsChecked = !s.IsFullScreen;

            FixedWindowPanel.IsEnabled = !s.IsFullScreen;
            FixedWindowPanel.Opacity = s.IsFullScreen ? 0.55 : 1.0;

            string activePreset = SettingsService.NormalizePositionPreset(s.WindowPositionPreset);
            PresetStatusText.Text = s.IsFullScreen
                ? "当前：占用整屏"
                : $"当前：{FormatPreset(activePreset)}";

            var normalPreset = (Style)FindResource("PresetButton");
            var activePresetStyle = (Style)FindResource("ActivePresetButton");
            foreach (var btn in _presetButtons)
            {
                bool isSelected = !s.IsFullScreen &&
                                  string.Equals(btn.Tag?.ToString(), activePreset, StringComparison.OrdinalIgnoreCase);
                btn.Style = isSelected ? activePresetStyle : normalPreset;
            }

            int relX = targetMon != null ? effectiveRect.Left - targetMon.WorkLeft : s.WindowOffsetX;
            int relY = targetMon != null ? effectiveRect.Top - targetMon.WorkTop : s.WindowOffsetY;

            TxtPosX.Text = relX.ToString();
            TxtPosY.Text = relY.ToString();
            TxtPosW.Text = effectiveRect.Width.ToString();
            TxtPosH.Text = effectiveRect.Height.ToString();
            TxtMargin.Text = s.WindowEdgeMargin.ToString();

            BtnCaptureCurrentPos.IsEnabled = _controller.IsDisplayOpen && !s.IsFullScreen;

            ChkLockPosition.IsChecked = s.LockWindowPosition;
            if (s.LockWindowPosition)
            {
                LockHintText.Text = "已锁定：监控窗口严格固定在设定坐标，重启后自动恢复。";
                LockHintText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            }
            else
            {
                LockHintText.Text = "位置已解锁：可直接拖动监控窗口顶栏或拉伸边缘调整，坐标实时同步。";
                LockHintText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            }

            // General Window Options
            ChkTopMost.IsChecked = s.AlwaysOnTop;
            ChkHideTopBar.IsChecked = s.HideMonitorTopBar;
            ChkShowInTaskbar.IsChecked = s.ShowDisplayInTaskbar;
            SelectComboByTag(ComboRenderMode, s.RenderMode ?? "safe");

            // Appearance & Hardware Monitor
            SelectComboByTag(ComboLayout, s.LayoutMode ?? "split");
            SelectComboByTag(ComboTheme, SettingsService.NormalizeTheme(s.Theme));
            SelectComboByTag(ComboColors, s.ColorScheme ?? "cn");
            SelectComboByTag(ComboInterval, s.TelemetryIntervalMs.ToString());
            SelectComboByTag(ComboCpuView, s.CpuViewMode ?? "cores");
            SelectComboByTag(ComboProcSort, s.ProcessSortBy ?? "cpu");

            // Network IP readout
            var netIp = _controller.LastNetworkIp;
            if (netIp != null)
            {
                BtnRefreshIp.IsEnabled = !netIp.IsChecking;
                BtnRefreshIp.Content = netIp.IsChecking ? "检测中" : "刷新 IP";
                TxtDomesticIpInfo.Text = FormatIpSummary(netIp.Domestic, netIp.IsChecking);
                TxtOverseasIpInfo.Text = FormatIpSummary(netIp.Overseas, netIp.IsChecking);
            }

            // Startup & Elevation
            ChkAutoOpenDisplay.IsChecked = s.AutoOpenDisplayOnStartup;
            ChkShowControl.IsChecked = s.ShowControlOnStartup;
            ChkMinTray.IsChecked = s.MinimizeToTray;
            ChkOled.IsChecked = s.OledPixelShift;

            if (_controller.IsElevated)
            {
                ElevateText.Text = "已以管理员身份运行，完整 CPU / GPU 传感器已解锁。";
                BtnElevate.Visibility = Visibility.Collapsed;
            }
            else
            {
                ElevateText.Text = "CPU 封装温度需管理员权限（GPU 温度已免提权支持）。";
                BtnElevate.Visibility = Visibility.Visible;
            }

            // Market, K-Line, Exchange & Active Symbol
            SelectComboByTag(ComboCategory, s.ActiveCategory ?? "crypto");
            SelectComboByTag(ComboKlineInterval, s.ActiveInterval ?? "15m");
            SelectComboByTag(ComboCryptoExchange, s.CryptoExchange ?? "binance");
            SelectComboByTag(ComboCryptoType, s.CryptoMarketType ?? "futures");

            var allSymbols = _controller.GetAllSymbols();
            ComboActiveSymbol.Items.Clear();
            int activeSymIdx = 0;
            for (int i = 0; i < allSymbols.Count; i++)
            {
                var sym = allSymbols[i];
                ComboActiveSymbol.Items.Add(new ComboBoxItem
                {
                    Content = $"{sym.DisplayCode} · {sym.Name}",
                    Tag = sym.Id
                });
                if (string.Equals(sym.Id, s.ActiveSymbolId, StringComparison.OrdinalIgnoreCase))
                    activeSymIdx = i;
            }
            if (ComboActiveSymbol.Items.Count > 0)
                ComboActiveSymbol.SelectedIndex = activeSymIdx;

            ChkShowMA.IsChecked = s.ShowMA;
            ChkShowBoll.IsChecked = s.ShowBoll;
            ChkShowVol.IsChecked = s.ShowVolume;

            // FX Converter settings
            if (!TxtFxAmount.IsKeyboardFocused)
                TxtFxAmount.Text = s.FxAmount.ToString("G");
            SelectComboByTag(ComboFxFrom, s.FxFromCurrency ?? "USD");
            SelectComboByTag(ComboFxTo, s.FxToCurrency ?? "CNY");

            // Custom symbols list
            CustomSymbolsList.ItemsSource = null;
            CustomSymbolsList.ItemsSource = s.CustomSymbols.ToList();
            NoCustomHint.Visibility = s.CustomSymbols.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static string FormatIpSummary(IpEndpointInfo? ep, bool isChecking)
    {
        if (ep != null && ep.Ok && !string.IsNullOrWhiteSpace(ep.Ip))
        {
            return string.IsNullOrWhiteSpace(ep.Location) ? ep.Ip : $"{ep.Ip}（{ep.Location}）";
        }
        return isChecking ? "检测中" : "未连通";
    }

    private void UpdateAddSymbolPlaceholders()
    {
        string mode = GetSelectedTag(ComboAddProvider, "crypto");
        switch (mode)
        {
            case "cn_stock":
                TxtAddCode.Tag = "6 位代码，例如 601318 / 000001";
                TxtAddFormatHint.Text = "示例：沪深 A 股 6 位代码，如 601318（中国平安）、000001";
                break;
            case "us_stock":
                TxtAddCode.Tag = "例如 PLTR / MSTR / 0700.HK";
                TxtAddFormatHint.Text = "示例：美股填代码如 PLTR、MSTR；港股加 .HK 如 0700.HK";
                break;
            case "fx":
                TxtAddCode.Tag = "例如 AUDCNY=X / GBPUSD=X";
                TxtAddFormatHint.Text = "示例：外汇货币对，如 AUDCNY=X、GBPUSD=X、USDCAD=X";
                break;
            default:
                TxtAddCode.Tag = "例如 PEPE / ADA / ARB / TON";
                TxtAddFormatHint.Text = "示例：输入币种简称即可，如 PEPE、ADA、ARB、TON";
                break;
        }
        TxtAddName.Tag = "留空则默认使用代码";
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetSelectedTag(System.Windows.Controls.ComboBox combo, string fallback)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    }

    private static string FormatPreset(string? preset) => SettingsService.NormalizePositionPreset(preset) switch
    {
        "top-left" => "左上角",
        "top-right" => "右上角",
        "bottom-left" => "左下角",
        "bottom-right" => "右下角",
        "center" => "屏幕居中",
        "left-half" => "左半屏",
        "right-half" => "右半屏",
        "top-half" => "上半屏",
        "bottom-half" => "下半屏",
        _ => "自定义坐标"
    };

    private void BtnOpenOrRecreate_Click(object sender, RoutedEventArgs e) => _ = _controller.RecreateDisplayWindowAsync();
    private void BtnCloseDisplay_Click(object sender, RoutedEventArgs e) => _controller.CloseDisplayWindow();
    private void BtnToggleFullScreen_Click(object sender, RoutedEventArgs e) => _controller.ToggleFullScreen();
    private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshFromController();
    private void BtnRefreshIp_Click(object sender, RoutedEventArgs e) => _controller.RefreshNetworkIpNow();
    private void BtnRefreshMarket_Click(object sender, RoutedEventArgs e) => _controller.RefreshMarketNow();
    private void BtnCaptureCurrentPos_Click(object sender, RoutedEventArgs e) => _controller.CaptureCurrentDisplayGeometry();
    private void BtnElevate_Click(object sender, RoutedEventArgs e) => _controller.RestartAsAdministrator();
    private void BtnHideToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _controller.NotifyHiddenToTrayOnce();
    }
    private void BtnExitAll_Click(object sender, RoutedEventArgs e) => _controller.ShutdownApplication();

    private void ComboMonitors_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        string dev = GetSelectedTag(ComboMonitors, "");
        if (!string.IsNullOrWhiteSpace(dev)) _controller.SwitchMonitor(dev);
    }

    private void RadioMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetFullScreen(RadioFullScreen.IsChecked == true);
    }

    private void BtnPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string preset)
        {
            if (int.TryParse(TxtMargin.Text.Trim(), out int margin))
                _controller.Settings.WindowEdgeMargin = Math.Clamp(margin, 0, 200);
            if (int.TryParse(TxtPosW.Text.Trim(), out int w) && w >= 420)
                _controller.Settings.WindowWidth = Math.Clamp(w, 420, 7680);
            if (int.TryParse(TxtPosH.Text.Trim(), out int h) && h >= 300)
                _controller.Settings.WindowHeight = Math.Clamp(h, 300, 4320);

            _controller.SetWindowPositionPreset(preset);
        }
    }

    private void BtnApplyCoords_Click(object sender, RoutedEventArgs e) => ApplyCoordinateInputs();

    private void PosInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ReferenceEquals(sender, TxtMargin) &&
                !string.Equals(_controller.Settings.WindowPositionPreset, "custom", StringComparison.OrdinalIgnoreCase))
            {
                int margin = int.TryParse(TxtMargin.Text.Trim(), out int m) ? m : _controller.Settings.WindowEdgeMargin;
                _controller.SetWindowEdgeMargin(margin);
            }
            else
            {
                ApplyCoordinateInputs();
            }
            e.Handled = true;
        }
    }

    private void ApplyCoordinateInputs()
    {
        var s = _controller.Settings;
        int x = int.TryParse(TxtPosX.Text.Trim(), out int parsedX) ? parsedX : s.WindowOffsetX;
        int y = int.TryParse(TxtPosY.Text.Trim(), out int parsedY) ? parsedY : s.WindowOffsetY;
        int w = int.TryParse(TxtPosW.Text.Trim(), out int parsedW) ? parsedW : (int)s.WindowWidth;
        int h = int.TryParse(TxtPosH.Text.Trim(), out int parsedH) ? parsedH : (int)s.WindowHeight;
        int margin = int.TryParse(TxtMargin.Text.Trim(), out int parsedM) ? parsedM : s.WindowEdgeMargin;

        _controller.SetCustomWindowGeometry(x, y, w, h, margin);
    }

    private void BtnQuickSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string raw) return;
        var parts = raw.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) return;

        var s = _controller.Settings;
        string currentPreset = SettingsService.NormalizePositionPreset(s.WindowPositionPreset);
        bool isHalfPreset = currentPreset is "left-half" or "right-half" or "top-half" or "bottom-half";

        s.WindowWidth = w;
        s.WindowHeight = h;

        if (isHalfPreset || currentPreset == "custom")
        {
            int x = int.TryParse(TxtPosX.Text.Trim(), out int px) ? px : s.WindowOffsetX;
            int y = int.TryParse(TxtPosY.Text.Trim(), out int py) ? py : s.WindowOffsetY;
            int margin = int.TryParse(TxtMargin.Text.Trim(), out int pm) ? pm : s.WindowEdgeMargin;
            _controller.SetCustomWindowGeometry(x, y, w, h, margin);
        }
        else
        {
            _controller.SetWindowPositionPreset(currentPreset);
        }
    }

    private void ChkLockPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetLockWindowPosition(ChkLockPosition.IsChecked == true);
    }

    private void ComboRenderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetRenderMode(GetSelectedTag(ComboRenderMode, "safe"));
    }

    private void ChkTopMost_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetAlwaysOnTop(ChkTopMost.IsChecked == true);
    }

    private void ChkShowInTaskbar_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetShowDisplayInTaskbar(ChkShowInTaskbar.IsChecked == true);
    }

    private void ComboVisual_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        string layout = GetSelectedTag(ComboLayout, "split");
        string theme = GetSelectedTag(ComboTheme, "light");
        string colors = GetSelectedTag(ComboColors, "cn");
        int interval = int.TryParse(GetSelectedTag(ComboInterval, "1000"), out int ms) ? ms : 1000;
        string cpuView = GetSelectedTag(ComboCpuView, "cores");
        string procSort = GetSelectedTag(ComboProcSort, "cpu");

        _controller.UpdateSettings(s =>
        {
            s.LayoutMode = layout;
            s.Theme = theme;
            s.ColorScheme = colors;
            s.TelemetryIntervalMs = interval;
            s.CpuViewMode = cpuView;
            s.ProcessSortBy = procSort;
        });
    }

    private void ChkOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.UpdateSettings(s =>
        {
            s.HideMonitorTopBar = ChkHideTopBar.IsChecked == true;
            s.ShowMA = ChkShowMA.IsChecked == true;
            s.ShowBoll = ChkShowBoll.IsChecked == true;
            s.ShowVolume = ChkShowVol.IsChecked == true;
            s.AutoOpenDisplayOnStartup = ChkAutoOpenDisplay.IsChecked == true;
            s.ShowControlOnStartup = ChkShowControl.IsChecked == true;
            s.MinimizeToTray = ChkMinTray.IsChecked == true;
            s.OledPixelShift = ChkOled.IsChecked == true;
        });
    }

    private void ComboCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SelectCategory(GetSelectedTag(ComboCategory, "crypto"));
    }

    private void ComboKlineInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SelectInterval(GetSelectedTag(ComboKlineInterval, "15m"));
    }

    private void ComboActiveSymbol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        string id = GetSelectedTag(ComboActiveSymbol, "");
        if (!string.IsNullOrWhiteSpace(id)) _controller.SelectSymbol(id);
    }

    private void ComboCrypto_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetCryptoConfig(
            GetSelectedTag(ComboCryptoExchange, "binance"),
            GetSelectedTag(ComboCryptoType, "futures"));
    }

    private void ComboFx_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        ApplyFxSettings();
    }

    private void FxAmount_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyFxSettings();
            e.Handled = true;
        }
    }

    private void FxAmount_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ApplyFxSettings();
    }

    private void BtnFxSwap_Click(object sender, RoutedEventArgs e)
    {
        string from = GetSelectedTag(ComboFxFrom, "USD");
        string to = GetSelectedTag(ComboFxTo, "CNY");
        _controller.UpdateSettings(s =>
        {
            s.FxFromCurrency = to;
            s.FxToCurrency = from;
        });
    }

    private void ApplyFxSettings()
    {
        double amt = double.TryParse(TxtFxAmount.Text.Trim(), out double v) && v >= 0 ? v : _controller.Settings.FxAmount;
        string from = GetSelectedTag(ComboFxFrom, "USD");
        string to = GetSelectedTag(ComboFxTo, "CNY");
        _controller.UpdateSettings(s =>
        {
            s.FxAmount = amt;
            s.FxFromCurrency = from;
            s.FxToCurrency = to;
        });
    }

    private void ComboAddProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TxtAddCode == null) return;
        UpdateAddSymbolPlaceholders();
        ClearAddError();
    }

    private void AddSymbolInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        ClearAddError();
        if (e.Key == Key.Enter)
        {
            SubmitCustomSymbol();
            e.Handled = true;
        }
    }

    private void BtnAddSymbol_Click(object sender, RoutedEventArgs e) => SubmitCustomSymbol();

    private void SubmitCustomSymbol()
    {
        string code = TxtAddCode.Text.Trim();
        string name = TxtAddName.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            TxtAddError.Text = "请输入标的代码后再添加（可参考输入框中的格式示例）。";
            TxtAddError.Visibility = Visibility.Visible;
            TxtAddCode.SetResourceReference(BorderBrushProperty, "Danger");
            TxtAddCode.Focus();
            return;
        }

        ClearAddError();
        string mode = GetSelectedTag(ComboAddProvider, "crypto");
        _controller.AddCustomSymbol(code, name, mode);
        TxtAddCode.Text = "";
        TxtAddName.Text = "";
    }

    private void ClearAddError()
    {
        if (TxtAddError == null) return;
        TxtAddError.Visibility = Visibility.Collapsed;
        TxtAddCode.SetResourceReference(BorderBrushProperty, "BorderStrong");
    }

    private void BtnRemoveSymbol_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string id)
        {
            _controller.RemoveCustomSymbol(id);
        }
    }

    private void ControlWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && Win32Interop.GetWindowRect(hwnd, out var rect) && !Win32Interop.IsIconic(hwnd))
        {
            _controller.SaveControlWindowPosition(rect.Left, rect.Top);
        }

        if (_allowClose) return;

        if (_controller.Settings.MinimizeToTray || _controller.IsDisplayOpen)
        {
            e.Cancel = true;
            Hide();
            _controller.NotifyHiddenToTrayOnce();
            return;
        }

        _controller.ShutdownApplication();
    }
}
