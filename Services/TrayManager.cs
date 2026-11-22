using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SideScreenHUD.Models;
using SideScreenHUD.Native;

namespace SideScreenHUD.Services;

/// <summary>
/// Manages the Windows system tray icon, live tooltip, MioLab-styled right-click quick settings menu,
/// and tray notifications.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly AppController _controller;
    private readonly Font _menuFont;
    private readonly Font _menuBoldFont;
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _menu;
    private Icon? _icon;
    private bool _shownMinimizeHint;
    private bool _disposed;

    public TrayManager(AppController controller)
    {
        _controller = controller;
        _menuFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        _menuBoldFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        _icon = CreateMioLabIcon();

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = true,
            ShowCheckMargin = true,
            Font = _menuFont
        };
        _menu.Opening += (_, _) => RebuildMenuItems();

        _trayIcon = new NotifyIcon
        {
            Text = "星澪硬件监控（左键打开设置，右键快捷调整）",
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = true
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _controller.ShowControlWindow();
            }
        };
        _trayIcon.DoubleClick += (_, _) => _controller.ShowControlWindow();

        ApplyMenuTheme();
    }

    public void Refresh()
    {
        if (_disposed || _trayIcon == null) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _menu == null) return;
            ApplyMenuTheme();
            if (_menu.Visible)
            {
                RebuildMenuItems();
            }
        });
    }

    public void UpdateLiveTooltip(TelemetrySnapshot snap)
    {
        if (_disposed || _trayIcon == null) return;
        try
        {
            string cpuText = snap.Cpus != null && snap.Cpus.Count > 1
                ? string.Join(" · ", snap.Cpus.Take(2).Select(c => $"CPU{c.Index + 1} {Math.Round(c.TotalUsagePercent)}%"))
                : $"CPU {Math.Round(snap.Cpu.TotalUsagePercent)}%";
            string text = $"星澪监控 · {cpuText} · GPU {Math.Round(snap.Gpu.UsagePercent)}% · 内存 {Math.Round(snap.Memory.UsagePercent)}%";
            if (text.Length > 63) text = text[..63];
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && _trayIcon != null)
                {
                    _trayIcon.Text = text;
                }
            });
        }
        catch { }
    }

    public void ShowBalloon(string title, string body)
    {
        if (_disposed || _trayIcon == null) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _trayIcon == null) return;
            try
            {
                _trayIcon.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
            }
            catch { }
        });
    }

    public void NotifyHiddenToTrayOnce()
    {
        if (_disposed || _trayIcon == null || _shownMinimizeHint) return;
        _shownMinimizeHint = true;
        try
        {
            _trayIcon.ShowBalloonTip(
                2500,
                "星澪硬件监控已在托盘运行",
                "左键托盘图标打开设置窗口，右键可快捷修改常用设置。",
                ToolTipIcon.Info);
        }
        catch { }
    }

    private void ApplyMenuTheme()
    {
        if (_menu == null) return;
        bool isDark = string.Equals(_controller.Settings.Theme, "dark", StringComparison.OrdinalIgnoreCase);
        _menu.Renderer = new MioLabToolStripRenderer(isDark);
        _menu.BackColor = isDark ? Color.FromArgb(0x17, 0x1A, 0x21) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        _menu.ForeColor = isDark ? Color.FromArgb(0xE6, 0xE8, 0xEC) : Color.FromArgb(0x1F, 0x29, 0x37);
    }

    private void RebuildMenuItems()
    {
        if (_menu == null || _disposed) return;
        ApplyMenuTheme();

        // Dispose previous items cleanly before rebuilding
        while (_menu.Items.Count > 0)
        {
            var item = _menu.Items[0];
            _menu.Items.RemoveAt(0);
            item.Dispose();
        }

        var s = _controller.Settings;
        bool isDark = string.Equals(s.Theme, "dark", StringComparison.OrdinalIgnoreCase);

        // 1. Primary Window Actions
        var openSettings = new ToolStripMenuItem("打开设置窗口")
        {
            Font = _menuBoldFont
        };
        openSettings.Click += (_, _) => _controller.ShowControlWindow();
        _menu.Items.Add(openSettings);

        if (_controller.IsDisplayOpen)
        {
            _menu.Items.Add("关闭监控窗口", null, (_, _) => _controller.CloseDisplayWindow());
            _menu.Items.Add("重建监控窗口（刷新渲染）", null, (_, _) => _ = _controller.RecreateDisplayWindowAsync());
        }
        else
        {
            _menu.Items.Add("打开监控窗口", null, (_, _) => _ = _controller.RecreateDisplayWindowAsync());
        }

        _menu.Items.Add(new ToolStripSeparator());

        // 2. Display Mode & Fixed Position
        var modeMenu = new ToolStripMenuItem("显示模式与固定位置");

        var directItem = new ToolStripMenuItem("硬件独占显示器（脱离桌面 · Direct Display）") { Checked = s.UseDirectDisplayExclusive };
        directItem.Click += (_, _) => _controller.SetDisplayMode(useDirectExclusive: true, isFullScreen: true);
        modeMenu.DropDownItems.Add(directItem);

        var fsItem = new ToolStripMenuItem("占用整块屏幕 (F11)") { Checked = !s.UseDirectDisplayExclusive && s.IsFullScreen };
        fsItem.Click += (_, _) => _controller.SetDisplayMode(useDirectExclusive: false, isFullScreen: true);
        modeMenu.DropDownItems.Add(fsItem);

        var fixedItem = new ToolStripMenuItem("窗口固定显示位置") { Checked = !s.UseDirectDisplayExclusive && !s.IsFullScreen };
        fixedItem.Click += (_, _) => _controller.SetDisplayMode(useDirectExclusive: false, isFullScreen: false);
        modeMenu.DropDownItems.Add(fixedItem);

        modeMenu.DropDownItems.Add("打开 Windows「从桌面删除显示器」设置", null, (_, _) => AppController.OpenWindowsSpecializedDisplaySettings());

        modeMenu.DropDownItems.Add(new ToolStripSeparator());

        var presets = new (string Key, string Label)[]
        {
            ("top-left", "停靠：左上角"),
            ("top-half", "停靠：上半屏"),
            ("top-right", "停靠：右上角"),
            ("left-half", "停靠：左半屏"),
            ("center", "停靠：屏幕居中"),
            ("right-half", "停靠：右半屏"),
            ("bottom-left", "停靠：左下角"),
            ("bottom-half", "停靠：下半屏"),
            ("bottom-right", "停靠：右下角")
        };
        foreach (var (key, label) in presets)
        {
            var pItem = new ToolStripMenuItem(label)
            {
                Checked = !s.IsFullScreen && string.Equals(s.WindowPositionPreset, key, StringComparison.OrdinalIgnoreCase)
            };
            var k = key;
            pItem.Click += (_, _) => _controller.SetWindowPositionPreset(k);
            modeMenu.DropDownItems.Add(pItem);
        }

        modeMenu.DropDownItems.Add(new ToolStripSeparator());
        var quickSizes = new (int W, int H)[] { (640, 420), (960, 640), (1280, 760) };
        foreach (var (w, h) in quickSizes)
        {
            var szItem = new ToolStripMenuItem($"窗口尺寸：{w} × {h}")
            {
                Checked = !s.IsFullScreen && (int)s.WindowWidth == w && (int)s.WindowHeight == h
            };
            int cw = w, ch = h;
            szItem.Click += (_, _) =>
            {
                s.WindowWidth = cw;
                s.WindowHeight = ch;
                string curPreset = SettingsService.NormalizePositionPreset(s.WindowPositionPreset);
                bool isHalf = curPreset is "left-half" or "right-half" or "top-half" or "bottom-half";
                if (isHalf || curPreset == "custom")
                    _controller.SetCustomWindowGeometry(s.WindowOffsetX, s.WindowOffsetY, cw, ch, s.WindowEdgeMargin);
                else
                    _controller.SetWindowPositionPreset(curPreset);
            };
            modeMenu.DropDownItems.Add(szItem);
        }
        _menu.Items.Add(modeMenu);

        // 3. Target Monitor
        var monitorMenu = new ToolStripMenuItem("选择目标屏幕");
        foreach (var mon in _controller.DisplayManager.GetMonitors())
        {
            var mItem = new ToolStripMenuItem(mon.FriendlyName)
            {
                Checked = string.Equals(mon.DeviceName, s.TargetMonitorDevice, StringComparison.OrdinalIgnoreCase)
            };
            var dev = mon.DeviceName;
            mItem.Click += (_, _) => _controller.SwitchMonitor(dev);
            monitorMenu.DropDownItems.Add(mItem);
        }
        _menu.Items.Add(monitorMenu);

        // 4. Instant Window Toggles
        var topItem = new ToolStripMenuItem("监控窗口置顶") { Checked = s.AlwaysOnTop };
        topItem.Click += (_, _) => _controller.SetAlwaysOnTop(!s.AlwaysOnTop);
        _menu.Items.Add(topItem);

        var pinItem = new ToolStripMenuItem("跨虚拟桌面常驻（切桌面不消失）") { Checked = s.PinToAllVirtualDesktops };
        pinItem.Click += (_, _) => _controller.SetPinToAllVirtualDesktops(!s.PinToAllVirtualDesktops);
        _menu.Items.Add(pinItem);

        var lockItem = new ToolStripMenuItem("锁定窗口位置与大小") { Checked = s.LockWindowPosition };
        lockItem.Click += (_, _) => _controller.SetLockWindowPosition(!s.LockWindowPosition);
        _menu.Items.Add(lockItem);

        var hideBarItem = new ToolStripMenuItem("隐藏监控窗口顶栏") { Checked = s.HideMonitorTopBar };
        hideBarItem.Click += (_, _) => _controller.UpdateSettings(x => x.HideMonitorTopBar = !x.HideMonitorTopBar);
        _menu.Items.Add(hideBarItem);

        var taskbarItem = new ToolStripMenuItem("在任务栏显示监控窗口") { Checked = s.ShowDisplayInTaskbar };
        taskbarItem.Click += (_, _) => _controller.SetShowDisplayInTaskbar(!s.ShowDisplayInTaskbar);
        _menu.Items.Add(taskbarItem);

        _menu.Items.Add(new ToolStripSeparator());

        // 5. Layout, Theme & Hardware Quick Settings
        var layoutMenu = new ToolStripMenuItem("布局视图");
        AddRadioOption(layoutMenu, "综合看板（性能 + K 线）", s.LayoutMode == "split", () => _controller.UpdateSettings(x => x.LayoutMode = "split"));
        AddRadioOption(layoutMenu, "性能专注模式", s.LayoutMode == "hardware", () => _controller.UpdateSettings(x => x.LayoutMode = "hardware"));
        AddRadioOption(layoutMenu, "行情专注模式", s.LayoutMode == "market", () => _controller.UpdateSettings(x => x.LayoutMode = "market"));
        AddRadioOption(layoutMenu, "自定义排版", s.LayoutMode == "custom", () => _controller.UpdateSettings(x => x.LayoutMode = "custom"));
        _menu.Items.Add(layoutMenu);

        var themeMenu = new ToolStripMenuItem("界面主题");
        AddRadioOption(themeMenu, "浅色（星澪标准）", !isDark, () => _controller.UpdateSettings(x => x.Theme = "light"));
        AddRadioOption(themeMenu, "深色（星澪深色）", isDark, () => _controller.UpdateSettings(x => x.Theme = "dark"));
        _menu.Items.Add(themeMenu);

        var hwMenu = new ToolStripMenuItem("硬件监控设置");
        AddRadioOption(hwMenu, "CPU：所有逻辑处理器（任务管理器）", s.CpuViewMode != "overall", () => _controller.UpdateSettings(x => x.CpuViewMode = "cores"));
        AddRadioOption(hwMenu, "CPU：总体利用率曲线", s.CpuViewMode == "overall", () => _controller.UpdateSettings(x => x.CpuViewMode = "overall"));
        hwMenu.DropDownItems.Add(new ToolStripSeparator());

        var cpuTargetMenu = new ToolStripMenuItem("多路 CPU 显示方式");
        string curTarget = SettingsService.NormalizeCpuDisplayTarget(s.CpuDisplayTarget);
        AddRadioOption(cpuTargetMenu, "自动（多 CPU 分路展示）", curTarget == "auto", () => _controller.UpdateSettings(x => x.CpuDisplayTarget = "auto"));
        AddRadioOption(cpuTargetMenu, "分路展示全部 CPU", curTarget == "all", () => _controller.UpdateSettings(x => x.CpuDisplayTarget = "all"));
        AddRadioOption(cpuTargetMenu, "合并为总览视图", curTarget == "combined", () => _controller.UpdateSettings(x => x.CpuDisplayTarget = "combined"));
        var detectedCpus = _controller.LastTelemetry?.Cpus;
        if (detectedCpus != null && detectedCpus.Count > 1)
        {
            cpuTargetMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var cpuSock in detectedCpus)
            {
                string idxTag = cpuSock.Index.ToString();
                AddRadioOption(cpuTargetMenu, $"仅显示 {cpuSock.Label}（{cpuSock.PhysicalCores}核/{cpuSock.LogicalProcessors}线程）", curTarget == idxTag,
                    () => _controller.UpdateSettings(x => x.CpuDisplayTarget = idxTag));
            }
        }
        hwMenu.DropDownItems.Add(cpuTargetMenu);

        hwMenu.DropDownItems.Add(new ToolStripSeparator());
        AddRadioOption(hwMenu, "进程：按 CPU 占用排序", s.ProcessSortBy != "mem", () => _controller.UpdateSettings(x => x.ProcessSortBy = "cpu"));
        AddRadioOption(hwMenu, "进程：按内存占用排序", s.ProcessSortBy == "mem", () => _controller.UpdateSettings(x => x.ProcessSortBy = "mem"));
        hwMenu.DropDownItems.Add(new ToolStripSeparator());
        AddRadioOption(hwMenu, "采样周期：0.5 秒", s.TelemetryIntervalMs == 500, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 500));
        AddRadioOption(hwMenu, "采样周期：1.0 秒（推荐）", s.TelemetryIntervalMs == 1000, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 1000));
        AddRadioOption(hwMenu, "采样周期：2.0 秒", s.TelemetryIntervalMs == 2000, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 2000));
        hwMenu.DropDownItems.Add(new ToolStripSeparator());
        var oledItem = new ToolStripMenuItem("OLED 防烧屏微位移") { Checked = s.OledPixelShift };
        oledItem.Click += (_, _) => _controller.UpdateSettings(x => x.OledPixelShift = !x.OledPixelShift);
        hwMenu.DropDownItems.Add(oledItem);
        _menu.Items.Add(hwMenu);

        // 6. Market & K-Line Quick Settings
        var marketMenu = new ToolStripMenuItem("行情与 K 线设置");
        var catSub = new ToolStripMenuItem("行情板块");
        AddRadioOption(catSub, "虚拟货币", s.ActiveCategory == "crypto", () => _controller.SelectCategory("crypto"));
        AddRadioOption(catSub, "A 股", s.ActiveCategory == "cn", () => _controller.SelectCategory("cn"));
        AddRadioOption(catSub, "美股", s.ActiveCategory == "us", () => _controller.SelectCategory("us"));
        AddRadioOption(catSub, "外汇 / 黄金", s.ActiveCategory == "fx", () => _controller.SelectCategory("fx"));
        AddRadioOption(catSub, "全部标的", s.ActiveCategory == "all", () => _controller.SelectCategory("all"));
        marketMenu.DropDownItems.Add(catSub);

        var ivSub = new ToolStripMenuItem("K 线周期");
        foreach (var iv in new[] { "1m", "5m", "15m", "1h", "4h", "1d", "1w" })
        {
            var curIv = iv;
            AddRadioOption(ivSub, curIv.ToUpperInvariant(), string.Equals(s.ActiveInterval, curIv, StringComparison.OrdinalIgnoreCase), () => _controller.SelectInterval(curIv));
        }
        marketMenu.DropDownItems.Add(ivSub);

        var indSub = new ToolStripMenuItem("技术指标与色系");
        var maItem = new ToolStripMenuItem("MA5 / 10 / 20 均线") { Checked = s.ShowMA };
        maItem.Click += (_, _) => _controller.UpdateSettings(x => x.ShowMA = !x.ShowMA);
        indSub.DropDownItems.Add(maItem);

        var bollItem = new ToolStripMenuItem("BOLL 布林带") { Checked = s.ShowBoll };
        bollItem.Click += (_, _) => _controller.UpdateSettings(x => x.ShowBoll = !x.ShowBoll);
        indSub.DropDownItems.Add(bollItem);

        var volItem = new ToolStripMenuItem("成交量") { Checked = s.ShowVolume };
        volItem.Click += (_, _) => _controller.UpdateSettings(x => x.ShowVolume = !x.ShowVolume);
        indSub.DropDownItems.Add(volItem);

        indSub.DropDownItems.Add(new ToolStripSeparator());
        AddRadioOption(indSub, "红涨绿跌（国内）", s.ColorScheme != "global", () => _controller.UpdateSettings(x => x.ColorScheme = "cn"));
        AddRadioOption(indSub, "绿涨红跌（国际）", s.ColorScheme == "global", () => _controller.UpdateSettings(x => x.ColorScheme = "global"));
        marketMenu.DropDownItems.Add(indSub);

        var exSub = new ToolStripMenuItem("虚拟货币交易所");
        foreach (var (code, label) in new[] { ("binance", "Binance 币安"), ("okx", "OKX 欧易"), ("bybit", "Bybit"), ("bitget", "Bitget"), ("gate", "Gate.io") })
        {
            var c = code;
            AddRadioOption(exSub, label, string.Equals(s.CryptoExchange, c, StringComparison.OrdinalIgnoreCase), () => _controller.SetCryptoConfig(c, null));
        }
        exSub.DropDownItems.Add(new ToolStripSeparator());
        AddRadioOption(exSub, "U 本位永续合约", s.CryptoMarketType != "spot", () => _controller.SetCryptoConfig(null, "futures"));
        AddRadioOption(exSub, "现货交易", s.CryptoMarketType == "spot", () => _controller.SetCryptoConfig(null, "spot"));
        marketMenu.DropDownItems.Add(exSub);

        marketMenu.DropDownItems.Add(new ToolStripSeparator());
        marketMenu.DropDownItems.Add("立即刷新行情", null, (_, _) => _controller.RefreshMarketNow());
        _menu.Items.Add(marketMenu);

        // 7. Network Exit IPv4 (Glance + Copy + Refresh)
        var ipMenu = new ToolStripMenuItem("网络出口 IPv4");
        var netIp = _controller.LastNetworkIp;
        AddIpCopyItem(ipMenu, "国内站", netIp?.Domestic, netIp?.IsChecking ?? false);
        AddIpCopyItem(ipMenu, "国外站", netIp?.Overseas, netIp?.IsChecking ?? false);
        ipMenu.DropDownItems.Add(new ToolStripSeparator());
        ipMenu.DropDownItems.Add("立即重新检测 IP", null, (_, _) => _controller.RefreshNetworkIpNow());
        _menu.Items.Add(ipMenu);

        // 8. Cross-GPU Render Mode
        var renderMenu = new ToolStripMenuItem("跨显卡渲染模式");
        AddRadioOption(renderMenu, "跨显卡安全模式（推荐）", string.Equals(s.RenderMode, "safe", StringComparison.OrdinalIgnoreCase), () => _controller.SetRenderMode("safe"));
        AddRadioOption(renderMenu, "禁用 DirectComposition", string.Equals(s.RenderMode, "nodcomp", StringComparison.OrdinalIgnoreCase), () => _controller.SetRenderMode("nodcomp"));
        AddRadioOption(renderMenu, "原生 GPU 硬件加速", string.Equals(s.RenderMode, "gpu", StringComparison.OrdinalIgnoreCase), () => _controller.SetRenderMode("gpu"));
        _menu.Items.Add(renderMenu);

        _menu.Items.Add(new ToolStripSeparator());

        // 9. Software Update Quick Entry
        var upd = _controller.GetUpdateState();
        if (upd.Status == UpdateStatus.Downloading)
        {
            var dlItem = new ToolStripMenuItem($"正在下载更新 {upd.LatestRelease?.TagName} ({upd.ProgressPercent:F0}%)")
            {
                Font = _menuBoldFont
            };
            dlItem.Click += (_, _) => _controller.ShowUpdateWindow();
            _menu.Items.Add(dlItem);
        }
        else if (upd.Status == UpdateStatus.ReadyToInstall && upd.LatestRelease != null)
        {
            var readyItem = new ToolStripMenuItem($"更新 {upd.LatestRelease.TagName} 已就绪（点击安装）")
            {
                Font = _menuBoldFont
            };
            readyItem.Click += (_, _) => _controller.ShowUpdateWindow();
            _menu.Items.Add(readyItem);
        }
        else if (upd.HasUpdate && upd.LatestRelease != null)
        {
            string ignoredSuffix = upd.IsIgnoredVersion ? " · 已忽略" : "";
            var availItem = new ToolStripMenuItem($"发现新版本 {upd.LatestRelease.TagName}{ignoredSuffix}（查看更新）")
            {
                Font = _menuBoldFont
            };
            availItem.Click += (_, _) => _controller.ShowUpdateWindow();
            _menu.Items.Add(availItem);
        }
        else
        {
            var checkItem = new ToolStripMenuItem($"检查更新（当前 v{upd.CurrentVersion}）");
            checkItem.Click += (_, _) => _ = _controller.CheckForUpdatesAsync(manual: true, showDialogIfAvailable: true, showUpToDateBalloon: true);
            _menu.Items.Add(checkItem);
        }

        if (!_controller.IsElevated)
        {
            _menu.Items.Add("以管理员身份重启（解锁 CPU 温度）", null, (_, _) => _controller.RestartAsAdministrator());
        }

        _menu.Items.Add("退出星澪硬件监控", null, (_, _) => _controller.ShutdownApplication());
    }

    private static void AddRadioOption(ToolStripMenuItem parent, string text, bool isChecked, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Checked = isChecked };
        item.Click += (_, _) => onClick();
        parent.DropDownItems.Add(item);
    }

    private static void AddIpCopyItem(ToolStripMenuItem parent, string label, IpEndpointInfo? ep, bool checking)
    {
        if (ep != null && ep.Ok && !string.IsNullOrWhiteSpace(ep.Ip))
        {
            string loc = string.IsNullOrWhiteSpace(ep.Location) ? "" : $"（{ep.Location}）";
            var item = new ToolStripMenuItem($"{label}：{ep.Ip}{loc}  [点击复制]");
            string ipToCopy = ep.Ip;
            item.Click += (_, _) =>
            {
                try { System.Windows.Clipboard.SetText(ipToCopy); } catch { }
            };
            parent.DropDownItems.Add(item);
        }
        else
        {
            parent.DropDownItems.Add(new ToolStripMenuItem($"{label}：{(checking ? "检测中" : "未连通")}") { Enabled = false });
        }
    }

    /// <summary>
    /// Creates a crisp MioLab brand-blue (#2B5FD9) tray icon that stands out cleanly
    /// on both light and dark Windows taskbars, without leaking GDI HICON handles.
    /// </summary>
    private static Icon CreateMioLabIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Brand blue rounded tile (#2B5FD9)
            using var bgBrush = new SolidBrush(Color.FromArgb(0x2B, 0x5F, 0xD9));
            using var path = CreateRoundedRect(new Rectangle(1, 1, 30, 30), 6);
            g.FillPath(bgBrush, path);

            // Crisp white monitor outline
            using var pen = new Pen(Color.White, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawRectangle(pen, 6, 7, 20, 14);
            g.DrawLine(pen, 12, 24, 20, 24);
            g.DrawLine(pen, 16, 21, 16, 24);

            // Clean telemetry pulse inside monitor
            using var pulsePen = new Pen(Color.White, 1.8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pulsePen, new[]
            {
                new PointF(9, 15.5f),
                new PointF(12.5f, 15.5f),
                new PointF(14.5f, 11f),
                new PointF(17.5f, 17.5f),
                new PointF(19.5f, 13.5f),
                new PointF(23f, 13.5f)
            });
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            Win32Interop.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        catch { }

        try { _menu?.Dispose(); _menu = null; } catch { }
        try { _icon?.Dispose(); _icon = null; } catch { }
        try { _menuFont.Dispose(); } catch { }
        try { _menuBoldFont.Dispose(); } catch { }
    }

    private sealed class MioLabToolStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public MioLabToolStripRenderer(bool dark) : base(new MioLabColorTable(dark))
        {
            _dark = dark;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                e.TextColor = _dark ? Color.FromArgb(0xA1, 0xA9, 0xB6) : Color.FromArgb(0x5B, 0x64, 0x72);
            }
            else if (e.Item is ToolStripMenuItem mi && mi.Checked)
            {
                e.TextColor = _dark ? Color.FromArgb(0x7B, 0xA3, 0xF2) : Color.FromArgb(0x2B, 0x5F, 0xD9);
            }
            else
            {
                e.TextColor = _dark ? Color.FromArgb(0xE6, 0xE8, 0xEC) : Color.FromArgb(0x1F, 0x29, 0x37);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                Color hover = _dark ? Color.FromArgb(0x1F, 0x23, 0x2B) : Color.FromArgb(0xF1, 0xF3, 0xF5);
                using var b = new SolidBrush(hover);
                e.Graphics.FillRectangle(b, rect);
            }
            else if (e.Item is ToolStripMenuItem mi && mi.Checked)
            {
                Color subtle = _dark ? Color.FromArgb(0x1B, 0x2A, 0x45) : Color.FromArgb(0xEA, 0xF1, 0xFB);
                using var b = new SolidBrush(subtle);
                e.Graphics.FillRectangle(b, rect);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            Color accent = _dark ? Color.FromArgb(0x7B, 0xA3, 0xF2) : Color.FromArgb(0x2B, 0x5F, 0xD9);
            using var pen = new Pen(accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new Point(r.Left + 3, r.Top + r.Height / 2),
                new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4),
                new Point(r.Right - 3, r.Top + 4)
            });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Color sep = _dark ? Color.FromArgb(0x2A, 0x2F, 0x3A) : Color.FromArgb(0xE4, 0xE7, 0xEC);
            int y = e.Item.Height / 2;
            using var pen = new Pen(sep, 1f);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _dark ? Color.FromArgb(0xA1, 0xA9, 0xB6) : Color.FromArgb(0x5B, 0x64, 0x72);
            base.OnRenderArrow(e);
        }
    }

    private sealed class MioLabColorTable : ProfessionalColorTable
    {
        private readonly bool _dark;
        public MioLabColorTable(bool dark) => _dark = dark;

        private Color Surface => _dark ? Color.FromArgb(0x17, 0x1A, 0x21) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        private Color Border => _dark ? Color.FromArgb(0x2A, 0x2F, 0x3A) : Color.FromArgb(0xE4, 0xE7, 0xEC);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
