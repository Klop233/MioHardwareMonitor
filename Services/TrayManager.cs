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
        _icon = LoadAppIcon() ?? CreateMioLabIcon();

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
            string cpuText;
            if (snap.Cpus != null && snap.Cpus.Count > 1)
            {
                cpuText = string.Join(" · ", snap.Cpus.Take(2).Select(c =>
                    c.TemperatureC.HasValue
                        ? $"CPU{c.Index + 1} {Math.Round(c.TotalUsagePercent)}% {Math.Round(c.TemperatureC.Value)}°C"
                        : $"CPU{c.Index + 1} {Math.Round(c.TotalUsagePercent)}%"));
            }
            else
            {
                string cpuTemp = snap.Cpu.TemperatureC.HasValue ? $" {Math.Round(snap.Cpu.TemperatureC.Value)}°C" : "";
                cpuText = $"CPU {Math.Round(snap.Cpu.TotalUsagePercent)}%{cpuTemp}";
            }

            string gpuTemp = snap.Gpu.TemperatureC.HasValue ? $" {Math.Round(snap.Gpu.TemperatureC.Value)}°C" : "";
            string text = $"星澪 · {cpuText} · GPU {Math.Round(snap.Gpu.UsagePercent)}%{gpuTemp} · 内存 {Math.Round(snap.Memory.UsagePercent)}%";
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
        var settings = _controller.Settings;
        var palette = SkinCatalog.ForSettings(settings);
        _menu.Renderer = new MioLabToolStripRenderer(palette);
        _menu.BackColor = ToColor(palette.BgSurface);
        _menu.ForeColor = ToColor(palette.TextMain);
    }

    private static Color ToColor((byte R, byte G, byte B) c) => Color.FromArgb(c.R, c.G, c.B);

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
        var openSettings = new ToolStripMenuItem("打开管理面板（设置窗口）")
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

        var rotSub = new ToolStripMenuItem("硬件独占旋转");
        int curRot = DirectDisplaySession.NormalizeRotation(s.DirectDisplayRotation);
        foreach (var (deg, label) in new[] { (0, "0°（不旋转）"), (90, "顺时针 90°（竖屏）"), (180, "180°（上下颠倒）"), (270, "顺时针 270°（竖屏 · 反向）") })
        {
            int capturedDeg = deg;
            AddRadioOption(rotSub, label, curRot == capturedDeg, () => _controller.SetDirectDisplayRotation(capturedDeg));
        }
        modeMenu.DropDownItems.Add(rotSub);

        var fpsSub = new ToolStripMenuItem("硬件独占帧率");
        foreach (var (fps, label) in new[] { (5, "5 FPS（超低占用）"), (10, "10 FPS（推荐）"), (15, "15 FPS（较高流畅度）"), (30, "30 FPS（高刷输出）") })
        {
            int capturedFps = fps;
            AddRadioOption(fpsSub, label, s.DirectDisplayFps == capturedFps, () => _controller.SetDirectDisplayFps(capturedFps));
        }
        modeMenu.DropDownItems.Add(fpsSub);

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

        var autoStartItem = new ToolStripMenuItem("开机自动启动（恢复上次模式）") { Checked = _controller.IsAutoStartEnabled };
        autoStartItem.Click += (_, _) =>
        {
            bool next = !_controller.IsAutoStartEnabled;
            var res = _controller.SetAutoStart(next);
            if (!res.Success)
            {
                ShowBalloon("设置开机自启失败", res.ErrorMessage ?? "请检查系统权限后重试。");
            }
        };
        _menu.Items.Add(autoStartItem);

        _menu.Items.Add(new ToolStripSeparator());

        // 5. Monitor Page, Layout, Theme & Hardware Quick Settings
        var pageMenu = new ToolStripMenuItem("监控页面");
        string curPage = SettingsService.NormalizeMonitorPage(s.MonitorPage);
        AddRadioOption(pageMenu, "硬件监控总览 (Alt+1)", curPage == "overview", () => _controller.SetMonitorPage("overview"));
        AddRadioOption(pageMenu, "进程与线程树 (Alt+2)", curPage == "proctree", () => _controller.SetMonitorPage("proctree"));
        pageMenu.DropDownItems.Add(new ToolStripSeparator());
        var autoFollowTopItem = new ToolStripMenuItem("进程树：自动追踪最高负载") { Checked = s.ProcessTreeAutoFollowTop };
        autoFollowTopItem.Click += (_, _) =>
        {
            bool next = !s.ProcessTreeAutoFollowTop;
            int? pinPid = next
                ? 0
                : (_controller.LatestProcessTree?.ActivePid is int active && active > 0 ? active : null);
            _controller.SetProcessTreeOptions(autoFollowTop: next, pinnedPid: pinPid);
        };
        pageMenu.DropDownItems.Add(autoFollowTopItem);
        string curTreeView = SettingsService.NormalizeProcessTreeViewMode(s.ProcessTreeViewMode);
        AddRadioOption(pageMenu, "进程树视图：树状层级", curTreeView == "tree", () => _controller.SetProcessTreeOptions(viewMode: "tree"));
        AddRadioOption(pageMenu, "进程树视图：扁平排行", curTreeView == "flat", () => _controller.SetProcessTreeOptions(viewMode: "flat"));
        _menu.Items.Add(pageMenu);

        var layoutMenu = new ToolStripMenuItem("布局视图");
        if (AppFeatures.MarketEnabled)
            AddRadioOption(layoutMenu, "综合看板（性能 + K 线）", s.LayoutMode == "split", () => _controller.UpdateSettings(x => x.LayoutMode = "split"));
        AddRadioOption(layoutMenu, "性能专注模式", s.LayoutMode == "hardware", () => _controller.UpdateSettings(x => x.LayoutMode = "hardware"));
        if (AppFeatures.MarketEnabled)
            AddRadioOption(layoutMenu, "行情专注模式", s.LayoutMode == "market", () => _controller.UpdateSettings(x => x.LayoutMode = "market"));
        AddRadioOption(layoutMenu, "自定义排版", s.LayoutMode == "custom", () => _controller.UpdateSettings(x => x.LayoutMode = "custom"));
        _menu.Items.Add(layoutMenu);

        var themeMenu = new ToolStripMenuItem("界面主题");
        AddRadioOption(themeMenu, "浅色（星澪标准）", !isDark, () => _controller.UpdateSettings(x => x.Theme = "light"));
        AddRadioOption(themeMenu, "深色（星澪深色）", isDark, () => _controller.UpdateSettings(x => x.Theme = "dark"));
        var oledBlackItem = new ToolStripMenuItem("OLED 纯黑模式（深色下 #000000）") { Checked = s.OledTrueBlack };
        oledBlackItem.Click += (_, _) => _controller.UpdateSettings(x => x.OledTrueBlack = !x.OledTrueBlack);
        themeMenu.DropDownItems.Add(oledBlackItem);

        themeMenu.DropDownItems.Add(new ToolStripSeparator());
        string hudStyle = SettingsService.NormalizeHudStyle(s.HudStyle);
        AddRadioOption(themeMenu, "画面风格：精密仪器（仪表盘）", hudStyle == "instrument", () => _controller.UpdateSettings(x => x.HudStyle = "instrument"));
        AddRadioOption(themeMenu, "画面风格：星澪扁平（经典极简）", hudStyle == "flat", () => _controller.UpdateSettings(x => x.HudStyle = "flat"));

        themeMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var skin in SkinCatalog.All)
        {
            var sk = skin;
            AddRadioOption(themeMenu, $"皮肤：{skin.Name}", SkinCatalog.Normalize(s.Skin) == skin.Key,
                () => _controller.UpdateSettings(x => x.Skin = sk.Key));
        }
        _menu.Items.Add(themeMenu);

        var perfMenu = new ToolStripMenuItem("性能模式（传感器频率）");
        AddRadioOption(perfMenu, "节能 · 温度功耗 6 秒/次", SettingsService.NormalizePerformanceMode(s.PerformanceMode) == "eco", () => _controller.SetPerformanceMode("eco"));
        AddRadioOption(perfMenu, "均衡 · 温度功耗 3 秒/次（默认）", SettingsService.NormalizePerformanceMode(s.PerformanceMode) == "balanced", () => _controller.SetPerformanceMode("balanced"));
        AddRadioOption(perfMenu, "实时 · 温度功耗 1 秒/次", SettingsService.NormalizePerformanceMode(s.PerformanceMode) == "realtime", () => _controller.SetPerformanceMode("realtime"));
        _menu.Items.Add(perfMenu);

        var hwMenu = new ToolStripMenuItem("硬件监控设置");
        string curCpuView = SettingsService.NormalizeCpuViewMode(s.CpuViewMode);
        AddRadioOption(hwMenu, "CPU：紧凑核心格（推荐 · 省空间）", curCpuView == "compact", () => _controller.UpdateSettings(x => x.CpuViewMode = "compact"));
        AddRadioOption(hwMenu, "CPU：完整多核曲线（任务管理器）", curCpuView == "cores", () => _controller.UpdateSettings(x => x.CpuViewMode = "cores"));
        AddRadioOption(hwMenu, "CPU：仅总体曲线", curCpuView == "overall", () => _controller.UpdateSettings(x => x.CpuViewMode = "overall"));
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
        AddRadioOption(hwMenu, "采样周期：0.5 秒（最流畅 · 占用较高）", s.TelemetryIntervalMs == 500, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 500));
        AddRadioOption(hwMenu, "采样周期：1.0 秒", s.TelemetryIntervalMs == 1000, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 1000));
        AddRadioOption(hwMenu, "采样周期：1.5 秒（推荐 · 默认）", s.TelemetryIntervalMs == 1500, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 1500));
        AddRadioOption(hwMenu, "采样周期：3.0 秒（最省电）", s.TelemetryIntervalMs == 3000, () => _controller.UpdateSettings(x => x.TelemetryIntervalMs = 3000));
        hwMenu.DropDownItems.Add(new ToolStripSeparator());
        var oledItem = new ToolStripMenuItem("OLED 防烧屏微位移") { Checked = s.OledPixelShift };
        oledItem.Click += (_, _) => _controller.UpdateSettings(x => x.OledPixelShift = !x.OledPixelShift);
        hwMenu.DropDownItems.Add(oledItem);

        var maskIpItem = new ToolStripMenuItem("隐私模式：隐藏公网 IP") { Checked = s.MaskPublicIp };
        maskIpItem.Click += (_, _) => _controller.UpdateSettings(x => x.MaskPublicIp = !x.MaskPublicIp);
        hwMenu.DropDownItems.Add(maskIpItem);

        hwMenu.DropDownItems.Add(new ToolStripSeparator());
        hwMenu.DropDownItems.Add("重置会话峰值", null, (_, _) => _controller.ResetSessionStats());

        var recStatus = _controller.RecordingStatus;
        if (recStatus.IsRecording)
        {
            var stopRecItem = new ToolStripMenuItem($"停止录制 CSV（已录 {recStatus.SampleCount} 样本）") { Checked = true };
            stopRecItem.Click += (_, _) => _controller.StopRecording();
            hwMenu.DropDownItems.Add(stopRecItem);
        }
        else
        {
            hwMenu.DropDownItems.Add("开始录制 CSV 遥测", null, (_, _) =>
            {
                var res = _controller.StartRecording();
                if (!res.Success)
                {
                    ShowBalloon("开始录制失败", res.ErrorMessage ?? "无法创建 CSV 日志文件。");
                }
            });
        }
        hwMenu.DropDownItems.Add("打开遥测日志目录", null, (_, _) => _controller.OpenLogsFolder());
        hwMenu.DropDownItems.Add("告警、录制与配置设置...", null, (_, _) => _controller.ShowControlWindow("pro"));
        _menu.Items.Add(hwMenu);

        // 6. Market & K-Line Quick Settings (feature-gated)
        if (AppFeatures.MarketEnabled)
        {
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

        }

        // 7. Network Exit IPv4 (Glance + Copy + Refresh)
        var ipMenu = new ToolStripMenuItem("网络出口 IPv4");
        var netIp = _controller.LastNetworkIp;
        AddIpCopyItem(ipMenu, "国内站", netIp?.Domestic, netIp?.IsChecking ?? false, s.MaskPublicIp);
        AddIpCopyItem(ipMenu, "国外站", netIp?.Overseas, netIp?.IsChecking ?? false, s.MaskPublicIp);
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

        if (AppFeatures.DonationEnabled)
        {
            var donateItem = new ToolStripMenuItem("赞助支持（微信支付）");
            donateItem.Click += (_, _) => _controller.ShowControlWindow("donate");
            _menu.Items.Add(donateItem);
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

    private static void AddIpCopyItem(ToolStripMenuItem parent, string label, IpEndpointInfo? ep, bool checking, bool maskIp)
    {
        if (ep != null && ep.Ok && !string.IsNullOrWhiteSpace(ep.Ip))
        {
            string displayIp = maskIp ? SettingsService.MaskIpAddress(ep.Ip) : ep.Ip;
            string loc = string.IsNullOrWhiteSpace(ep.Location) ? "" : $"（{ep.Location}）";
            string riskBadge = FormatIpRiskSuffix(ep);
            string suffix = maskIp ? "  [已隐私遮罩 · 点击复制]" : "  [点击复制]";
            var item = new ToolStripMenuItem($"{label}：{displayIp}{loc}{riskBadge}{suffix}");
            string tooltip = BuildIpRiskTooltip(ep);
            if (!string.IsNullOrEmpty(tooltip))
            {
                item.ToolTipText = tooltip;
            }
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

    private static string FormatIpRiskSuffix(IpEndpointInfo ep)
    {
        var parts = new List<string>(2);
        if (ep.PurityScore.HasValue)
            parts.Add($"纯净{ep.PurityScore.Value}");
        if (!string.IsNullOrWhiteSpace(ep.IpType))
            parts.Add(ep.IpType);
        else if (!string.IsNullOrWhiteSpace(ep.RiskLabel))
            parts.Add(ep.RiskLabel);
        return parts.Count > 0 ? $" [{string.Join("·", parts)}]" : "";
    }

    private static string BuildIpRiskTooltip(IpEndpointInfo ep)
    {
        var parts = new List<string>();
        if (ep.PurityScore.HasValue && ep.FraudScore.HasValue)
            parts.Add($"纯净度 {ep.PurityScore.Value} / 欺诈分 {ep.FraudScore.Value}");
        if (!string.IsNullOrWhiteSpace(ep.RiskLabel))
            parts.Add($"风险：{ep.RiskLabel}");
        if (!string.IsNullOrWhiteSpace(ep.IpType))
            parts.Add($"类型：{ep.IpType}");
        if (!string.IsNullOrWhiteSpace(ep.Asn))
            parts.Add(ep.Asn);
        if (!string.IsNullOrWhiteSpace(ep.Isp))
            parts.Add(ep.Isp);
        if (!string.IsNullOrWhiteSpace(ep.RiskSource))
            parts.Add($"来源：{ep.RiskSource}");
        return string.Join(" · ", parts);
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info?.Stream != null)
            {
                using var stream = info.Stream;
                return new Icon(stream, new Size(32, 32));
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Fallback MioLab brand-blue (#2B5FD9) tray icon when Assets/app.ico resource is unavailable.
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
        private readonly SkinPalette _p;

        public MioLabToolStripRenderer(SkinPalette palette) : base(new MioLabColorTable(palette))
        {
            _p = palette;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                e.TextColor = ToColor(_p.TextMuted);
            }
            else if (e.Item is ToolStripMenuItem mi && mi.Checked)
            {
                e.TextColor = ToColor(_p.AccentText);
            }
            else
            {
                e.TextColor = ToColor(_p.TextMain);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                Color hover = ToColor(_p.BgHover);
                using var b = new SolidBrush(hover);
                e.Graphics.FillRectangle(b, rect);
            }
            else if (e.Item is ToolStripMenuItem mi && mi.Checked)
            {
                Color subtle = ToColor(_p.AccentSubtle);
                using var b = new SolidBrush(subtle);
                e.Graphics.FillRectangle(b, rect);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            Color accent = ToColor(_p.AccentText);
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
            Color sep = ToColor(_p.Border);
            int y = e.Item.Height / 2;
            using var pen = new Pen(sep, 1f);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = ToColor(_p.TextMuted);
            base.OnRenderArrow(e);
        }
    }

    private sealed class MioLabColorTable : ProfessionalColorTable
    {
        private readonly SkinPalette _p;
        public MioLabColorTable(SkinPalette palette) => _p = palette;

        private Color Surface => ToColor(_p.BgSurface);
        private Color Border => ToColor(_p.Border);

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
