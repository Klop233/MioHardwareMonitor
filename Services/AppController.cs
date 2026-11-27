using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
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
/// Central application controller that owns hardware telemetry, latency probes, CSV recording,
/// market feeds, settings, profiles, system tray, the persistent ControlWindow, and the disposable DisplayWindow.
/// Switching monitors destroys and recreates ONLY DisplayWindow directly on the target GPU/monitor.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly SettingsService _settingsService = new();
    private readonly DisplayManager _displayManager = new();
    private readonly MarketDataService _marketService = new();
    private readonly NetworkIpService _networkIpService = new();
    private readonly NetworkPingService _pingService = new();
    private readonly TelemetryRecorderService _recorderService = new();
    private readonly ProcessTreeService _processTreeService = new();
    private readonly UpdateService _updateService = new();
    private readonly DonationService _donationService = new();

    private readonly object _hwInitLock = new();
    private HardwareMonitorService? _hardwareService;
    private readonly bool _isElevated = CheckIsElevated();
    private readonly long _controllerStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _klineFetchLock = new(1, 1);
    private readonly SemaphoreSlim _recreateLock = new(1, 1);
    private readonly SemaphoreSlim _ipDetectLock = new(1, 1);
    private readonly SemaphoreSlim _procTreeLock = new(1, 1);

    private readonly object _directTargetsLock = new();
    private List<DirectDisplayTargetDescriptor>? _cachedDirectTargets;
    private DateTime _directTargetsCachedUtc = DateTime.MinValue;

    private ControlWindow? _controlWindow;
    private volatile bool _isControlWindowVisible;
    private DisplayWindow? _displayWindow;
    private UpdateWindow? _updateWindow;
    private TrayManager? _trayManager;
    private string _dismissedUpdateTagThisSession = "";
    private volatile bool _disposed;

    // In-memory state buffers so newly created DisplayWindows hydrate immediately
    private readonly object _cacheSync = new();
    private readonly Queue<SparkHistoryPoint> _sparkHistory = new();
    private TelemetrySnapshot? _lastTelemetry;
    private ProcessTreeSnapshot? _lastProcessTree;
    private NetworkIpSnapshot? _lastNetworkIp;
    private NetworkLatencyMetrics? _lastLatency;
    private List<QuoteItem>? _lastQuotes;
    private KlinePayload? _lastKline;
    private FxRatesPayload? _lastFxRates;

    public AppSettings Settings => _settingsService.Current;
    public DisplayManager DisplayManager => _displayManager;
    public DonationService DonationService => _donationService;
    public bool IsDisplayOpen => _displayWindow != null;
    public bool IsDirectDisplayActive => _displayWindow?.IsDirectDisplayExclusive == true;
    public DirectDisplayTargetDescriptor? ActiveDirectDisplayTarget => _displayWindow?.DirectDisplayTarget;
    public string DirectDisplayErrorMessage { get; private set; } = "";
    public bool IsElevated => _isElevated;
    public bool IsAutoStartEnabled => AutoStartService.IsEnabled();
    public MonitorDescriptor? CurrentDisplayMonitor => _displayWindow?.CurrentMonitor;
    public MonitorDescriptor? TargetMonitor => _displayManager.ResolveTargetMonitor(Settings.TargetMonitorDevice, preferSecondary: true);
    public TelemetrySnapshot? LastTelemetry { get { lock (_cacheSync) return _lastTelemetry; } }
    public ProcessTreeSnapshot? LatestProcessTree { get { lock (_cacheSync) return _lastProcessTree; } }
    public NetworkIpSnapshot? LastNetworkIp { get { lock (_cacheSync) return _lastNetworkIp; } }
    public FxRatesPayload? LastFxRates { get { lock (_cacheSync) return _lastFxRates; } }
    public TelemetryRecordingStatus RecordingStatus => _recorderService.GetStatus();
    public DirectDisplayDiagnostics DirectDisplayDiagnostics =>
        _displayWindow?.GetDirectDisplayDiagnostics() ?? new DirectDisplayDiagnostics { IsActive = false };

    public SessionStatsSnapshot CurrentSessionStats
    {
        get
        {
            var hw = GetHardwareService();
            if (hw != null)
            {
                try { return hw.GetSessionStats(); } catch { }
            }
            lock (_cacheSync)
            {
                return _lastTelemetry?.SessionStats ?? new SessionStatsSnapshot
                {
                    SessionStartedMs = _controllerStartedMs
                };
            }
        }
    }

    public event Action? StateChanged;
    public event Action<UpdateStateSnapshot>? UpdateStateChanged;
    public event Action<NetworkIpSnapshot>? NetworkIpChanged;
    public event Action<TelemetrySnapshot>? TelemetryUpdated;
    public event Action<ProcessTreeSnapshot>? ProcessTreeUpdated;
    public event Action<TelemetryRecordingStatus>? RecordingStateChanged;

    private static bool CheckIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private HardwareMonitorService? GetHardwareService()
    {
        lock (_hwInitLock)
        {
            return _hardwareService;
        }
    }

    public UpdateStateSnapshot GetUpdateState() => _updateService.GetState();

    public void Start(bool startedFromAutoStart = false)
    {
        AutoStartService.SyncPathIfEnabled();

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

        // Initialize hardware sensors in background with disposal-race guard
        _ = Task.Run(() =>
        {
            HardwareMonitorService? created = null;
            try
            {
                created = new HardwareMonitorService
                {
                    HeavySampleIntervalMs = SettingsService.HeavySampleIntervalMs(Settings.PerformanceMode)
                };

                bool disposeImmediately = false;
                lock (_hwInitLock)
                {
                    if (_disposed)
                    {
                        disposeImmediately = true;
                    }
                    else
                    {
                        _hardwareService = created;
                    }
                }

                if (disposeImmediately)
                {
                    try { created.Dispose(); } catch { }
                }
            }
            catch
            {
                try { created?.Dispose(); } catch { }
            }
        });

        StartBackgroundLoops();

        if (startedFromAutoStart)
        {
            // On Windows boot auto-start: restore the exact previous monitor display mode quietly in background
            // (do not clutter the desktop with the management panel; user can open it anytime via system tray).
            _ = RestoreDisplayOnAutoStartAsync();
            return;
        }

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

    private async Task RestoreDisplayOnAutoStartAsync()
    {
        try
        {
            // Give Windows desktop / GPU drivers / secondary displays a moment to finish initializing after login
            await Task.Delay(1500, _cts.Token);

            if (Settings.UseDirectDisplayExclusive)
            {
                int[] retryDelaysMs = { 0, 2500, 4000, 6000 };
                for (int i = 0; i < retryDelaysMs.Length; i++)
                {
                    if (_disposed || _cts.IsCancellationRequested || !Settings.UseDirectDisplayExclusive)
                        return;

                    if (retryDelaysMs[i] > 0)
                    {
                        await Task.Delay(retryDelaysMs[i], _cts.Token);
                    }

                    await RecreateDisplayWindowAsync();
                    if (IsDirectDisplayActive)
                        return;
                }

                if (!_disposed && Settings.UseDirectDisplayExclusive && !IsDirectDisplayActive)
                {
                    _trayManager?.ShowBalloon(
                        "硬件独占副屏暂未就绪",
                        string.IsNullOrWhiteSpace(DirectDisplayErrorMessage)
                            ? "开机恢复硬件独占模式失败，请检查副屏连接或右键托盘重新启动。"
                            : DirectDisplayErrorMessage);
                }
                return;
            }

            // Desktop Fullscreen or Fixed Window mode: if the saved secondary monitor hasn't woken up yet, wait briefly before opening
            if (!string.IsNullOrWhiteSpace(Settings.TargetMonitorDevice))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var monitors = _displayManager.GetMonitors();
                    if (monitors.Any(m => string.Equals(m.DeviceName, Settings.TargetMonitorDevice, StringComparison.OrdinalIgnoreCase)))
                        break;
                    await Task.Delay(1500, _cts.Token);
                }
            }

            if (!_disposed && !_cts.IsCancellationRequested)
            {
                await RecreateDisplayWindowAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public (bool Success, string? ErrorMessage) SetAutoStart(bool enabled)
    {
        var result = AutoStartService.SetEnabled(enabled);
        RefreshTrayMenu();
        NotifyStateChanged();
        return result;
    }

    public string GetStartupRestoreSummary()
    {
        var s = Settings;
        if (s.UseDirectDisplayExclusive)
        {
            string targetName = ActiveDirectDisplayTarget?.DisplayName ?? "";
            if (string.IsNullOrWhiteSpace(targetName) && !string.IsNullOrWhiteSpace(s.DirectDisplayTargetId))
            {
                var found = GetCachedDirectDisplayTargets().FirstOrDefault(t =>
                    string.Equals(t.TargetKey, s.DirectDisplayTargetId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.StableMonitorId, s.DirectDisplayTargetId, StringComparison.OrdinalIgnoreCase));
                targetName = found?.DisplayName ?? "已脱离桌面的专用副屏";
            }
            return string.IsNullOrWhiteSpace(targetName)
                ? "开机自启将静默恢复：硬件独占显示器（Direct Display）"
                : $"开机自启将静默恢复：硬件独占显示器 · {targetName}";
        }

        var mon = CurrentDisplayMonitor ?? TargetMonitor;
        string monLabel = mon?.FriendlyName ?? (string.IsNullOrWhiteSpace(s.TargetMonitorDevice) ? "目标屏幕" : s.TargetMonitorDevice);
        if (s.IsFullScreen)
        {
            return $"开机自启将静默恢复：桌面无边框全屏 · {monLabel}";
        }

        return $"开机自启将静默恢复：固定窗口（{(int)s.WindowWidth}×{(int)s.WindowHeight}）· {monLabel}";
    }

    public List<DirectDisplayTargetDescriptor> GetCachedDirectDisplayTargets(bool forceRefresh = false)
    {
        lock (_directTargetsLock)
        {
            if (!forceRefresh &&
                _cachedDirectTargets != null &&
                (DateTime.UtcNow - _directTargetsCachedUtc).TotalSeconds < 30.0)
            {
                return _cachedDirectTargets.ToList();
            }
        }

        var fresh = DirectDisplayService.EnumerateTargets();
        lock (_directTargetsLock)
        {
            _cachedDirectTargets = fresh;
            _directTargetsCachedUtc = DateTime.UtcNow;
            return fresh.ToList();
        }
    }

    public void ShowControlWindow(string? initialTab = null)
    {
        InvokeOnUi(() =>
        {
            if (_disposed) return;
            if (_controlWindow == null)
            {
                _controlWindow = new ControlWindow(this);
                _controlWindow.IsVisibleChanged += (_, _) =>
                {
                    bool visible = _controlWindow?.IsVisible == true;
                    _isControlWindowVisible = visible;
                    if (visible && !_disposed &&
                        string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
                    {
                        TriggerProcessTreeRefresh(warmFollowUp: !_processTreeService.HasBaseline);
                    }
                };
                _controlWindow.Closed += (_, _) =>
                {
                    _isControlWindowVisible = false;
                    _controlWindow = null;
                };
            }
            _controlWindow.Show();
            if (_controlWindow.WindowState == WindowState.Minimized)
                _controlWindow.WindowState = WindowState.Normal;
            SyncControlWindowTopMost();
            _controlWindow.Activate();
            _controlWindow.RefreshFromController();
            if (!string.IsNullOrWhiteSpace(initialTab))
            {
                _controlWindow.SwitchToTab(initialTab);
            }
        });
    }

    public void SyncControlWindowTopMost()
    {
        PostToUi(() =>
        {
            if (_controlWindow == null || !_controlWindow.IsVisible) return;
            bool needTopMost = false;
            if (_displayWindow != null && !_displayWindow.IsDirectDisplayExclusive && Settings.AlwaysOnTop)
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
        InvokeOnUi(() =>
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
        if (_disposed) return;

        bool entered = false;
        try
        {
            await _recreateLock.WaitAsync(_cts.Token);
            entered = true;
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (_disposed) return;

            if (!string.IsNullOrWhiteSpace(overrideDeviceName))
            {
                Settings.UseDirectDisplayExclusive = false;
                Settings.TargetMonitorDevice = overrideDeviceName;
                _settingsService.Save();
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            bool hadExisting = false;
            await dispatcher.InvokeAsync(() =>
            {
                hadExisting = _displayWindow != null;
                DestroyDisplayWindowInternal();
            });

            // Brief pause ensures old WebView2 controller, HWND, or DirectDisplay target fully releases resources
            if (hadExisting)
            {
                await Task.Delay(160, _cts.Token);
            }

            if (_disposed) return;

            if (Settings.UseDirectDisplayExclusive)
            {
                DirectDisplaySession? directSession = null;
                string directErr = "";
                bool acquired = await Task.Run(() =>
                    DirectDisplayService.TryStartSession(Settings.DirectDisplayTargetId, out directSession, out directErr));

                if (!acquired || directSession == null)
                {
                    DirectDisplayErrorMessage = string.IsNullOrWhiteSpace(directErr)
                        ? "无法硬件独占目标显示器，请确认已在 Windows 设置中开启「从桌面删除显示器」。"
                        : directErr;
                    RefreshTrayMenu();
                    NotifyStateChanged();
                    return;
                }

                DirectDisplayErrorMessage = "";

                // Persist StableMonitorId when available (survives reboot/LUID reassignment), falling back to TargetKey.
                string persistId = !string.IsNullOrWhiteSpace(directSession.Descriptor.StableMonitorId)
                    ? directSession.Descriptor.StableMonitorId
                    : directSession.Descriptor.TargetKey;
                if (string.IsNullOrWhiteSpace(Settings.DirectDisplayTargetId) ||
                    string.Equals(Settings.DirectDisplayTargetId, directSession.Descriptor.TargetKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Settings.DirectDisplayTargetId, persistId, StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.DirectDisplayTargetId = persistId;
                        _settingsService.Save();
                    }
                }

                // Async UI dispatch prevents AB-BA deadlock between WinRT callback thread and UI Dispatcher.
                directSession.SessionInvalidated += reason =>
                {
                    DirectDisplayErrorMessage = reason;
                    PostToUi(() =>
                    {
                        if (_disposed) return;
                        CloseDisplayWindow();
                    });
                };

                var fallbackMon = _displayManager.ResolveTargetMonitor(Settings.TargetMonitorDevice, preferSecondary: true)
                                  ?? new MonitorDescriptor
                                  {
                                      DeviceName = directSession.Descriptor.TargetKey,
                                      FriendlyName = directSession.Descriptor.FriendlyName,
                                      Width = directSession.Width,
                                      Height = directSession.Height,
                                      WorkWidth = directSession.Width,
                                      WorkHeight = directSession.Height,
                                      RefreshRateHz = directSession.RefreshRateHz
                                  };

                await dispatcher.InvokeAsync(() =>
                {
                    if (_disposed)
                    {
                        directSession.Dispose();
                        return;
                    }
                    var win = new DisplayWindow(this, fallbackMon, directSession);
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
                return;
            }

            DirectDisplayErrorMessage = "";
            var target = _displayManager.ResolveTargetMonitor(Settings.TargetMonitorDevice, preferSecondary: true);
            if (target == null) return;

            if (string.IsNullOrWhiteSpace(Settings.TargetMonitorDevice))
            {
                Settings.TargetMonitorDevice = target.DeviceName;
                _settingsService.Save();
            }

            await dispatcher.InvokeAsync(() =>
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
        catch (OperationCanceledException) { }
        finally
        {
            if (entered)
            {
                try { _recreateLock.Release(); } catch (ObjectDisposedException) { }
            }
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

    public void SetDisplayMode(bool useDirectExclusive, bool isFullScreen)
    {
        bool modeTypeChanged = Settings.UseDirectDisplayExclusive != useDirectExclusive;
        Settings.UseDirectDisplayExclusive = useDirectExclusive;
        Settings.IsFullScreen = useDirectExclusive || isFullScreen;
        DirectDisplayErrorMessage = "";
        _settingsService.Save();

        if (modeTypeChanged || useDirectExclusive)
        {
            _ = RecreateDisplayWindowAsync();
            RefreshTrayMenu();
            NotifyStateChanged();
            return;
        }

        InvokeOnUi(() =>
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

    public void SetDirectDisplayTarget(string targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey)) return;
        string trimmed = targetKey.Trim();
        var matched = GetCachedDirectDisplayTargets().FirstOrDefault(t =>
            string.Equals(t.TargetKey, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.StableMonitorId, trimmed, StringComparison.OrdinalIgnoreCase));

        Settings.DirectDisplayTargetId = matched != null && !string.IsNullOrWhiteSpace(matched.StableMonitorId)
            ? matched.StableMonitorId
            : trimmed;
        DirectDisplayErrorMessage = "";
        _settingsService.Save();

        if (Settings.UseDirectDisplayExclusive)
        {
            _ = RecreateDisplayWindowAsync();
        }
        NotifyStateChanged();
    }

    public void SetDirectDisplayFps(int fps)
    {
        Settings.DirectDisplayFps = Math.Clamp(fps, 2, 30);
        _settingsService.Save();
        PostToUi(() =>
        {
            _displayWindow?.UpdateDirectDisplayFps(Settings.DirectDisplayFps);
        });
        NotifyStateChanged();
    }

    public void SetDirectDisplayRotation(int degrees)
    {
        Settings.DirectDisplayRotation = DirectDisplaySession.NormalizeRotation(degrees);
        _settingsService.Save();
        PostToUi(() =>
        {
            _displayWindow?.UpdateDirectDisplayRotation(Settings.DirectDisplayRotation);
        });
        NotifyStateChanged();
    }

    public void SetPinToAllVirtualDesktops(bool pinAllDesktops)
    {
        Settings.PinToAllVirtualDesktops = pinAllDesktops;
        _settingsService.Save();

        InvokeOnUi(() =>
        {
            _displayWindow?.ApplyPinToAllVirtualDesktops(pinAllDesktops);
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public static void OpenWindowsSpecializedDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:display-advanced",
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void SetFullScreen(bool isFullScreen)
    {
        SetDisplayMode(useDirectExclusive: false, isFullScreen: isFullScreen);
    }

    public void ToggleFullScreen()
    {
        if (Settings.UseDirectDisplayExclusive)
        {
            SetDisplayMode(useDirectExclusive: false, isFullScreen: false);
            return;
        }
        SetDisplayMode(useDirectExclusive: false, isFullScreen: !Settings.IsFullScreen);
    }

    public void SetWindowPositionPreset(string preset)
    {
        bool leavingDirect = Settings.UseDirectDisplayExclusive;
        Settings.UseDirectDisplayExclusive = false;
        Settings.WindowPositionPreset = SettingsService.NormalizePositionPreset(preset);
        Settings.IsFullScreen = false;

        var mon = CurrentDisplayMonitor ?? TargetMonitor;
        if (mon != null)
        {
            var rect = DisplayManager.CalculateFixedWindowRect(mon, Settings);
            SyncRectIntoSettings(mon, rect);
        }

        _settingsService.Save();

        if (leavingDirect || _displayWindow == null)
        {
            _ = RecreateDisplayWindowAsync();
            RefreshTrayMenu();
            NotifyStateChanged();
            return;
        }

        InvokeOnUi(() =>
        {
            _displayWindow?.ApplyDisplayModeAndGeometry();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetCustomWindowGeometry(int offsetX, int offsetY, int width, int height, int edgeMargin)
    {
        bool leavingDirect = Settings.UseDirectDisplayExclusive;
        Settings.UseDirectDisplayExclusive = false;
        Settings.IsFullScreen = false;
        Settings.WindowPositionPreset = "custom";
        Settings.WindowOffsetX = offsetX;
        Settings.WindowOffsetY = offsetY;
        Settings.WindowWidth = Math.Clamp(width, 280, 7680);
        Settings.WindowHeight = Math.Clamp(height, 180, 4320);
        Settings.WindowEdgeMargin = Math.Clamp(edgeMargin, 0, 200);
        Settings.HasWindowOffset = true;

        var mon = CurrentDisplayMonitor ?? TargetMonitor;
        if (mon != null)
        {
            var rect = DisplayManager.CalculateFixedWindowRect(mon, Settings);
            SyncRectIntoSettings(mon, rect);
        }

        _settingsService.Save();

        if (leavingDirect || _displayWindow == null)
        {
            _ = RecreateDisplayWindowAsync();
            RefreshTrayMenu();
            NotifyStateChanged();
            return;
        }

        InvokeOnUi(() =>
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

        InvokeOnUi(() =>
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

        InvokeOnUi(() =>
        {
            _displayWindow?.ApplyLockState(locked);
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetShowDisplayInTaskbar(bool showInTaskbar)
    {
        Settings.ShowDisplayInTaskbar = false;
        _settingsService.Save();

        InvokeOnUi(() =>
        {
            _displayWindow?.ApplyShowInTaskbar(false);
            NotifyStateChanged();
        });
    }

    public void CaptureCurrentDisplayGeometry()
    {
        InvokeOnUi(() =>
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

        InvokeOnUi(() =>
        {
            _displayWindow?.ApplyAlwaysOnTop(topMost);
            SyncControlWindowTopMost();
            RefreshTrayMenu();
            NotifyStateChanged();
        });
    }

    public void SetRenderMode(string renderMode)
    {
        string norm = SettingsService.NormalizeRenderMode(renderMode);
        if (string.Equals(Settings.RenderMode, norm, StringComparison.OrdinalIgnoreCase)) return;
        Settings.RenderMode = norm;
        _settingsService.Save();

        if (_displayWindow != null)
        {
            _ = RecreateDisplayWindowAsync();
        }
        NotifyStateChanged();
    }

    public void SetPerformanceMode(string mode)
    {
        string norm = SettingsService.NormalizePerformanceMode(mode);
        UpdateSettings(s =>
        {
            s.PerformanceMode = norm;
            s.TelemetryIntervalMs = SettingsService.RecommendedTelemetryIntervalMs(norm);
        });
    }

    public void ResetSessionStats()
    {
        var hw = GetHardwareService();
        hw?.ResetSessionStats();

        TelemetrySnapshot? updatedSnap = null;
        lock (_cacheSync)
        {
            if (_lastTelemetry != null)
            {
                _lastTelemetry.SessionStats = hw?.GetSessionStats() ?? new SessionStatsSnapshot
                {
                    SessionStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                updatedSnap = _lastTelemetry;
            }
        }

        if (updatedSnap != null)
        {
            _displayWindow?.PostToWeb("telemetry", updatedSnap);
            RaiseTelemetryUpdated(updatedSnap);
        }
        NotifyStateChanged();
    }

    public (bool Success, string? ErrorMessage) StartRecording()
    {
        var res = _recorderService.Start();
        var status = _recorderService.GetStatus();
        if (res.Success)
        {
            SyncRecordingStateIntoLastTelemetry(status);
        }
        RaiseRecordingStateChanged(status);
        RefreshTrayMenu();
        NotifyStateChanged();
        return res;
    }

    public void StopRecording()
    {
        _recorderService.Stop();
        var status = _recorderService.GetStatus();
        SyncRecordingStateIntoLastTelemetry(status);
        RaiseRecordingStateChanged(status);
        RefreshTrayMenu();
        NotifyStateChanged();
    }

    public (bool Success, string? ErrorMessage) ToggleRecording()
    {
        if (_recorderService.IsRecording)
        {
            StopRecording();
            return (true, null);
        }
        return StartRecording();
    }

    public void OpenLogsFolder()
    {
        try
        {
            string dir = TelemetryRecorderService.GetDefaultLogsDirectory();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public (bool Success, string? ErrorMessage) ExportProfile(string filePath, bool includeDisplayPlacement = false)
    {
        return ProfileService.Export(filePath, Settings, includeDisplayPlacement);
    }

    public (bool Success, string Summary, string? ErrorMessage) PreviewProfile(string filePath)
    {
        return ProfileService.Preview(filePath);
    }

    public (bool Success, string? ErrorMessage) ImportProfile(string filePath, bool applyDisplayPlacement = false)
    {
        bool prevDirect = Settings.UseDirectDisplayExclusive;
        string? prevTargetMon = Settings.TargetMonitorDevice;
        string? prevDirectTarget = Settings.DirectDisplayTargetId;
        string prevRender = Settings.RenderMode;
        string prevPingHost = Settings.PingTargetHost;
        string prevPage = Settings.MonitorPage;

        var res = ProfileService.Import(filePath, Settings, applyDisplayPlacement);
        if (!res.Success) return res;

        var hw = GetHardwareService();
        if (hw != null)
        {
            hw.HeavySampleIntervalMs = SettingsService.HeavySampleIntervalMs(Settings.PerformanceMode);
            hw.ResetAlertStates();
        }

        _settingsService.Save();

        if (!string.Equals(prevPingHost, Settings.PingTargetHost, StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => ProbePingOnceAsync(_cts.Token));
        }

        if (string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
        {
            TriggerProcessTreeRefresh(warmFollowUp: !_processTreeService.HasBaseline);
        }
        else if (string.Equals(prevPage, "proctree", StringComparison.OrdinalIgnoreCase))
        {
            _processTreeService.ResetBaseline();
            lock (_cacheSync) { _lastProcessTree = null; }
        }

        bool needRecreate = applyDisplayPlacement && (
            prevDirect != Settings.UseDirectDisplayExclusive ||
            !string.Equals(prevRender, Settings.RenderMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(prevTargetMon, Settings.TargetMonitorDevice, StringComparison.OrdinalIgnoreCase) ||
            (Settings.UseDirectDisplayExclusive && !string.Equals(prevDirectTarget, Settings.DirectDisplayTargetId, StringComparison.OrdinalIgnoreCase)));

        if (needRecreate && _displayWindow != null)
        {
            _ = RecreateDisplayWindowAsync();
        }
        else
        {
            PostToUi(() =>
            {
                var win = _displayWindow;
                if (win == null) return;
                win.ApplyThemeChrome(Settings);
                win.PostSettingsSync(Settings);
                if (applyDisplayPlacement)
                {
                    if (win.IsDirectDisplayExclusive)
                    {
                        win.UpdateDirectDisplayFps(Settings.DirectDisplayFps);
                        win.UpdateDirectDisplayRotation(Settings.DirectDisplayRotation);
                    }
                    else
                    {
                        win.ApplyDisplayModeAndGeometry();
                        win.ApplyLockState(Settings.LockWindowPosition);
                        win.ApplyAlwaysOnTop(Settings.AlwaysOnTop);
                        win.ApplyPinToAllVirtualDesktops(Settings.PinToAllVirtualDesktops);
                    }
                }
                SyncControlWindowTopMost();
            });
        }

        RefreshTrayMenu();
        NotifyStateChanged();
        return (true, null);
    }

    public void UpdateSettings(Action<AppSettings> mutator, bool pushVisualToDisplay = true)
    {
        string prevPingHost = Settings.PingTargetHost;
        string prevPage = Settings.MonitorPage;
        string prevTreeView = Settings.ProcessTreeViewMode;
        string prevTreeSort = Settings.ProcessTreeSortBy;
        bool prevTreeFollow = Settings.ProcessTreeAutoFollowTop;
        int prevTreePin = Settings.ProcessTreePinnedPid;
        string prevTreeFilter = Settings.ProcessTreeFilter;
        var prevAlerts = Settings.Alerts.Clone();

        mutator(Settings);
        SettingsService.Normalize(Settings);

        var hw = GetHardwareService();
        if (hw != null)
        {
            hw.HeavySampleIntervalMs = SettingsService.HeavySampleIntervalMs(Settings.PerformanceMode);
            if (!AlertsEqual(prevAlerts, Settings.Alerts))
            {
                hw.ResetAlertStates();
            }
        }

        _settingsService.Save();

        if (!string.Equals(prevPingHost, Settings.PingTargetHost, StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => ProbePingOnceAsync(_cts.Token));
        }

        bool isProcTree = string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase);
        bool wasProcTree = string.Equals(prevPage, "proctree", StringComparison.OrdinalIgnoreCase);
        bool treeOptsChanged =
            !string.Equals(prevTreeView, Settings.ProcessTreeViewMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(prevTreeSort, Settings.ProcessTreeSortBy, StringComparison.OrdinalIgnoreCase) ||
            prevTreeFollow != Settings.ProcessTreeAutoFollowTop ||
            prevTreePin != Settings.ProcessTreePinnedPid ||
            !string.Equals(prevTreeFilter, Settings.ProcessTreeFilter, StringComparison.Ordinal);

        if (isProcTree && (!wasProcTree || treeOptsChanged))
        {
            TriggerProcessTreeRefresh(warmFollowUp: !wasProcTree || !_processTreeService.HasBaseline);
        }
        else if (!isProcTree && wasProcTree)
        {
            _processTreeService.ResetBaseline();
            lock (_cacheSync) { _lastProcessTree = null; }
        }

        if (pushVisualToDisplay)
        {
            PostToUi(() =>
            {
                _displayWindow?.ApplyThemeChrome(Settings);
                _displayWindow?.PostSettingsSync(Settings);
            });
        }
        RefreshTrayMenu();
        NotifyStateChanged();
    }

    public void SetMonitorPage(string page)
    {
        string norm = SettingsService.NormalizeMonitorPage(page);
        bool wasProcTree = string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase);
        Settings.MonitorPage = norm;
        _settingsService.Save();

        PostToUi(() =>
        {
            _displayWindow?.PostSettingsSync(Settings);
        });
        RefreshTrayMenu();
        NotifyStateChanged();

        if (norm == "proctree")
        {
            TriggerProcessTreeRefresh(warmFollowUp: !wasProcTree || !_processTreeService.HasBaseline);
        }
        else
        {
            _processTreeService.ResetBaseline();
            lock (_cacheSync) { _lastProcessTree = null; }
        }
    }

    public void ToggleMonitorPage()
    {
        string next = string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase)
            ? "overview"
            : "proctree";
        SetMonitorPage(next);
    }

    public void SetProcessTreeOptions(
        string? viewMode = null,
        string? sortBy = null,
        bool? autoFollowTop = null,
        int? pinnedPid = null,
        string? filter = null)
    {
        int prevPinnedPid = Settings.ProcessTreePinnedPid;
        bool prevAutoFollow = Settings.ProcessTreeAutoFollowTop;

        if (viewMode != null)
            Settings.ProcessTreeViewMode = SettingsService.NormalizeProcessTreeViewMode(viewMode);
        if (sortBy != null)
            Settings.ProcessTreeSortBy = SettingsService.NormalizeProcessTreeSortBy(sortBy);
        if (pinnedPid.HasValue)
            Settings.ProcessTreePinnedPid = Math.Max(0, pinnedPid.Value);
        if (autoFollowTop.HasValue)
            Settings.ProcessTreeAutoFollowTop = autoFollowTop.Value;
        else if (pinnedPid.HasValue && pinnedPid.Value > 0)
            Settings.ProcessTreeAutoFollowTop = false;
        if (filter != null)
            Settings.ProcessTreeFilter = SettingsService.NormalizeProcessTreeFilter(filter);

        SettingsService.Normalize(Settings);
        _settingsService.Save();

        PostToUi(() =>
        {
            _displayWindow?.PostSettingsSync(Settings);
        });
        RefreshTrayMenu();
        NotifyStateChanged();

        if (string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
        {
            bool targetChanged = prevPinnedPid != Settings.ProcessTreePinnedPid || prevAutoFollow != Settings.ProcessTreeAutoFollowTop;
            TriggerProcessTreeRefresh(warmFollowUp: !_processTreeService.HasBaseline || targetChanged);
        }
    }

    public bool TryOpenProcessLocation(int pid, out string error)
    {
        return _processTreeService.TryOpenProcessLocation(pid, out error);
    }

    public bool TryTerminateProcess(int pid, out string error)
    {
        bool ok = _processTreeService.TryTerminateProcessSafe(pid, out error);
        if (ok && string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(160, _cts.Token);
                    await RefreshProcessTreeOnceAsync(_cts.Token);
                }
                catch { }
            });
        }
        return ok;
    }

    private static bool AlertsEqual(AlertSettings a, AlertSettings b) =>
        a.Enabled == b.Enabled &&
        a.TrayNotifyEnabled == b.TrayNotifyEnabled &&
        a.CooldownSeconds == b.CooldownSeconds &&
        a.CpuTempWarnC == b.CpuTempWarnC &&
        a.CpuTempCritC == b.CpuTempCritC &&
        a.GpuTempWarnC == b.GpuTempWarnC &&
        a.GpuTempCritC == b.GpuTempCritC &&
        a.RamUsageWarnPct == b.RamUsageWarnPct &&
        a.DiskUsageWarnPct == b.DiskUsageWarnPct;

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
        PostToUi(() =>
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
        ProcessTreeSnapshot? procTree;
        NetworkIpSnapshot? netIp;
        List<QuoteItem>? quotes;
        KlinePayload? kline;
        FxRatesPayload? fx;

        lock (_cacheSync)
        {
            historyCopy = _sparkHistory.ToList();
            snap = _lastTelemetry;
            procTree = _lastProcessTree;
            netIp = _lastNetworkIp;
            quotes = _lastQuotes;
            kline = _lastKline;
            fx = _lastFxRates;
        }

        bool isDirect = win.IsDirectDisplayExclusive;
        win.PostToWeb("init", new
        {
            settings = DisplayWindow.BuildSettingsPayload(Settings),
            monitors,
            isFullScreen = isDirect || Settings.IsFullScreen,
            isDirectExclusive = isDirect,
            alwaysOnTop = !isDirect && Settings.AlwaysOnTop,
            lockWindowPosition = isDirect || Settings.LockWindowPosition,
            windowPositionPreset = Settings.WindowPositionPreset,
            targetMonitorDevice = win.CurrentMonitor.DeviceName,
            symbols,
            history = historyCopy
        });

        if (snap != null) win.PostToWeb("telemetry", snap);
        if (netIp != null) win.PostToWeb("networkIp", netIp);
        if (string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
        {
            if (procTree != null)
            {
                win.PostToWeb("proctree", procTree);
            }
            TriggerProcessTreeRefresh(warmFollowUp: !_processTreeService.HasBaseline);
        }
        if (AppFeatures.MarketEnabled)
        {
            if (quotes != null) win.PostToWeb("quotes", quotes);
            if (kline != null) win.PostToWeb("kline", kline);
            if (fx != null) win.PostToWeb("fxRates", fx);
        }
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
        InvokeOnUi(() =>
        {
            Dispose();
            System.Windows.Application.Current?.Shutdown();
        });
    }

    private void StartBackgroundLoops()
    {
        var ct = _cts.Token;

        // 1. Hardware Telemetry Loop (cadence-compensated, attaches ping latency, evaluates alerts, records CSV)
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 40 && GetHardwareService() == null && !ct.IsCancellationRequested; i++)
            {
                try { await Task.Delay(100, ct); } catch { return; }
            }

            while (!ct.IsCancellationRequested)
            {
                long tickStart = Stopwatch.GetTimestamp();
                try
                {
                    var hw = GetHardwareService();
                    if (hw != null && !_disposed)
                    {
                        var snap = hw.Collect();

                        // Attach latest ICMP latency metrics
                        AttachLatestLatency(snap);

                        // Evaluate thermal & load alerts (2-tick debounce, hysteresis, tray cooldown)
                        hw.EvaluateAlerts(snap, Settings.Alerts, out var trayAlerts);
                        if (trayAlerts.Count == 1)
                        {
                            _trayManager?.ShowBalloon(trayAlerts[0].Title, trayAlerts[0].Message);
                        }
                        else if (trayAlerts.Count > 1)
                        {
                            _trayManager?.ShowBalloon(
                                "星澪硬件监控告警",
                                string.Join("；", trayAlerts.Select(a => a.Message)));
                        }

                        // Feed CSV telemetry recorder & surface live recording state on snapshot
                        bool wasRecording = _recorderService.IsRecording;
                        if (wasRecording)
                        {
                            _recorderService.Record(snap);
                        }
                        var recStatus = _recorderService.GetStatus();
                        snap.IsRecording = recStatus.IsRecording;
                        snap.RecordingDurationSeconds = recStatus.DurationSeconds;
                        if (wasRecording && !recStatus.IsRecording)
                        {
                            RaiseRecordingStateChanged(recStatus);
                            RefreshTrayMenu();
                            NotifyStateChanged();
                        }

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
                        RaiseTelemetryUpdated(snap);
                        if (socketCountChanged)
                        {
                            NotifyStateChanged();
                        }
                    }

                    // Collect Process & Thread Tree ONLY when MonitorPage == "proctree" AND at least one UI surface is active
                    if (!_disposed &&
                        (_displayWindow != null || _isControlWindowVisible) &&
                        string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
                    {
                        await RefreshProcessTreeOnceAsync(ct);
                    }
                }
                catch { }

                int intervalMs = Math.Clamp(Settings.TelemetryIntervalMs, 500, 5000);
                int elapsedMs = (int)((Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency);
                int delayMs = Math.Max(50, intervalMs - elapsedMs);
                try { await Task.Delay(delayMs, ct); } catch { break; }
            }
        }, ct);

        // 2. Network Latency (ICMP Ping) Loop (3s normal, 5s Eco mode)
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await ProbePingOnceAsync(ct);

                int pingDelayMs = string.Equals(SettingsService.NormalizePerformanceMode(Settings.PerformanceMode), "eco", StringComparison.OrdinalIgnoreCase)
                    ? 5000
                    : 3000;
                try { await Task.Delay(pingDelayMs, ct); } catch { break; }
            }
        }, ct);

        // 3. Market Quotes / K-Line / FX Loops (skipped entirely when the market surface is feature-gated off)
        if (AppFeatures.MarketEnabled)
        {
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
        }

        // 4. Domestic & Overseas IPv4 Loop (10-minute cadence + instant trigger on NetworkAddressChanged)
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RefreshNetworkIpInternalAsync(ct);
                }
                catch { }

                try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch { break; }
            }
        }, ct);

        // 5. Auto Update Check Loop (initial check 4s after launch, then every 6 hours)
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

    private async Task ProbePingOnceAsync(CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested) return;
        try
        {
            string target = SettingsService.NormalizePingTarget(Settings.PingTargetHost);
            var lat = await _pingService.ProbeAsync(target, ct);
            lock (_cacheSync)
            {
                _lastLatency = lat;
            }
        }
        catch { }
    }

    private void AttachLatestLatency(TelemetrySnapshot snap)
    {
        string target = SettingsService.NormalizePingTarget(Settings.PingTargetHost);
        NetworkLatencyMetrics? latest;
        lock (_cacheSync)
        {
            latest = _lastLatency;
        }

        if (latest != null && string.Equals(latest.Target, target, StringComparison.OrdinalIgnoreCase))
        {
            snap.Network.Latency = new NetworkLatencyMetrics
            {
                Target = latest.Target,
                PingMs = latest.PingMs,
                JitterMs = latest.JitterMs,
                PacketLossPercent = latest.PacketLossPercent,
                Ok = latest.Ok
            };
        }
        else
        {
            snap.Network.Latency = _pingService.GetSnapshot(target);
        }
    }

    private void SyncRecordingStateIntoLastTelemetry(TelemetryRecordingStatus status)
    {
        TelemetrySnapshot? snap = null;
        lock (_cacheSync)
        {
            if (_lastTelemetry != null)
            {
                _lastTelemetry.IsRecording = status.IsRecording;
                _lastTelemetry.RecordingDurationSeconds = status.DurationSeconds;
                snap = _lastTelemetry;
            }
        }
        if (snap != null)
        {
            _displayWindow?.PostToWeb("telemetry", snap);
        }
    }

    private async Task RefreshNetworkIpInternalAsync(CancellationToken ct)
    {
        if (_disposed) return;

        bool entered = false;
        try
        {
            entered = await _ipDetectLock.WaitAsync(0, ct);
        }
        catch { return; }

        if (!entered) return;
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
            RaiseNetworkIpChanged(checking);

            var result = await _networkIpService.DetectAsync(ct);
            lock (_cacheSync) { _lastNetworkIp = result; }
            _displayWindow?.PostToWeb("networkIp", result);
            RaiseNetworkIpChanged(result);
        }
        catch { }
        finally
        {
            try { _ipDetectLock.Release(); } catch (ObjectDisposedException) { }
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
        if (_disposed) return;

        bool entered = false;
        try
        {
            entered = await _klineFetchLock.WaitAsync(0, ct);
        }
        catch { return; }

        if (!entered) return;
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
            try { _klineFetchLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_directTargetsLock)
        {
            _cachedDirectTargets = null;
            _directTargetsCachedUtc = DateTime.MinValue;
        }

        PostToUi(() =>
        {
            RefreshTrayMenu();
            NotifyStateChanged();
            if (_displayWindow != null && !_displayWindow.IsDirectDisplayExclusive)
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
        var handler = StateChanged;
        if (handler == null || _disposed) return;
        PostToUi(() =>
        {
            try { handler.Invoke(); } catch { }
        });
    }

    private void RaiseNetworkIpChanged(NetworkIpSnapshot snap)
    {
        var handler = NetworkIpChanged;
        if (handler == null || _disposed) return;
        PostToUi(() =>
        {
            try { handler.Invoke(snap); } catch { }
        });
    }

    private void TriggerProcessTreeRefresh(bool warmFollowUp)
    {
        if (_disposed) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshProcessTreeOnceAsync(_cts.Token);
                if (warmFollowUp && !_disposed && !_cts.IsCancellationRequested &&
                    string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(450, _cts.Token);
                    if (!_disposed && string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase))
                    {
                        await RefreshProcessTreeOnceAsync(_cts.Token);
                    }
                }
            }
            catch { }
        });
    }

    private async Task RefreshProcessTreeOnceAsync(CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested) return;
        if (!string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase)) return;

        bool entered = false;
        try
        {
            entered = await _procTreeLock.WaitAsync(0, ct);
        }
        catch { return; }

        if (!entered) return;
        try
        {
            if (_disposed || ct.IsCancellationRequested) return;
            if (!string.Equals(Settings.MonitorPage, "proctree", StringComparison.OrdinalIgnoreCase)) return;

            var snap = _processTreeService.Collect(
                Settings.ProcessTreeViewMode,
                Settings.ProcessTreeSortBy,
                Settings.ProcessTreeAutoFollowTop,
                Settings.ProcessTreePinnedPid,
                Settings.ProcessTreeFilter);

            lock (_cacheSync)
            {
                _lastProcessTree = snap;
            }
            _displayWindow?.PostToWeb("proctree", snap);
            RaiseProcessTreeUpdated(snap);
        }
        catch { }
        finally
        {
            try { _procTreeLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void RaiseTelemetryUpdated(TelemetrySnapshot snap)
    {
        var handler = TelemetryUpdated;
        if (handler == null || _disposed) return;
        PostToUi(() =>
        {
            try { handler.Invoke(snap); } catch { }
        });
    }

    private void RaiseProcessTreeUpdated(ProcessTreeSnapshot snap)
    {
        var handler = ProcessTreeUpdated;
        if (handler == null || _disposed) return;
        PostToUi(() =>
        {
            try { handler.Invoke(snap); } catch { }
        });
    }

    private void RaiseRecordingStateChanged(TelemetryRecordingStatus status)
    {
        var handler = RecordingStateChanged;
        if (handler == null || _disposed) return;
        PostToUi(() =>
        {
            try { handler.Invoke(status); } catch { }
        });
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        dispatcher.BeginInvoke(action);
    }

    private static void InvokeOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        HardwareMonitorService? hwToDispose;
        lock (_hwInitLock)
        {
            if (_disposed) return;
            _disposed = true;
            hwToDispose = _hardwareService;
            _hardwareService = null;
        }

        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
        try { _cts.Cancel(); } catch { }

        DestroyDisplayWindowInternal();
        try { _updateWindow?.Close(); } catch { }
        _updateWindow = null;
        try { _controlWindow?.ForceClose(); } catch { }
        _isControlWindowVisible = false;
        _controlWindow = null;

        try { hwToDispose?.Dispose(); } catch { }
        try { _processTreeService.Dispose(); } catch { }
        try { _recorderService.Dispose(); } catch { }
        try { _pingService.Dispose(); } catch { }
        try { _marketService.Dispose(); } catch { }
        try { _networkIpService.Dispose(); } catch { }
        try { _updateService.Dispose(); } catch { }
        try { _donationService.Dispose(); } catch { }
        try { _trayManager?.Dispose(); } catch { }
        _trayManager = null;

        try { _cts.Dispose(); } catch { }
        try { _klineFetchLock.Dispose(); } catch { }
        try { _recreateLock.Dispose(); } catch { }
        try { _ipDetectLock.Dispose(); } catch { }
        try { _procTreeLock.Dispose(); } catch { }
    }
}
