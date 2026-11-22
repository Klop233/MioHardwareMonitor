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
    private readonly System.Windows.Controls.Button[] _donateAmountButtons;
    private System.Windows.Threading.DispatcherTimer? _donatePollTimer;
    private System.Windows.Threading.DispatcherTimer? _donateCountdownTimer;
    private string _activeDonateOrderId = "";
    private DateTimeOffset? _activeDonateExpireAt;
    private bool _donatePollInFlight;
    private string _selectedWidgetId = "cpu";
    private string? _dragWidgetId;
    private bool _isResizingWidget;
    private System.Windows.Point _dragStartPt;
    private int _dragStartCol, _dragStartRow, _dragStartColSpan, _dragStartRowSpan;
    private bool _dragMutated;

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

        _navButtons = new[] { BtnNavWindow, BtnNavMonitor, BtnNavLayout, BtnNavMarket, BtnNavDonate };
        _donateAmountButtons = new[] { BtnDonate6, BtnDonate18, BtnDonate36, BtnDonate66, BtnDonate128, BtnDonateCustom };

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
        StopDonatePolling();
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
        SwitchToTab(tab);
    }

    public void SwitchToTab(string tab)
    {
        PanelWindow.Visibility = tab == "window" ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitor.Visibility = tab == "monitor" ? Visibility.Visible : Visibility.Collapsed;
        PanelLayout.Visibility = tab == "layout" ? Visibility.Visible : Visibility.Collapsed;
        PanelMarket.Visibility = tab == "market" ? Visibility.Visible : Visibility.Collapsed;
        PanelDonate.Visibility = tab == "donate" ? Visibility.Visible : Visibility.Collapsed;

        var normalStyle = (Style)FindResource("NavButton");
        var activeStyle = (Style)FindResource("ActiveNavButton");
        foreach (var b in _navButtons)
        {
            bool match = string.Equals(b.Tag?.ToString(), tab, StringComparison.OrdinalIgnoreCase);
            b.Style = match ? activeStyle : normalStyle;
        }

        if (tab == "layout")
        {
            Dispatcher.BeginInvoke(RenderLayoutCanvas, System.Windows.Threading.DispatcherPriority.Loaded);
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
                StatusBadgeText.Text = _controller.IsDirectDisplayActive ? "硬件独占中" : "运行中";
                BtnOpenOrRecreate.Content = "重建监控窗口";
                BtnCloseDisplay.IsEnabled = true;

                if (_controller.IsDirectDisplayActive && _controller.ActiveDirectDisplayTarget != null)
                {
                    StatusDetailText.Text = $"{_controller.ActiveDirectDisplayTarget.FriendlyName} · 脱离桌面直出";
                }
                else
                {
                    var mon = _controller.CurrentDisplayMonitor;
                    string modeStr = s.IsFullScreen
                        ? "占用整块屏幕"
                        : $"固定窗口 · {FormatPreset(s.WindowPositionPreset)} {effectiveRect.Width}×{effectiveRect.Height}";
                    StatusDetailText.Text = mon != null
                        ? $"{mon.FriendlyName} · {modeStr}"
                        : modeStr;
                }
            }
            else
            {
                StatusBadge.SetResourceReference(Border.BackgroundProperty, "BorderToken");
                StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                StatusBadgeText.Text = "已关闭";
                BtnOpenOrRecreate.Content = "打开监控窗口";
                BtnCloseDisplay.IsEnabled = false;
                StatusDetailText.Text = !string.IsNullOrWhiteSpace(_controller.DirectDisplayErrorMessage)
                    ? _controller.DirectDisplayErrorMessage
                    : "监控窗口未打开，点击右侧「打开监控窗口」启动";
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

            // Display Mode & Fixed Window / Direct Display Panel
            RadioDirectExclusive.IsChecked = s.UseDirectDisplayExclusive;
            RadioFullScreen.IsChecked = !s.UseDirectDisplayExclusive && s.IsFullScreen;
            RadioWindowed.IsChecked = !s.UseDirectDisplayExclusive && !s.IsFullScreen;

            DirectDisplayPanel.Visibility = s.UseDirectDisplayExclusive ? Visibility.Visible : Visibility.Collapsed;
            FixedWindowPanel.Visibility = s.UseDirectDisplayExclusive ? Visibility.Collapsed : Visibility.Visible;

            FixedWindowPanel.IsEnabled = !s.IsFullScreen && !s.UseDirectDisplayExclusive;
            FixedWindowPanel.Opacity = s.IsFullScreen ? 0.55 : 1.0;

            RefreshDirectDisplayTargetsUi(s);

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
            ChkPinAllDesktops.IsChecked = s.PinToAllVirtualDesktops;
            ChkHideTopBar.IsChecked = s.HideMonitorTopBar;
            SelectComboByTag(ComboRenderMode, s.RenderMode ?? "safe");

            // Appearance & Hardware Monitor
            SelectComboByTag(ComboLayout, s.LayoutMode ?? "split");
            SelectComboByTag(ComboTheme, SettingsService.NormalizeTheme(s.Theme));
            SelectComboByTag(ComboColors, s.ColorScheme ?? "cn");
            SelectComboByTag(ComboInterval, s.TelemetryIntervalMs.ToString());
            SelectComboByTag(ComboCpuView, s.CpuViewMode ?? "cores");
            SelectComboByTag(ComboProcSort, s.ProcessSortBy ?? "cpu");
            RefreshCpuDisplayTargetCombo(s);
            RefreshLayoutEditorUi(s);

            // Network IP readout
            var netIp = _controller.LastNetworkIp;
            if (netIp != null)
            {
                BtnRefreshIp.IsEnabled = !netIp.IsChecking;
                BtnRefreshIp.Content = netIp.IsChecking ? "检测中" : "刷新 IP";
                TxtDomesticIpInfo.Text = FormatIpSummary(netIp.Domestic, netIp.IsChecking);
                TxtOverseasIpInfo.Text = FormatIpSummary(netIp.Overseas, netIp.IsChecking);
            }

            // Startup, Updates & Elevation
            ChkAutoStart.IsChecked = _controller.IsAutoStartEnabled;
            TxtAutoStartSummary.Text = _controller.GetStartupRestoreSummary();
            ChkAutoOpenDisplay.IsChecked = s.AutoOpenDisplayOnStartup;
            ChkShowControl.IsChecked = s.ShowControlOnStartup;
            ChkMinTray.IsChecked = s.MinimizeToTray;
            ChkOled.IsChecked = s.OledPixelShift;
            ChkAutoCheckUpdates.IsChecked = s.AutoCheckUpdates;
            RefreshUpdateSectionUi(s);

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

            // Donation API endpoint
            if (!TxtDonateApiUrl.IsKeyboardFocused)
                TxtDonateApiUrl.Text = DonationService.NormalizeBaseUrl(s.DonationApiBaseUrl);
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
        if (RadioDirectExclusive.IsChecked == true)
        {
            _controller.SetDisplayMode(useDirectExclusive: true, isFullScreen: true);
        }
        else if (RadioFullScreen.IsChecked == true)
        {
            _controller.SetDisplayMode(useDirectExclusive: false, isFullScreen: true);
        }
        else
        {
            _controller.SetDisplayMode(useDirectExclusive: false, isFullScreen: false);
        }
    }

    private void RefreshDirectDisplayTargetsUi(AppSettings s)
    {
        var targets = DirectDisplayService.EnumerateTargets();
        ComboDirectTargets.Items.Clear();
        int selectedIdx = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            ComboDirectTargets.Items.Add(new ComboBoxItem { Content = t.FriendlyName, Tag = t.TargetKey });
            if (!string.IsNullOrWhiteSpace(s.DirectDisplayTargetId) &&
                (string.Equals(t.TargetKey, s.DirectDisplayTargetId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.StableMonitorId, s.DirectDisplayTargetId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedIdx = i;
            }
        }

        if (ComboDirectTargets.Items.Count > 0)
            ComboDirectTargets.SelectedIndex = selectedIdx;

        SelectComboByTag(ComboDirectFps, s.DirectDisplayFps.ToString());

        int specializedCount = targets.Count(t => t.IsSpecialized);
        if (_controller.IsDirectDisplayActive && _controller.ActiveDirectDisplayTarget != null)
        {
            DirectStatusPill.SetResourceReference(Border.BackgroundProperty, "SuccessSubtle");
            DirectStatusPillText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            DirectStatusPillText.Text = "已硬件独占";
            DirectInfoBox.SetResourceReference(Border.BorderBrushProperty, "Success");
            TxtDirectDisplayStatus.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            TxtDirectDisplayStatus.Text = $"硬件直出运行中：{_controller.ActiveDirectDisplayTarget.FriendlyName} · 已彻底脱离 Windows 桌面（切虚拟桌面、Win+D、全屏游戏均完全不受影响）。";
        }
        else if (!string.IsNullOrWhiteSpace(_controller.DirectDisplayErrorMessage))
        {
            DirectStatusPill.SetResourceReference(Border.BackgroundProperty, "DangerSubtle");
            DirectStatusPillText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            DirectStatusPillText.Text = "独占失败";
            DirectInfoBox.SetResourceReference(Border.BorderBrushProperty, "Danger");
            TxtDirectDisplayStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            TxtDirectDisplayStatus.Text = _controller.DirectDisplayErrorMessage;
        }
        else if (specializedCount > 0)
        {
            DirectStatusPill.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            DirectStatusPillText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            DirectStatusPillText.Text = $"就绪 ({specializedCount} 台)";
            DirectInfoBox.SetResourceReference(Border.BorderBrushProperty, "BorderToken");
            TxtDirectDisplayStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            TxtDirectDisplayStatus.Text = $"已检测到 {specializedCount} 台「已脱离桌面」的专用显示器，点击右侧「立即独占启动」即可直接接管硬件扫描输出。";
        }
        else
        {
            DirectStatusPill.SetResourceReference(Border.BackgroundProperty, "BorderToken");
            DirectStatusPillText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            DirectStatusPillText.Text = "等待脱离桌面";
            DirectInfoBox.SetResourceReference(Border.BorderBrushProperty, "BorderToken");
            TxtDirectDisplayStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            TxtDirectDisplayStatus.Text = "尚未检测到已脱离桌面的显示器。请先点击下方按钮打开 Windows 高级显示器设置，对副屏开启「从桌面删除显示器」，再点击刷新即可硬件独占。";
        }
    }

    private void ComboDirectTargets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        string key = GetSelectedTag(ComboDirectTargets, "");
        if (!string.IsNullOrWhiteSpace(key)) _controller.SetDirectDisplayTarget(key);
    }

    private void ComboDirectFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (int.TryParse(GetSelectedTag(ComboDirectFps, "10"), out int fps))
            _controller.SetDirectDisplayFps(fps);
    }

    private void BtnOpenWinDisplaySettings_Click(object sender, RoutedEventArgs e) =>
        AppController.OpenWindowsSpecializedDisplaySettings();

    private void BtnStartDirectDisplay_Click(object sender, RoutedEventArgs e)
    {
        string key = GetSelectedTag(ComboDirectTargets, "");
        if (!string.IsNullOrWhiteSpace(key))
            _controller.Settings.DirectDisplayTargetId = key;
        _controller.SetDisplayMode(useDirectExclusive: true, isFullScreen: true);
    }

    private void ChkPinAllDesktops_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetPinToAllVirtualDesktops(ChkPinAllDesktops.IsChecked == true);
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

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool want = ChkAutoStart.IsChecked == true;
        var res = _controller.SetAutoStart(want);
        if (!res.Success)
        {
            _suppressEvents = true;
            try { ChkAutoStart.IsChecked = _controller.IsAutoStartEnabled; }
            finally { _suppressEvents = false; }
            System.Windows.MessageBox.Show(
                $"无法更新开机自启设置：{res.ErrorMessage ?? "未知错误"}",
                "星澪硬件监控",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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

    private void RefreshCpuDisplayTargetCombo(AppSettings s)
    {
        var snap = _controller.LastTelemetry;
        var cpus = snap?.Cpus;
        int sockCount = cpus != null && cpus.Count > 0 ? cpus.Count : (snap?.Cpu.SocketCount ?? 1);

        if (snap != null)
        {
            TxtCpuTopologyInfo.Text = sockCount > 1
                ? $"检测到 {sockCount} 颗物理 CPU · {snap.Cpu.PhysicalCores} 核 {snap.Cpu.LogicalProcessors} 线程"
                : $"检测到 1 颗物理 CPU · {snap.Cpu.PhysicalCores} 核 {snap.Cpu.LogicalProcessors} 线程";
        }
        else
        {
            TxtCpuTopologyInfo.Text = "正在读取处理器拓扑";
        }

        ComboCpuDisplayTarget.Items.Clear();
        ComboCpuDisplayTarget.Items.Add(new ComboBoxItem { Tag = "auto", Content = "自动（多 CPU 分路展示，单 CPU 标准展示）" });
        ComboCpuDisplayTarget.Items.Add(new ComboBoxItem { Tag = "all", Content = "分路独立展示全部 CPU" });
        ComboCpuDisplayTarget.Items.Add(new ComboBoxItem { Tag = "combined", Content = "全部 CPU 合并为总览视图" });

        if (cpus != null && cpus.Count > 1)
        {
            foreach (var c in cpus)
            {
                ComboCpuDisplayTarget.Items.Add(new ComboBoxItem
                {
                    Tag = c.Index.ToString(),
                    Content = $"仅聚焦 {c.Label}（{c.Name} · {c.PhysicalCores}核/{c.LogicalProcessors}线）"
                });
            }
        }

        SelectComboByTag(ComboCpuDisplayTarget, SettingsService.NormalizeCpuDisplayTarget(s.CpuDisplayTarget));
    }

    private void ComboCpuDisplayTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        string target = GetSelectedTag(ComboCpuDisplayTarget, "auto");
        _controller.UpdateSettings(s => s.CpuDisplayTarget = target);
    }

    // ==================== CUSTOM LAYOUT EDITOR ====================

    private void RefreshLayoutEditorUi(AppSettings s)
    {
        var layout = SettingsService.NormalizeCustomLayout(s.CustomLayout);
        bool isCustomActive = string.Equals(s.LayoutMode, "custom", StringComparison.OrdinalIgnoreCase);

        if (isCustomActive)
        {
            LayoutActiveBadge.SetResourceReference(Border.BackgroundProperty, "SuccessSubtle");
            LayoutActiveBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            LayoutActiveBadgeText.Text = "已启用自定义排版";
            BtnActivateCustomLayout.Content = "已启用自定义排版";
            BtnActivateCustomLayout.IsEnabled = false;
        }
        else
        {
            LayoutActiveBadge.SetResourceReference(Border.BackgroundProperty, "BorderToken");
            LayoutActiveBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            LayoutActiveBadgeText.Text = "当前为内置布局";
            BtnActivateCustomLayout.Content = "启用自定义排版";
            BtnActivateCustomLayout.IsEnabled = true;
        }

        SelectComboByTag(ComboGridCols, layout.Columns.ToString());
        SelectComboByTag(ComboGridRows, layout.Rows.ToString());
        SelectComboByTag(ComboGridGap, layout.GapPx.ToString());

        var selected = layout.Widgets.FirstOrDefault(w => string.Equals(w.Id, _selectedWidgetId, StringComparison.OrdinalIgnoreCase))
                       ?? layout.Widgets.FirstOrDefault();
        if (selected != null)
        {
            _selectedWidgetId = selected.Id;
            TxtSelectedWidgetTitle.Text = $"选中：{CustomLayoutConfig.GetWidgetTitle(selected.Id)}";
            ChkSelectedWidgetVisible.IsChecked = selected.Visible;
            TxtWidgetCol.Text = selected.Col.ToString();
            TxtWidgetRow.Text = selected.Row.ToString();
            TxtWidgetColSpan.Text = selected.ColSpan.ToString();
            TxtWidgetRowSpan.Text = selected.RowSpan.ToString();
        }

        // Rebuild compact 3x3 module toggle chips
        LayoutModulesGrid.Children.Clear();
        foreach (var (id, _, shortTitle) in CustomLayoutConfig.AllWidgets)
        {
            var widget = layout.Widgets.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
            bool isVisible = widget?.Visible ?? true;
            bool isSelected = string.Equals(id, _selectedWidgetId, StringComparison.OrdinalIgnoreCase);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Padding = new Thickness(6, 4, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            chip.SetResourceReference(Border.BackgroundProperty, isSelected ? "AccentSubtle" : "BgPage");
            chip.SetResourceReference(Border.BorderBrushProperty, isSelected ? "Accent" : "BorderToken");

            var rowPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var chk = new System.Windows.Controls.CheckBox
            {
                IsChecked = isVisible,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            string capturedId = id;
            chk.Click += (_, ev) =>
            {
                ev.Handled = true;
                ToggleWidgetVisibility(capturedId, chk.IsChecked == true);
            };

            var lbl = new TextBlock
            {
                Text = shortTitle,
                FontSize = 14,
                FontWeight = isSelected ? FontWeights.Medium : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, isSelected ? "AccentText" : (isVisible ? "TextMain" : "TextMuted"));

            rowPanel.Children.Add(chk);
            rowPanel.Children.Add(lbl);
            chip.Child = rowPanel;
            chip.MouseLeftButtonDown += (_, _) =>
            {
                _selectedWidgetId = capturedId;
                _suppressEvents = true;
                try { RefreshLayoutEditorUi(_controller.Settings); }
                finally { _suppressEvents = false; }
            };

            LayoutModulesGrid.Children.Add(chip);
        }

        RenderLayoutCanvas();
    }

    private void RenderLayoutCanvas()
    {
        if (LayoutEditorCanvas == null) return;
        LayoutEditorCanvas.Children.Clear();

        double canvasW = LayoutEditorCanvas.ActualWidth > 40 ? LayoutEditorCanvas.ActualWidth : 460;
        double canvasH = LayoutEditorCanvas.ActualHeight > 40 ? LayoutEditorCanvas.ActualHeight : 296;
        var layout = SettingsService.NormalizeCustomLayout(_controller.Settings.CustomLayout);

        int cols = layout.Columns;
        int rows = layout.Rows;
        double cellW = canvasW / cols;
        double cellH = canvasH / rows;

        // 1. Background grid cells
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = new Border
                {
                    Width = Math.Max(2, cellW - 2),
                    Height = Math.Max(2, cellH - 2),
                    BorderThickness = new Thickness(0.5),
                    CornerRadius = new CornerRadius(2),
                    IsHitTestVisible = false,
                    Opacity = 0.6
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "BorderToken");
                Canvas.SetLeft(cell, c * cellW + 1);
                Canvas.SetTop(cell, r * cellH + 1);
                LayoutEditorCanvas.Children.Add(cell);
            }
        }

        // 2. Visible widget tiles
        var visibleWidgets = layout.Widgets.Where(w => w.Visible).ToList();
        foreach (var w in visibleWidgets)
        {
            bool isSelected = string.Equals(w.Id, _selectedWidgetId, StringComparison.OrdinalIgnoreCase);
            bool overlaps = HasWidgetOverlap(visibleWidgets, w);

            double x = (w.Col - 1) * cellW + 2;
            double y = (w.Row - 1) * cellH + 2;
            double tileW = Math.Max(24, w.ColSpan * cellW - 4);
            double tileH = Math.Max(22, w.RowSpan * cellH - 4);

            var tile = new Border
            {
                Width = tileW,
                Height = tileH,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                Cursor = System.Windows.Input.Cursors.SizeAll
            };

            if (overlaps)
            {
                tile.SetResourceReference(Border.BackgroundProperty, "DangerSubtle");
                tile.SetResourceReference(Border.BorderBrushProperty, "Danger");
            }
            else if (isSelected)
            {
                tile.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
                tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
            }
            else
            {
                tile.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                tile.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            }

            var grid = new Grid();
            var infoStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(6, 2, 6, 2),
                IsHitTestVisible = false
            };

            string shortTitle = CustomLayoutConfig.AllWidgets.FirstOrDefault(a => a.Id == w.Id).ShortTitle ?? w.Id;
            var titleTb = new TextBlock
            {
                Text = tileW >= 120 ? CustomLayoutConfig.GetWidgetTitle(w.Id) : shortTitle,
                FontSize = 14,
                FontWeight = isSelected ? FontWeights.Bold : FontWeights.Medium,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, overlaps ? "Danger" : (isSelected ? "AccentText" : "TextMain"));
            infoStack.Children.Add(titleTb);

            if (tileH >= 50)
            {
                var dimTb = new TextBlock
                {
                    Text = overlaps ? $"{w.ColSpan}×{w.RowSpan} · 重叠" : $"{w.ColSpan}×{w.RowSpan}",
                    FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Consolas"),
                    FontSize = 14,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                dimTb.SetResourceReference(TextBlock.ForegroundProperty, overlaps ? "Danger" : "TextMuted");
                infoStack.Children.Add(dimTb);
            }
            grid.Children.Add(infoStack);

            // Bottom-right resize grip handle
            var handle = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 3),
                Cursor = System.Windows.Input.Cursors.SizeNWSE,
                ToolTip = "拖动缩放此卡片占用的行列数"
            };
            handle.SetResourceReference(Border.BackgroundProperty, isSelected ? "Accent" : "BorderStrong");
            handle.Opacity = isSelected ? 1.0 : 0.45;

            string capturedId = w.Id;
            handle.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                StartWidgetDrag(capturedId, isResize: true, ev.GetPosition(LayoutEditorCanvas));
            };
            grid.Children.Add(handle);

            tile.Child = grid;
            tile.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                StartWidgetDrag(capturedId, isResize: false, ev.GetPosition(LayoutEditorCanvas));
            };

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);
            System.Windows.Controls.Panel.SetZIndex(tile, isSelected ? 10 : 1);
            LayoutEditorCanvas.Children.Add(tile);
        }
    }

    private void StartWidgetDrag(string widgetId, bool isResize, System.Windows.Point startPt)
    {
        var layout = _controller.Settings.CustomLayout;
        var w = layout.Widgets.FirstOrDefault(x => string.Equals(x.Id, widgetId, StringComparison.OrdinalIgnoreCase));
        if (w == null) return;

        _selectedWidgetId = w.Id;
        _dragWidgetId = w.Id;
        _isResizingWidget = isResize;
        _dragStartPt = startPt;
        _dragStartCol = w.Col;
        _dragStartRow = w.Row;
        _dragStartColSpan = w.ColSpan;
        _dragStartRowSpan = w.RowSpan;
        _dragMutated = false;

        LayoutEditorCanvas.CaptureMouse();
        _suppressEvents = true;
        try { RefreshLayoutEditorUi(_controller.Settings); }
        finally { _suppressEvents = false; }
    }

    private void LayoutEditorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PanelLayout.Visibility == Visibility.Visible)
        {
            RenderLayoutCanvas();
        }
    }

    private void LayoutEditorCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (string.IsNullOrEmpty(_dragWidgetId) || e.LeftButton != MouseButtonState.Pressed) return;

        var layout = _controller.Settings.CustomLayout;
        var w = layout.Widgets.FirstOrDefault(x => string.Equals(x.Id, _dragWidgetId, StringComparison.OrdinalIgnoreCase));
        if (w == null) return;

        double canvasW = LayoutEditorCanvas.ActualWidth > 40 ? LayoutEditorCanvas.ActualWidth : 460;
        double canvasH = LayoutEditorCanvas.ActualHeight > 40 ? LayoutEditorCanvas.ActualHeight : 296;
        double cellW = canvasW / layout.Columns;
        double cellH = canvasH / layout.Rows;

        var cur = e.GetPosition(LayoutEditorCanvas);
        int dCol = (int)Math.Round((cur.X - _dragStartPt.X) / cellW);
        int dRow = (int)Math.Round((cur.Y - _dragStartPt.Y) / cellH);

        int newCol = w.Col;
        int newRow = w.Row;
        int newColSpan = w.ColSpan;
        int newRowSpan = w.RowSpan;

        if (_isResizingWidget)
        {
            newColSpan = Math.Clamp(_dragStartColSpan + dCol, 1, layout.Columns - _dragStartCol + 1);
            newRowSpan = Math.Clamp(_dragStartRowSpan + dRow, 1, layout.Rows - _dragStartRow + 1);
        }
        else
        {
            newCol = Math.Clamp(_dragStartCol + dCol, 1, layout.Columns - _dragStartColSpan + 1);
            newRow = Math.Clamp(_dragStartRow + dRow, 1, layout.Rows - _dragStartRowSpan + 1);
        }

        if (newCol != w.Col || newRow != w.Row || newColSpan != w.ColSpan || newRowSpan != w.RowSpan)
        {
            w.Col = newCol;
            w.Row = newRow;
            w.ColSpan = newColSpan;
            w.RowSpan = newRowSpan;
            _dragMutated = true;

            TxtWidgetCol.Text = w.Col.ToString();
            TxtWidgetRow.Text = w.Row.ToString();
            TxtWidgetColSpan.Text = w.ColSpan.ToString();
            TxtWidgetRowSpan.Text = w.RowSpan.ToString();
            RenderLayoutCanvas();

            // Push live visual preview to DisplayWindow when custom mode is active
            if (string.Equals(_controller.Settings.LayoutMode, "custom", StringComparison.OrdinalIgnoreCase))
            {
                _controller.UpdateCustomLayout(layout.Clone(), activateCustomMode: true);
            }
        }
    }

    private void LayoutEditorCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_dragWidgetId)) return;
        _dragWidgetId = null;
        LayoutEditorCanvas.ReleaseMouseCapture();

        if (_dragMutated)
        {
            _dragMutated = false;
            _controller.UpdateCustomLayout(_controller.Settings.CustomLayout.Clone(), activateCustomMode: true);
        }
    }

    private static bool HasWidgetOverlap(List<DashboardWidgetLayout> visible, DashboardWidgetLayout target)
    {
        int ax1 = target.Col, ay1 = target.Row;
        int ax2 = target.Col + target.ColSpan - 1, ay2 = target.Row + target.RowSpan - 1;
        foreach (var other in visible)
        {
            if (ReferenceEquals(other, target)) continue;
            int bx1 = other.Col, by1 = other.Row;
            int bx2 = other.Col + other.ColSpan - 1, by2 = other.Row + other.RowSpan - 1;
            if (ax1 <= bx2 && ax2 >= bx1 && ay1 <= by2 && ay2 >= by1)
                return true;
        }
        return false;
    }

    private void ToggleWidgetVisibility(string widgetId, bool visible)
    {
        var layout = _controller.Settings.CustomLayout.Clone();
        var w = layout.Widgets.FirstOrDefault(x => string.Equals(x.Id, widgetId, StringComparison.OrdinalIgnoreCase));
        if (w == null) return;

        w.Visible = visible;
        _selectedWidgetId = w.Id;

        if (visible)
        {
            var others = layout.Widgets.Where(x => x.Visible && !ReferenceEquals(x, w)).ToList();
            if (HasWidgetOverlap(others.Append(w).ToList(), w))
            {
                TryFindFreeSlot(layout, others, w);
            }
        }

        _controller.UpdateCustomLayout(layout, activateCustomMode: true);
    }

    private static void TryFindFreeSlot(CustomLayoutConfig layout, List<DashboardWidgetLayout> occupied, DashboardWidgetLayout target)
    {
        for (int r = 1; r <= layout.Rows - target.RowSpan + 1; r++)
        {
            for (int c = 1; c <= layout.Columns - target.ColSpan + 1; c++)
            {
                target.Col = c;
                target.Row = r;
                if (!HasWidgetOverlap(occupied.Append(target).ToList(), target))
                    return;
            }
        }
    }

    private void BtnLayoutPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string preset) return;
        var next = CustomLayoutConfig.CreatePreset(preset);
        _controller.UpdateCustomLayout(next, activateCustomMode: true);
    }

    private void BtnLayoutAutoPack_Click(object sender, RoutedEventArgs e)
    {
        var layout = _controller.Settings.CustomLayout.Clone();
        var visible = layout.Widgets
            .Where(w => w.Visible)
            .OrderBy(w => w.Row)
            .ThenBy(w => w.Col)
            .ToList();

        var placed = new List<DashboardWidgetLayout>();
        foreach (var w in visible)
        {
            w.ColSpan = Math.Clamp(w.ColSpan, 1, layout.Columns);
            w.RowSpan = Math.Clamp(w.RowSpan, 1, layout.Rows);
            bool found = false;
            for (int r = 1; r <= layout.Rows - w.RowSpan + 1 && !found; r++)
            {
                for (int c = 1; c <= layout.Columns - w.ColSpan + 1 && !found; c++)
                {
                    w.Col = c;
                    w.Row = r;
                    if (!HasWidgetOverlap(placed.Append(w).ToList(), w))
                    {
                        found = true;
                    }
                }
            }
            if (!found && layout.Rows < 12)
            {
                int nextRow = placed.Count > 0 ? placed.Max(p => p.Row + p.RowSpan) : 1;
                layout.Rows = Math.Clamp(nextRow + w.RowSpan - 1, layout.Rows, 12);
                w.Col = 1;
                w.Row = Math.Clamp(nextRow, 1, layout.Rows - w.RowSpan + 1);
            }
            placed.Add(w);
        }

        _controller.UpdateCustomLayout(layout, activateCustomMode: true);
    }

    private void BtnLayoutReset_Click(object sender, RoutedEventArgs e)
    {
        _controller.UpdateCustomLayout(CustomLayoutConfig.CreatePreset("split"), activateCustomMode: true);
    }

    private void BtnActivateCustomLayout_Click(object sender, RoutedEventArgs e)
    {
        _controller.UpdateSettings(s => s.LayoutMode = "custom");
    }

    private void ComboGridConfig_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var layout = _controller.Settings.CustomLayout.Clone();
        if (int.TryParse(GetSelectedTag(ComboGridCols, "12"), out int cols)) layout.Columns = cols;
        if (int.TryParse(GetSelectedTag(ComboGridRows, "8"), out int rows)) layout.Rows = rows;
        if (int.TryParse(GetSelectedTag(ComboGridGap, "12"), out int gap)) layout.GapPx = gap;
        _controller.UpdateCustomLayout(layout, activateCustomMode: true);
    }

    private void ChkSelectedWidgetVisible_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ToggleWidgetVisibility(_selectedWidgetId, ChkSelectedWidgetVisible.IsChecked == true);
    }

    private void BtnWidgetStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string raw) return;
        var parts = raw.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int delta)) return;

        var layout = _controller.Settings.CustomLayout.Clone();
        var w = layout.Widgets.FirstOrDefault(x => string.Equals(x.Id, _selectedWidgetId, StringComparison.OrdinalIgnoreCase));
        if (w == null) return;

        switch (parts[0])
        {
            case "col":
                w.Col = Math.Clamp(w.Col + delta, 1, layout.Columns - w.ColSpan + 1);
                break;
            case "row":
                w.Row = Math.Clamp(w.Row + delta, 1, layout.Rows - w.RowSpan + 1);
                break;
            case "colSpan":
                w.ColSpan = Math.Clamp(w.ColSpan + delta, 1, layout.Columns - w.Col + 1);
                break;
            case "rowSpan":
                w.RowSpan = Math.Clamp(w.RowSpan + delta, 1, layout.Rows - w.Row + 1);
                break;
        }

        _controller.UpdateCustomLayout(layout, activateCustomMode: true);
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
            s.AutoCheckUpdates = ChkAutoCheckUpdates.IsChecked == true;
        });
    }

    private void RefreshUpdateSectionUi(AppSettings s)
    {
        var upd = _controller.GetUpdateState();
        string pkgLabel = upd.IsInstalledMode ? "安装版" : "便携版";
        TxtCurrentVersionBadge.Text = $"v{upd.CurrentVersion} · {upd.CurrentArch} {pkgLabel}";
        TxtUpdateStatusSummary.Text = upd.StatusMessage;

        if (upd.Status == UpdateStatus.Error && !string.IsNullOrWhiteSpace(upd.ErrorMessage))
        {
            TxtUpdateStatusSummary.Text = $"{upd.StatusMessage}（{upd.ErrorMessage}）";
            TxtUpdateStatusSummary.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        else if (upd.HasUpdate)
        {
            TxtUpdateStatusSummary.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        }
        else
        {
            TxtUpdateStatusSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
        }

        BtnCheckUpdate.IsEnabled = upd.Status is not UpdateStatus.Checking and not UpdateStatus.Downloading and not UpdateStatus.Verifying;
        BtnCheckUpdate.Content = upd.Status == UpdateStatus.Checking ? "检查中..." : "检查更新";

        bool showOpenBtn = upd.HasUpdate || upd.Status is UpdateStatus.Downloading or UpdateStatus.Verifying or UpdateStatus.ReadyToInstall;
        BtnOpenUpdateDialog.Visibility = showOpenBtn ? Visibility.Visible : Visibility.Collapsed;
        BtnOpenUpdateDialog.Content = upd.Status switch
        {
            UpdateStatus.Downloading => $"下载中 {upd.ProgressPercent:F0}%",
            UpdateStatus.Verifying => "校验中...",
            UpdateStatus.ReadyToInstall => "立即安装更新",
            _ => "查看并下载"
        };

        if (!string.IsNullOrWhiteSpace(s.IgnoredUpdateVersion))
        {
            PanelIgnoredVersion.Visibility = Visibility.Visible;
            TxtIgnoredVersionInfo.Text = $"已忽略版本：{s.IgnoredUpdateVersion}";
        }
        else
        {
            PanelIgnoredVersion.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _ = _controller.CheckForUpdatesAsync(manual: true, showDialogIfAvailable: true, showUpToDateBalloon: false);
    }

    private void BtnOpenUpdateDialog_Click(object sender, RoutedEventArgs e)
    {
        _controller.ShowUpdateWindow();
    }

    private void BtnClearIgnoredUpdate_Click(object sender, RoutedEventArgs e)
    {
        _controller.ClearIgnoredUpdateVersion();
    }

    private void BtnOpenReleasesPage_Click(object sender, RoutedEventArgs e)
    {
        UpdateService.OpenUrlInBrowser(UpdateService.ReleasesPageUrl);
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

    // ==================== DONATION (WECHAT PAY NATIVE v3) ====================

    private void BtnDonateAmountPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag) return;
        HighlightDonatePreset(btn);

        if (tag == "custom")
        {
            TxtDonateAmount.Focus();
            TxtDonateAmount.SelectAll();
            return;
        }

        TxtDonateAmount.Text = tag;
        if (decimal.TryParse(tag, out decimal yuan))
        {
            TxtDonateAmountDisplay.Text = $"¥ {yuan:F2}";
        }
    }

    private void TxtDonateAmount_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtDonateAmountDisplay == null) return;
        if (decimal.TryParse(TxtDonateAmount.Text.Trim(), out decimal yuan) && yuan > 0)
        {
            TxtDonateAmountDisplay.Text = $"¥ {yuan:F2}";
            string matchedTag = yuan == decimal.Truncate(yuan) ? ((int)yuan).ToString() : "custom";
            var btn = _donateAmountButtons.FirstOrDefault(b => string.Equals(b.Tag?.ToString(), matchedTag, StringComparison.OrdinalIgnoreCase))
                      ?? BtnDonateCustom;
            HighlightDonatePreset(btn);
        }
    }

    private void HighlightDonatePreset(System.Windows.Controls.Button selected)
    {
        var normalStyle = (Style)FindResource("PresetButton");
        var activeStyle = (Style)FindResource("ActivePresetButton");
        foreach (var b in _donateAmountButtons)
        {
            b.Style = ReferenceEquals(b, selected) ? activeStyle : normalStyle;
        }
    }

    private async void BtnTestDonateApi_Click(object sender, RoutedEventArgs e)
    {
        string url = DonationService.NormalizeBaseUrl(TxtDonateApiUrl.Text);
        TxtDonateApiUrl.Text = url;
        _controller.UpdateSettings(s => s.DonationApiBaseUrl = url, pushVisualToDisplay: false);

        BtnTestDonateApi.IsEnabled = false;
        BtnTestDonateApi.Content = "测试中...";
        TxtDonateEndpointStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        TxtDonateEndpointStatus.Text = $"正在连接 {url}/api/donate/health ...";

        try
        {
            var res = await _controller.DonationService.CheckHealthAsync(url);
            if (res.Ok && res.Configured)
            {
                TxtDonateEndpointStatus.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                TxtDonateEndpointStatus.Text = $"连接正常：收款商户「{res.MerchantName}」已就绪，可直接生成微信支付二维码。";
            }
            else if (res.Ok && !res.Configured)
            {
                TxtDonateEndpointStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                TxtDonateEndpointStatus.Text = $"服务已连通，但服务端尚未填完微信商户环境变量：{string.Join("、", res.Missing)}";
            }
            else
            {
                TxtDonateEndpointStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                TxtDonateEndpointStatus.Text = res.ErrorMessage;
            }
        }
        finally
        {
            BtnTestDonateApi.IsEnabled = true;
            BtnTestDonateApi.Content = "保存并测试";
        }
    }

    private async void BtnCreateDonateOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(TxtDonateAmount.Text.Trim(), out decimal yuan) || yuan < 1.00m || yuan > 10000.00m)
        {
            SetDonateBadge("金额无效", isError: true);
            TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            TxtDonateOrderStatus.Text = "请输入 1.00 ~ 10000.00 元之间的有效金额。";
            TxtDonateAmount.Focus();
            return;
        }

        string apiUrl = DonationService.NormalizeBaseUrl(TxtDonateApiUrl.Text);
        TxtDonateApiUrl.Text = apiUrl;
        _controller.UpdateSettings(s => s.DonationApiBaseUrl = apiUrl, pushVisualToDisplay: false);

        // Close previous unpaid order if any
        if (!string.IsNullOrEmpty(_activeDonateOrderId))
        {
            string prevId = _activeDonateOrderId;
            StopDonatePolling();
            _ = _controller.DonationService.CloseWeChatOrderAsync(apiUrl, prevId);
        }

        int amountFen = (int)Math.Round(yuan * 100m, MidpointRounding.AwayFromZero);
        string note = TxtDonateNote.Text.Trim();
        string ver = _controller.GetUpdateState().CurrentVersion;

        BtnCreateDonateOrder.IsEnabled = false;
        BtnCreateDonateOrder.Content = "正在下单...";
        SetDonateBadge("下单中", isAccent: true);
        TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        TxtDonateOrderStatus.Text = "正在向微信支付收单服务申请扫码链接...";
        TxtDonateOrderMeta.Text = "";
        DonateSuccessOverlay.Visibility = Visibility.Collapsed;

        try
        {
            var res = await _controller.DonationService.CreateWeChatNativeOrderAsync(apiUrl, amountFen, note, ver);
            if (!res.Ok || string.IsNullOrWhiteSpace(res.CodeUrl))
            {
                SetDonateBadge("下单失败", isError: true);
                ImgDonateQr.Visibility = Visibility.Collapsed;
                DonateQrPlaceholder.Visibility = Visibility.Visible;
                TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                TxtDonateOrderStatus.Text = res.ErrorMessage;
                return;
            }

            _activeDonateOrderId = res.OrderId;
            _activeDonateExpireAt = res.ExpireAt ?? DateTimeOffset.UtcNow.AddMinutes(15);

            ImgDonateQr.Source = DonationService.GenerateQrBitmapImage(res.CodeUrl);
            ImgDonateQr.Visibility = Visibility.Visible;
            DonateQrPlaceholder.Visibility = Visibility.Collapsed;
            DonateSuccessOverlay.Visibility = Visibility.Collapsed;

            TxtDonateAmountDisplay.Text = $"¥ {res.AmountFen / 100.0:F2}";
            SetDonateBadge("待微信扫码", isAccent: true);
            TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            TxtDonateOrderStatus.Text = $"请用微信扫描上方二维码支付给「{res.MerchantName}」";
            BtnCancelDonateOrder.Visibility = Visibility.Visible;
            BtnQueryDonateNow.Visibility = Visibility.Visible;

            StartDonatePolling();
            UpdateDonateCountdownText();
        }
        finally
        {
            BtnCreateDonateOrder.IsEnabled = true;
            BtnCreateDonateOrder.Content = "生成微信支付二维码";
        }
    }

    private void BtnCancelDonateOrder_Click(object sender, RoutedEventArgs e)
    {
        string orderId = _activeDonateOrderId;
        string apiUrl = DonationService.NormalizeBaseUrl(TxtDonateApiUrl.Text);
        StopDonatePolling();

        if (!string.IsNullOrEmpty(orderId))
        {
            _ = _controller.DonationService.CloseWeChatOrderAsync(apiUrl, orderId);
        }

        ImgDonateQr.Visibility = Visibility.Collapsed;
        ImgDonateQr.Source = null;
        DonateSuccessOverlay.Visibility = Visibility.Collapsed;
        DonateQrPlaceholder.Visibility = Visibility.Visible;
        BtnCancelDonateOrder.Visibility = Visibility.Collapsed;
        BtnQueryDonateNow.Visibility = Visibility.Collapsed;
        SetDonateBadge("已取消");
        TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        TxtDonateOrderStatus.Text = "订单已取消，可重新选择金额下单。";
        TxtDonateOrderMeta.Text = "";
    }

    private async void BtnQueryDonateNow_Click(object sender, RoutedEventArgs e)
    {
        await PollActiveDonateOrderOnceAsync(manual: true);
    }

    private void StartDonatePolling()
    {
        StopDonatePolling(keepOrderId: true);

        _donatePollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _donatePollTimer.Tick += async (_, _) => await PollActiveDonateOrderOnceAsync(manual: false);
        _donatePollTimer.Start();

        _donateCountdownTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _donateCountdownTimer.Tick += (_, _) => UpdateDonateCountdownText();
        _donateCountdownTimer.Start();
    }

    private void StopDonatePolling(bool keepOrderId = false)
    {
        try { _donatePollTimer?.Stop(); _donatePollTimer = null; } catch { }
        try { _donateCountdownTimer?.Stop(); _donateCountdownTimer = null; } catch { }
        if (!keepOrderId)
        {
            _activeDonateOrderId = "";
            _activeDonateExpireAt = null;
        }
    }

    private void UpdateDonateCountdownText()
    {
        if (string.IsNullOrEmpty(_activeDonateOrderId) || !_activeDonateExpireAt.HasValue) return;
        var remain = _activeDonateExpireAt.Value - DateTimeOffset.UtcNow;
        if (remain <= TimeSpan.Zero)
        {
            StopDonatePolling();
            ImgDonateQr.Visibility = Visibility.Collapsed;
            DonateQrPlaceholder.Visibility = Visibility.Visible;
            BtnCancelDonateOrder.Visibility = Visibility.Collapsed;
            BtnQueryDonateNow.Visibility = Visibility.Collapsed;
            SetDonateBadge("已过期", isError: true);
            TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            TxtDonateOrderStatus.Text = "二维码已过期，请点击左侧按钮重新生成。";
            TxtDonateOrderMeta.Text = "";
            return;
        }

        TxtDonateOrderMeta.Text = $"单号 {_activeDonateOrderId} · 有效期 {(int)remain.TotalMinutes:D2}:{remain.Seconds:D2}";
    }

    private async Task PollActiveDonateOrderOnceAsync(bool manual)
    {
        if (_donatePollInFlight || string.IsNullOrEmpty(_activeDonateOrderId)) return;
        _donatePollInFlight = true;
        string orderId = _activeDonateOrderId;
        string apiUrl = DonationService.NormalizeBaseUrl(TxtDonateApiUrl.Text);

        try
        {
            var res = await _controller.DonationService.QueryWeChatOrderStatusAsync(apiUrl, orderId);
            if (!string.Equals(orderId, _activeDonateOrderId, StringComparison.Ordinal)) return;

            if (!res.Ok)
            {
                if (manual)
                {
                    TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                    TxtDonateOrderStatus.Text = res.ErrorMessage;
                }
                return;
            }

            switch ((res.TradeState ?? "").ToUpperInvariant())
            {
                case "SUCCESS":
                    StopDonatePolling();
                    ImgDonateQr.Visibility = Visibility.Collapsed;
                    DonateQrPlaceholder.Visibility = Visibility.Collapsed;
                    DonateSuccessOverlay.Visibility = Visibility.Visible;
                    BtnCancelDonateOrder.Visibility = Visibility.Collapsed;
                    BtnQueryDonateNow.Visibility = Visibility.Collapsed;

                    double paidYuan = res.AmountFen > 0 ? res.AmountFen / 100.0 : (double.TryParse(TxtDonateAmount.Text, out double y) ? y : 0);
                    TxtDonateSuccessAmount.Text = $"¥ {paidYuan:F2}";
                    TxtDonateAmountDisplay.Text = $"¥ {paidYuan:F2}";
                    SetDonateBadge("支付成功", isSuccess: true);
                    TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                    TxtDonateOrderStatus.Text = "微信支付已确认收款，衷心感谢你对星澪硬件监控的支持！";
                    TxtDonateOrderMeta.Text = string.IsNullOrWhiteSpace(res.TransactionId)
                        ? $"商户单号：{orderId}"
                        : $"微信流水号：{res.TransactionId}";
                    break;

                case "USERPAYING":
                    SetDonateBadge("支付确认中", isAccent: true);
                    TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                    TxtDonateOrderStatus.Text = "检测到微信端正在输入密码确认，请稍候...";
                    break;

                case "CLOSED":
                case "REVOKED":
                case "PAYERROR":
                    StopDonatePolling();
                    ImgDonateQr.Visibility = Visibility.Collapsed;
                    DonateQrPlaceholder.Visibility = Visibility.Visible;
                    BtnCancelDonateOrder.Visibility = Visibility.Collapsed;
                    BtnQueryDonateNow.Visibility = Visibility.Collapsed;
                    SetDonateBadge("已关闭", isError: true);
                    TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                    TxtDonateOrderStatus.Text = string.IsNullOrWhiteSpace(res.TradeStateDesc) ? "订单已关闭或支付失败，请重新下单。" : res.TradeStateDesc;
                    break;

                default: // NOTPAY
                    if (manual)
                    {
                        TxtDonateOrderStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                        TxtDonateOrderStatus.Text = "当前尚未检测到付款，请在微信完成支付后稍等 2 秒自动刷新。";
                    }
                    break;
            }
        }
        finally
        {
            _donatePollInFlight = false;
        }
    }

    private void SetDonateBadge(string text, bool isSuccess = false, bool isAccent = false, bool isError = false)
    {
        DonateStateBadgeText.Text = text;
        if (isSuccess)
        {
            DonateStateBadge.SetResourceReference(Border.BackgroundProperty, "SuccessSubtle");
            DonateStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        }
        else if (isError)
        {
            DonateStateBadge.SetResourceReference(Border.BackgroundProperty, "DangerSubtle");
            DonateStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        else if (isAccent)
        {
            DonateStateBadge.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
            DonateStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        }
        else
        {
            DonateStateBadge.SetResourceReference(Border.BackgroundProperty, "BorderToken");
            DonateStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
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
