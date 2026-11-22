using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SideScreenHUD.Models;
using SideScreenHUD.Native;

namespace SideScreenHUD.Services;

public sealed class SparkHistoryPoint
{
    public double Cpu { get; set; }
    public double Gpu { get; set; }
    public double Ram { get; set; }
    public double NetDn { get; set; }
    public double NetUp { get; set; }
    public double[]? Cores { get; set; }
    public double[]? SocketUsages { get; set; }
    public List<double[]>? SocketCores { get; set; }
}

/// <summary>
/// Central application controller that owns hardware telemetry, market feeds,
/// settings, system tray, the persistent ControlWindow, and the disposable DisplayWindow.
/// Switching monitors destroys and recreates ONLY DisplayWindow directly on the target GPU/monitor.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly SettingsService _settingsService = new();
    private readonly DisplayManager _displayManager = new();
    private readonly MarketDataService _marketService = new();
    private readonly NetworkIpService _networkIpService = new();
    private readonly UpdateService _updateService = new();
    private HardwareMonitorService? _hardwareService;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _klineFetchLock = new(1, 1);
    private readonly SemaphoreSlim _recreateLock = new(1, 1);
    private readonly SemaphoreSlim _ipDetectLock = new(1, 1);

    private ControlWindow? _controlWindow;
    private DisplayWindow? _displayWindow;
    private UpdateWindow? _updateWindow;
    private TrayManager? _trayManager;
    private string _dismissedUpdateTagThisSession = "";
    private bool _disposed;

    // In-memory state buffers so newly created DisplayWindows hydrate immediately
    private readonly object _cacheSync = new();
    private readonly Queue<SparkHistoryPoint> _sparkHistory = new();
    private TelemetrySnapshot? _lastTelemetry;
    private NetworkIpSnapshot? _lastNetworkIp;
    private List<QuoteItem>? _lastQuotes;
    private KlinePayload? _lastKline;
    private FxRatesPayload? _lastFxRates;

    public AppSettings Settings => _settingsService.Current;
    public DisplayManager DisplayManager => _displayManager;
    public bool IsDisplayOpen => _displayWindow != null;
    public bool IsElevated => _lastTelemetry?.IsElevated ?? false;
    public MonitorDescriptor? CurrentDisplayMonitor => _displayWindow?.CurrentMonitor;
    public MonitorDescriptor? TargetMonitor => _displayManager.ResolveTargetMonitor(Settings.TargetMonitorDevice, preferSecondary: true);
    public TelemetrySnapshot? LastTelemetry { get { lock (_cacheSync) return _lastTelemetry; } }
    public NetworkIpSnapshot? LastNetworkIp { get { lock (_cacheSync) return _lastNetworkIp; } }
    public FxRatesPayload? LastFxRates { get { lock (_cacheSync) return _lastFxRates; } }

    public event Action? StateChanged;
    public event Action<UpdateStateSnapshot>? UpdateStateChanged;

    public UpdateStateSnapshot GetUpdateState() => _updateService.GetState();

    public void Start()
    {
        _updateService.StateChanged += snap =>
        {
            try { UpdateStateChanged?.Invoke(snap); } catch { }
            RefreshTrayMenu();
            NotifyStateChanged();
        };

        // Resolve initial target monitor (prefer secondary display if available)
        var target = _displayManager.ResolveTargetMonitor(Settings.TargetMonitorDevice, preferSecondary: true);
        if (target != null && string.IsNullOrWhiteSpace(Settings.TargetMonitorDevice))
        {
            Settings.TargetMonitorDevice = target.DeviceName;
            _settingsService.Save();
        }

        _trayManager = new TrayManager(this);
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;

        // Initialize hardware sensors in background
        _ = Task.Run(() =>
        {
            try { _hardwareService = new HardwareMonitorService(); }
            catch { }
        });

        StartBackgroundLoops();

        // Open DisplayWindow on target monitor
        if (Settings.AutoOpenDisplayOnStartup)
        {
            _ = RecreateDisplayWindowAsync();
        }

        // Open persistent native WPF ControlWindow on primary monitor
        if (Settings.ShowControlOnStartup || !Settings.AutoOpenDisplayOnStartup)
        {
            ShowControlWindow();
        }
    }

    public void ShowControlWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_controlWindow == null)
            {
                _controlWindow = new ControlWindow(this);
                _controlWindow.Closed += (_, _) => _controlWindow = null;
            }
            _controlWindow.Show();
            if (_controlWindow.WindowState == WindowState.Minimized)
                _controlWindow.WindowState = WindowState.Normal;
            SyncControlWindowTopMost();
            _controlWindow.Activate();
            _controlWindow.RefreshFromController();
        });
    }

    public void SyncControlWindowTopMost()
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_controlWindow == null || !_controlWindow.IsVisible) return;
            bool needTopMost = false;
            if (_displayWindow != null && Settings.AlwaysOnTop)
            {
                var ctrlHwnd = new WindowInteropHelper(_controlWindow).Handle;
                var ctrlMon = ctrlHwnd != IntPtr.Zero ? _displayManager.GetMonitorForWindow(ctrlHwnd) : null;
                var dispMon = _displayWindow.CurrentMonitor;
                needTopMost = ctrlMon == null || string.Equals(ctrlMon.DeviceName, dispMon.DeviceName, StringComparison.OrdinalIgnoreCase);
            }
            _controlWindow.Topmost = needTopMost;
        });
    }

    public void SaveControlWindowPosition(int physicalLeft, int physicalTop)
    {
        Settings.ControlWindowLeft = physicalLeft;
        Settings.ControlWindowTop = physicalTop;
        _settingsService.Save();
        SyncControlWindowTopMost();
    }

    public void CloseDisplayWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            DestroyDisplayWindowInternal();
            SyncControlWindowTopMost();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    /// <summary>
    /// Completely destroys any existing DisplayWindow (and its WebView2/DXGI surface)
    /// and creates a fresh DisplayWindow bound directly to the target monitor.
    /// </summary>
    public async Task RecreateDisplayWindowAsync(string? overrideDeviceName = null)
    {
        await _recreateLock.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(overrideDeviceName))
            {
                Settings.TargetMonitorDevice = overrideDeviceName;
                _settingsService.Save();
            }

            var target = _displayManager.ResolveTargetMonitor(Settings.TargetMonitorDevice, preferSecondary: true);
            if (target == null) return;

            Settings.TargetMonitorDevice = target.DeviceName;
            _settingsService.Save();

            bool hadExisting = false;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                hadExisting = _displayWindow != null;
                DestroyDisplayWindowInternal();
            });

            // Brief pause ensures old WebView2 controller & HWND fully release cross-GPU resources
            if (hadExisting)
            {
                await Task.Delay(120);
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                var win = new DisplayWindow(this, target);
                win.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_displayWindow, win))
                    {
                        _displayWindow = null;
                        RefreshTrayMenu();
                        NotifyStateChanged();
                    }
                };
                _displayWindow = win;
                win.Show();
                SyncControlWindowTopMost();
                RefreshTrayMenu();
                NotifyStateChanged();
            });
        }
        finally
        {
            _recreateLock.Release();
        }
    }

    private void DestroyDisplayWindowInternal()
    {
        if (_displayWindow == null) return;
        var old = _displayWindow;
        _displayWindow = null;
        try
        {
            old.ForceCloseAndDispose();
        }
        catch { }
    }

    public void SwitchMonitor(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return;
        // Always destroy & recreate DisplayWindow on the new monitor to guarantee zero cross-GPU black screen!
        _ = RecreateDisplayWindowAsync(deviceName);
    }

    public void SetFullScreen(bool isFullScreen)
    {
        Settings.IsFullScreen = isFullScreen;
        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.ApplyDisplayModeAndGeometry();
            SyncControlWindowTopMost();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void ToggleFullScreen()
    {
        Settings.IsFullScreen = !Settings.IsFullScreen;
        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_displayWindow != null)
            {
                _displayWindow.ApplyDisplayModeAndGeometry();
            }
            else
            {
                _ = RecreateDisplayWindowAsync();
            }
            SyncControlWindowTopMost();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetWindowPositionPreset(string preset)
    {
        Settings.WindowPositionPreset = SettingsService.NormalizePositionPreset(preset);
        Settings.IsFullScreen = false;

        var mon = CurrentDisplayMonitor ?? TargetMonitor;
        if (mon != null)
        {
            var rect = DisplayManager.CalculateFixedWindowRect(mon, Settings);
            SyncRectIntoSettings(mon, rect);
        }

        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.ApplyDisplayModeAndGeometry();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetCustomWindowGeometry(int offsetX, int offsetY, int width, int height, int edgeMargin)
    {
        Settings.IsFullScreen = false;
        Settings.WindowPositionPreset = "custom";
        Settings.WindowOffsetX = offsetX;
        Settings.WindowOffsetY = offsetY;
        Settings.WindowWidth = Math.Clamp(width, 420, 7680);
        Settings.WindowHeight = Math.Clamp(height, 300, 4320);
        Settings.WindowEdgeMargin = Math.Clamp(edgeMargin, 0, 200);
        Settings.HasWindowOffset = true;

        var mon = CurrentDisplayMonitor ?? TargetMonitor;
        if (mon != null)
        {
            var rect = DisplayManager.CalculateFixedWindowRect(mon, Settings);
            SyncRectIntoSettings(mon, rect);
        }

        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.ApplyDisplayModeAndGeometry();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetWindowEdgeMargin(int edgeMargin)
    {
        Settings.WindowEdgeMargin = Math.Clamp(edgeMargin, 0, 200);
        var mon = CurrentDisplayMonitor ?? TargetMonitor;
        if (mon != null && !string.Equals(Settings.WindowPositionPreset, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var rect = DisplayManager.CalculateFixedWindowRect(mon, Settings);
            SyncRectIntoSettings(mon, rect);
        }
        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (!Settings.IsFullScreen)
            {
                _displayWindow?.ApplyDisplayModeAndGeometry();
            }
            NotifyStateChanged();
        });
    }

    public void SetLockWindowPosition(bool locked)
    {
        Settings.LockWindowPosition = locked;
        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.ApplyLockState(locked);
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetShowDisplayInTaskbar(bool showInTaskbar)
    {
        Settings.ShowDisplayInTaskbar = showInTaskbar;
        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.ApplyShowInTaskbar(showInTaskbar);
            NotifyStateChanged();
        });
    }

    public void CaptureCurrentDisplayGeometry()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.CaptureCurrentGeometryNow(markCustomPreset: true);
        });
    }

    internal void NotifyWindowGeometryChangedFromDisplay(MonitorDescriptor monitor, Win32Interop.RECT rect, bool markCustomPreset)
    {
        Settings.TargetMonitorDevice = monitor.DeviceName;
        if (markCustomPreset)
        {
            Settings.WindowPositionPreset = "custom";
        }
        SyncRectIntoSettings(monitor, rect);
        _settingsService.Save();
        RefreshTrayMenu();
        NotifyStateChanged();
    }

    private void SyncRectIntoSettings(MonitorDescriptor monitor, Win32Interop.RECT rect)
    {
        Settings.WindowOffsetX = rect.Left - monitor.WorkLeft;
        Settings.WindowOffsetY = rect.Top - monitor.WorkTop;
        Settings.WindowLeft = rect.Left;
        Settings.WindowTop = rect.Top;
        Settings.WindowWidth = rect.Width;
        Settings.WindowHeight = rect.Height;
        Settings.HasWindowOffset = true;
    }

    public void SetAlwaysOnTop(bool topMost)
    {
        Settings.AlwaysOnTop = topMost;
        _settingsService.Save();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _displayWindow?.ApplyAlwaysOnTop(topMost);
            SyncControlWindowTopMost();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetRenderMode(string renderMode)
    {
        if (string.Equals(Settings.RenderMode, renderMode, StringComparison.OrdinalIgnoreCase)) return;
        Settings.RenderMode = renderMode;
        _settingsService.Save();

        if (_displayWindow != null)
        {
            _ = RecreateDisplayWindowAsync();
        }
        NotifyStateChanged();
    }

    public void UpdateSettings(Action<AppSettings> mutator, bool pushVisualToDisplay = true)
    {
        mutator(Settings);
        Settings.TelemetryIntervalMs = Math.Clamp(Settings.TelemetryIntervalMs, 500, 5000);
        Settings.Theme = SettingsService.NormalizeTheme(Settings.Theme);
        Settings.LayoutMode = SettingsService.NormalizeLayoutMode(Settings.LayoutMode);
        Settings.CpuDisplayTarget = SettingsService.NormalizeCpuDisplayTarget(Settings.CpuDisplayTarget);
        Settings.CustomLayout = SettingsService.NormalizeCustomLayout(Settings.CustomLayout);
        _settingsService.Save();

        if (pushVisualToDisplay)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _displayWindow?.ApplyThemeChrome(Settings.Theme);
                _displayWindow?.PostSettingsSync(Settings);
            });
        }
        RefreshTrayMenu();
        NotifyStateChanged();
    }

    public void UpdateCustomLayout(CustomLayoutConfig layout, bool activateCustomMode = false)
    {
        UpdateSettings(s =>
        {
            s.CustomLayout = SettingsService.NormalizeCustomLayout(layout);
            if (activateCustomMode)
            {
                s.LayoutMode = "custom";
            }
        });
    }

    public void RefreshNetworkIpNow()
    {
        _ = Task.Run(() => RefreshNetworkIpInternalAsync(_cts.Token));
    }

    public async Task<UpdateStateSnapshot> CheckForUpdatesAsync(bool manual, bool showDialogIfAvailable = true, bool showUpToDateBalloon = false)
    {
        var snap = await _updateService.CheckForUpdatesAsync(Settings.IgnoredUpdateVersion, _cts.Token);

        if (snap.HasUpdate && snap.LatestRelease != null)
        {
            bool suppressedByIgnore = !manual && snap.IsIgnoredVersion;
            bool suppressedByLater = !manual && UpdateService.IsVersionIgnored(snap.LatestRelease.TagName, _dismissedUpdateTagThisSession);

            if (showDialogIfAvailable && !suppressedByIgnore && !suppressedByLater)
            {
                ShowUpdateWindow();
            }
        }
        else if (manual && showUpToDateBalloon)
        {
            if (snap.Status == UpdateStatus.UpToDate)
            {
                _trayManager?.ShowBalloon("星澪硬件监控已是最新版本", $"当前版本 v{snap.CurrentVersion} 无需更新。");
            }
            else if (snap.Status == UpdateStatus.Error)
            {
                _trayManager?.ShowBalloon("检查更新失败", string.IsNullOrWhiteSpace(snap.ErrorMessage) ? "无法连接 GitHub，请稍后重试。" : snap.ErrorMessage);
            }
        }

        return snap;
    }

    public void ShowUpdateWindow()
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_updateWindow == null)
            {
                _updateWindow = new UpdateWindow(this);
                _updateWindow.Closed += (_, _) => _updateWindow = null;
            }
            _updateWindow.Show();
            if (_updateWindow.WindowState == WindowState.Minimized)
                _updateWindow.WindowState = WindowState.Normal;
            _updateWindow.Activate();
            _updateWindow.RefreshFromState(_updateService.GetState());
        });
    }

    public Task<UpdateStateSnapshot> DownloadLatestUpdateAsync() => _updateService.DownloadLatestAsync();

    public void CancelUpdateDownload() => _updateService.CancelDownload();

    public void IgnoreUpdateVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        Settings.IgnoredUpdateVersion = tagName.Trim();
        _settingsService.Save();
        _updateService.UpdateIgnoredFlag(Settings.IgnoredUpdateVersion);
        RefreshTrayMenu();
        NotifyStateChanged();
    }

    public void ClearIgnoredUpdateVersion()
    {
        Settings.IgnoredUpdateVersion = "";
        _dismissedUpdateTagThisSession = "";
        _settingsService.Save();
        _updateService.UpdateIgnoredFlag("");
        RefreshTrayMenu();
        NotifyStateChanged();
    }

    public void RemindUpdateLater(string tagName)
    {
        _dismissedUpdateTagThisSession = (tagName ?? "").Trim();
    }

    public (bool Success, string? ErrorMessage) ApplyDownloadedUpdate()
    {
        var result = _updateService.LaunchDownloadedUpdate();
        if (result.Success)
        {
            ShutdownApplication();
        }
        return result;
    }

    public void SaveSettingsOnly()
    {
        _settingsService.Save();
    }

    public List<WatchlistSymbol> GetAllSymbols() => _marketService.GetAllSymbols(Settings.CustomSymbols);

    public void SelectSymbol(string symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId)) return;
        Settings.ActiveSymbolId = symbolId;
        _settingsService.Save();
        _displayWindow?.PostSettingsSync(Settings);
        _ = Task.Run(() => PushActiveKlineAsync(forceRefresh: false, _cts.Token));
        NotifyStateChanged();
    }

    public void SelectCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        Settings.ActiveCategory = category;
        _settingsService.Save();
        _displayWindow?.PostSettingsSync(Settings);
        _ = Task.Run(() => RefreshQuotesInternalAsync(priorityCategory: category, forceAll: false, _cts.Token));
        NotifyStateChanged();
    }

    public void SelectInterval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval)) return;
        Settings.ActiveInterval = interval;
        _settingsService.Save();
        _displayWindow?.PostSettingsSync(Settings);
        _ = Task.Run(() => PushActiveKlineAsync(forceRefresh: false, _cts.Token));
        NotifyStateChanged();
    }

    public void SetCryptoConfig(string? exchange, string? marketType)
    {
        string nextEx = string.IsNullOrWhiteSpace(exchange) ? Settings.CryptoExchange : MarketDataService.NormalizeExchange(exchange);
        string nextMt = string.IsNullOrWhiteSpace(marketType) ? Settings.CryptoMarketType : MarketDataService.NormalizeMarketType(marketType);

        if (nextEx == Settings.CryptoExchange && nextMt == Settings.CryptoMarketType) return;

        Settings.CryptoExchange = nextEx;
        Settings.CryptoMarketType = nextMt;
        _settingsService.Save();
        _marketService.ClearCryptoCache();

        _displayWindow?.PostSettingsSync(Settings);
        NotifyStateChanged();

        _ = Task.Run(async () =>
        {
            await RefreshQuotesInternalAsync(priorityCategory: "crypto", forceAll: false, _cts.Token);
            await PushActiveKlineAsync(forceRefresh: true, _cts.Token);
        });
    }

    public void RefreshMarketNow()
    {
        _ = Task.Run(async () =>
        {
            await RefreshQuotesInternalAsync(priorityCategory: Settings.ActiveCategory, forceAll: true, _cts.Token);
            await PushActiveKlineAsync(forceRefresh: true, _cts.Token);
        });
    }

    public void AddCustomSymbol(string code, string name, string mode)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code)) return;

        string id, displayCode, provider, providerSym, category;
        string? baseAsset = null;
        int decimals = 2;

        switch ((mode ?? "us_stock").ToLowerInvariant())
        {
            case "crypto":
            case "binance_crypto":
                string b = code.Replace("/", "").Replace("-", "").Replace("_", "");
                if (b.EndsWith("USDT") && b.Length > 4) b = b[..^4];
                id = "CUSTOM_CRYPTO_" + b;
                baseAsset = b;
                displayCode = $"{b}/USDT";
                provider = "crypto";
                providerSym = b;
                category = "crypto";
                decimals = 4;
                break;

            case "cn_stock":
                string digits = code.Replace(".SH", "").Replace(".SS", "").Replace(".SZ", "").Replace(".BJ", "");
                string secid;
                if (digits.Contains('.'))
                {
                    secid = digits;
                    digits = digits.Split('.').Last();
                }
                else
                {
                    // 6xxxxx / 5xxxxx -> Shanghai (1.), 0xxxxx / 3xxxxx / 2xxxxx / 8xxxxx / 4xxxxx -> Shenzhen/BSE (0.)
                    int mkt = (digits.StartsWith('6') || digits.StartsWith('5') || digits.StartsWith('9')) ? 1 : 0;
                    secid = $"{mkt}.{digits}";
                }
                id = "CUSTOM_CN_" + digits;
                displayCode = digits;
                provider = "eastmoney";
                providerSym = secid;
                category = "cn";
                decimals = 2;
                break;

            case "fx":
            case "yahoo_fx":
                providerSym = (!code.Contains('=') && code.Length == 6) ? code + "=X" : code;
                displayCode = code.Length == 6 ? $"{code[..3]}/{code[3..]}" : code.Replace("=X", "");
                id = "CUSTOM_FX_" + code.Replace("=", "").Replace("/", "");
                provider = "yahoo";
                category = "fx";
                decimals = 4;
                break;

            default: // us_stock / yahoo_market
                id = "CUSTOM_US_" + code.Replace("^", "").Replace(".", "").Replace("/", "");
                displayCode = code;
                provider = "yahoo";
                providerSym = code;
                category = "us";
                decimals = 2;
                break;
        }

        var newSym = new WatchlistSymbol
        {
            Id = id,
            DisplayCode = displayCode,
            Name = string.IsNullOrWhiteSpace(name) ? displayCode : name.Trim(),
            Category = category,
            Provider = provider,
            ProviderSymbol = providerSym,
            BaseAsset = baseAsset,
            Decimals = decimals,
            IsCustom = true
        };

        Settings.CustomSymbols.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Settings.CustomSymbols.Add(newSym);
        Settings.ActiveSymbolId = id;
        Settings.ActiveCategory = category;
        _settingsService.Save();

        var all = GetAllSymbols();
        _displayWindow?.PostToWeb("symbolsUpdated", new { symbols = all, activeSymbolId = id, activeCategory = category });
        RefreshMarketNow();
        NotifyStateChanged();
    }

    public void RemoveCustomSymbol(string symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId)) return;
        int removed = Settings.CustomSymbols.RemoveAll(x => x.Id.Equals(symbolId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;

        if (Settings.ActiveSymbolId.Equals(symbolId, StringComparison.OrdinalIgnoreCase))
        {
            Settings.ActiveSymbolId = "USDCNH";
        }
        _settingsService.Save();

        var all = GetAllSymbols();
        _displayWindow?.PostToWeb("symbolsUpdated", new { symbols = all, activeSymbolId = Settings.ActiveSymbolId });
        _ = Task.Run(() => PushActiveKlineAsync(forceRefresh: false, _cts.Token));
        NotifyStateChanged();
    }

    /// <summary>
    /// Hydrates a newly initialized DisplayWindow immediately with all buffered state.
    /// </summary>
    public void HydrateDisplayWindow(DisplayWindow win)
    {
        var monitors = _displayManager.GetMonitors();
        var symbols = GetAllSymbols();

        List<SparkHistoryPoint> historyCopy;
        TelemetrySnapshot? snap;
        NetworkIpSnapshot? netIp;
        List<QuoteItem>? quotes;
        KlinePayload? kline;
        FxRatesPayload? fx;

        lock (_cacheSync)
        {
            historyCopy = _sparkHistory.ToList();
            snap = _lastTelemetry;
            netIp = _lastNetworkIp;
            quotes = _lastQuotes;
            kline = _lastKline;
            fx = _lastFxRates;
        }

        win.PostToWeb("init", new
        {
            settings = Settings,
            monitors,
            isFullScreen = Settings.IsFullScreen,
            alwaysOnTop = Settings.AlwaysOnTop,
            lockWindowPosition = Settings.LockWindowPosition,
            windowPositionPreset = Settings.WindowPositionPreset,
            targetMonitorDevice = win.CurrentMonitor.DeviceName,
            symbols,
            history = historyCopy
        });

        if (snap != null) win.PostToWeb("telemetry", snap);
        if (netIp != null) win.PostToWeb("networkIp", netIp);
        if (quotes != null) win.PostToWeb("quotes", quotes);
        if (kline != null) win.PostToWeb("kline", kline);
        if (fx != null) win.PostToWeb("fxRates", fx);
    }

    public void RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            ShutdownApplication();
        }
        catch { }
    }

    public void ShutdownApplication()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        });
    }

    private void StartBackgroundLoops()
    {
        var ct = _cts.Token;

        // 1. Hardware Telemetry Loop
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 40 && _hardwareService == null && !ct.IsCancellationRequested; i++)
                await Task.Delay(100, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_hardwareService != null)
                    {
                        var snap = _hardwareService.Collect();
                        bool socketCountChanged;
                        lock (_cacheSync)
                        {
                            int prevSockets = _lastTelemetry?.Cpus?.Count ?? 0;
                            int nextSockets = snap.Cpus?.Count ?? 0;
                            socketCountChanged = prevSockets != nextSockets;

                            _lastTelemetry = snap;
                            _sparkHistory.Enqueue(new SparkHistoryPoint
                            {
                                Cpu = snap.Cpu.TotalUsagePercent,
                                Gpu = snap.Gpu.UsagePercent,
                                Ram = snap.Memory.UsagePercent,
                                NetDn = snap.Network.DownloadBytesPerSec,
                                NetUp = snap.Network.UploadBytesPerSec,
                                Cores = snap.Cpu.CoreUsages?.ToArray(),
                                SocketUsages = snap.Cpus?.Select(c => c.TotalUsagePercent).ToArray(),
                                SocketCores = snap.Cpus?.Select(c => c.CoreUsages?.ToArray() ?? Array.Empty<double>()).ToList()
                            });
                            while (_sparkHistory.Count > 60) _sparkHistory.Dequeue();
                        }
                        _displayWindow?.PostToWeb("telemetry", snap);
                        _trayManager?.UpdateLiveTooltip(snap);
                        if (socketCountChanged)
                        {
                            NotifyStateChanged();
                        }
                    }
                }
                catch { }

                int delay = Math.Clamp(Settings.TelemetryIntervalMs, 500, 5000);
                try { await Task.Delay(delay, ct); } catch { break; }
            }
        }, ct);

        // 2. Market Quotes Loop
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RefreshQuotesInternalAsync(Settings.ActiveCategory, forceAll: false, ct);
                }
                catch { }

                try { await Task.Delay(10000, ct); } catch { break; }
            }
        }, ct);

        // 3. Active K-Line Loop
        _ = Task.Run(async () =>
        {
            await PushActiveKlineAsync(forceRefresh: true, ct);
            while (!ct.IsCancellationRequested)
            {
                int delayMs = Settings.ActiveInterval == "1m" ? 6000 : 12000;
                try { await Task.Delay(delayMs, ct); } catch { break; }
                await PushActiveKlineAsync(forceRefresh: true, ct);
            }
        }, ct);

        // 4. FX Spot Rates Loop
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var fx = await _marketService.FetchFxSpotRatesAsync(ct);
                    lock (_cacheSync) { _lastFxRates = fx; }
                    _displayWindow?.PostToWeb("fxRates", fx);
                }
                catch { }

                try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch { break; }
            }
        }, ct);

        // 5. Domestic & Overseas IPv4 Loop
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RefreshNetworkIpInternalAsync(ct);
                }
                catch { }

                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { break; }
            }
        }, ct);

        // 6. Auto Update Check Loop (initial check 4s after launch, then every 6 hours)
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                if (Settings.AutoCheckUpdates)
                {
                    try
                    {
                        await CheckForUpdatesAsync(manual: false, showDialogIfAvailable: true, showUpToDateBalloon: false);
                    }
                    catch { }
                }

                try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch { break; }
            }
        }, ct);
    }

    private async Task RefreshNetworkIpInternalAsync(CancellationToken ct)
    {
        if (!await _ipDetectLock.WaitAsync(0, ct)) return;
        try
        {
            NetworkIpSnapshot? current;
            lock (_cacheSync) { current = _lastNetworkIp; }
            var checking = new NetworkIpSnapshot
            {
                Domestic = current?.Domestic ?? new IpEndpointInfo(),
                Overseas = current?.Overseas ?? new IpEndpointInfo(),
                UpdatedMs = current?.UpdatedMs ?? 0,
                IsChecking = true
            };
            lock (_cacheSync) { _lastNetworkIp = checking; }
            _displayWindow?.PostToWeb("networkIp", checking);
            NotifyStateChanged();

            var result = await _networkIpService.DetectAsync(ct);
            lock (_cacheSync) { _lastNetworkIp = result; }
            _displayWindow?.PostToWeb("networkIp", result);
            NotifyStateChanged();
        }
        catch { }
        finally
        {
            _ipDetectLock.Release();
        }
    }

    private void NetworkChange_NetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, _cts.Token);
                await RefreshNetworkIpInternalAsync(_cts.Token);
            }
            catch { }
        });
    }

    private async Task RefreshQuotesInternalAsync(string? priorityCategory, bool forceAll, CancellationToken ct)
    {
        var symbols = GetAllSymbols();
        var quotes = await _marketService.RefreshQuotesAsync(
            symbols,
            Settings.CryptoExchange,
            Settings.CryptoMarketType,
            priorityCategory,
            forceAll,
            ct);
        lock (_cacheSync) { _lastQuotes = quotes; }
        _displayWindow?.PostToWeb("quotes", quotes);
    }

    private async Task PushActiveKlineAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (!await _klineFetchLock.WaitAsync(0, ct)) return;
        try
        {
            var symbols = GetAllSymbols();
            var sym = symbols.FirstOrDefault(x => x.Id.Equals(Settings.ActiveSymbolId, StringComparison.OrdinalIgnoreCase))
                      ?? symbols[0];

            var payload = await _marketService.FetchKlineAsync(
                sym,
                Settings.ActiveInterval,
                Settings.CryptoExchange,
                Settings.CryptoMarketType,
                forceRefresh,
                ct);
            lock (_cacheSync) { _lastKline = payload; }
            _displayWindow?.PostToWeb("kline", payload);
        }
        catch { }
        finally
        {
            _klineFetchLock.Release();
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RefreshTrayMenu();
            NotifyStateChanged();
            if (_displayWindow != null)
            {
                _ = RecreateDisplayWindowAsync();
            }
        });
    }

    public void RefreshTrayMenu()
    {
        _trayManager?.Refresh();
    }

    public void NotifyHiddenToTrayOnce()
    {
        _trayManager?.NotifyHiddenToTrayOnce();
    }

    private void NotifyStateChanged()
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try { StateChanged?.Invoke(); } catch { }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
        try { _cts.Cancel(); } catch { }

        DestroyDisplayWindowInternal();
        try { _updateWindow?.Close(); } catch { }
        _updateWindow = null;
        try { _controlWindow?.ForceClose(); } catch { }
        _controlWindow = null;

        try { _hardwareService?.Dispose(); } catch { }
        try { _marketService.Dispose(); } catch { }
        try { _networkIpService.Dispose(); } catch { }
        try { _updateService.Dispose(); } catch { }
        try { _trayManager?.Dispose(); } catch { }
        _trayManager = null;
    }
}
