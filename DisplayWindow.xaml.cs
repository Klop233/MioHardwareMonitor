using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SideScreenHUD.Models;
using SideScreenHUD.Native;
using SideScreenHUD.Services;

namespace SideScreenHUD;

public partial class DisplayWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly SolidColorBrush LightPageBrush = new(System.Windows.Media.Color.FromRgb(0xF7, 0xF8, 0xFA));
    private static readonly SolidColorBrush DarkPageBrush = new(System.Windows.Media.Color.FromRgb(0x0F, 0x11, 0x15));
    private static readonly SolidColorBrush LightLockedBorderBrush = new(System.Windows.Media.Color.FromRgb(0xE4, 0xE7, 0xEC));
    private static readonly SolidColorBrush DarkLockedBorderBrush = new(System.Windows.Media.Color.FromRgb(0x2A, 0x2F, 0x3A));
    private static readonly SolidColorBrush LightAccentBrush = new(System.Windows.Media.Color.FromRgb(0x2B, 0x5F, 0xD9));
    private static readonly SolidColorBrush DarkAccentBrush = new(System.Windows.Media.Color.FromRgb(0x3D, 0x6F, 0xE0));
    private static readonly SolidColorBrush LightMutedBrush = new(System.Windows.Media.Color.FromRgb(0x5B, 0x64, 0x72));
    private static readonly SolidColorBrush DarkMutedBrush = new(System.Windows.Media.Color.FromRgb(0xA1, 0xA9, 0xB6));

    private readonly AppController _controller;
    private readonly DirectDisplaySession? _directSession;
    private readonly bool _isDirectDisplayMode;
    private HwndSource? _hwndSource;
    private DispatcherTimer? _topMostWatchdog;
    private DispatcherTimer? _directFrameTimer;
    private Bitmap? _directCaptureBitmap;
    private bool _directPresentBusy;
    private bool _webReady;
    private bool _isFullScreen;
    private bool _isLocked;
    private bool _pinAllDesktops;
    private bool _isDisposing;
    private bool _applyingGeometry;
    private bool _geometryAppliedOnce;

    private Win32Interop.RECT _targetPhysicalRect;

    public MonitorDescriptor CurrentMonitor { get; private set; }
    public bool IsDirectDisplayExclusive => _isDirectDisplayMode;
    public DirectDisplayTargetDescriptor? DirectDisplayTarget => _directSession?.Descriptor;

    public DisplayWindow(AppController controller, MonitorDescriptor targetMonitor, DirectDisplaySession? directSession = null)
    {
        _controller = controller;
        CurrentMonitor = targetMonitor;
        _directSession = directSession;
        _isDirectDisplayMode = directSession != null;

        InitializeComponent();

        var s = _controller.Settings;
        _isFullScreen = _isDirectDisplayMode || s.IsFullScreen;
        _isLocked = _isDirectDisplayMode || s.LockWindowPosition;
        _pinAllDesktops = s.PinToAllVirtualDesktops;
        Topmost = !_isDirectDisplayMode && s.AlwaysOnTop;
        ShowInTaskbar = !_isDirectDisplayMode && !_pinAllDesktops && s.ShowDisplayInTaskbar;

        LoadingText.Text = _isDirectDisplayMode
            ? $"正在硬件独占屏幕 {directSession!.Descriptor.DisplayName} ({directSession.Width}×{directSession.Height}) 上初始化..."
            : $"正在 {targetMonitor.FriendlyName} 上初始化监控窗口...";
        ApplyThemeChrome(s.Theme);

        // Initialize relative window offset on first run for backward compatibility
        if (!_isDirectDisplayMode && !s.HasWindowOffset)
        {
            var initialRect = DisplayManager.CalculateFixedWindowRect(targetMonitor, s);
            s.WindowOffsetX = initialRect.Left - targetMonitor.WorkLeft;
            s.WindowOffsetY = initialRect.Top - targetMonitor.WorkTop;
            s.WindowLeft = initialRect.Left;
            s.WindowTop = initialRect.Top;
            s.WindowWidth = initialRect.Width;
            s.WindowHeight = initialRect.Height;
            s.HasWindowOffset = true;
            _controller.SaveSettingsOnly();
        }

        _targetPhysicalRect = _isDirectDisplayMode
            ? new Win32Interop.RECT { Left = -16000, Top = -16000, Right = -16000 + directSession!.Width, Bottom = -16000 + directSession.Height }
            : ComputeTargetRect(targetMonitor);

        // Place HWND directly on the target monitor (or off-screen for Direct Display scanout) BEFORE WebView2 init!
        SourceInitialized += DisplayWindow_SourceInitialized;
        Loaded += DisplayWindow_Loaded;
        PreviewKeyDown += DisplayWindow_PreviewKeyDown;
        Closing += DisplayWindow_Closing;
    }

    private Win32Interop.RECT ComputeTargetRect(MonitorDescriptor mon)
    {
        return _isFullScreen
            ? DisplayManager.CalculateFullScreenRect(mon)
            : DisplayManager.CalculateFixedWindowRect(mon, _controller.Settings);
    }

    private void DisplayWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            if (_isDirectDisplayMode)
            {
                Win32Interop.ApplyVirtualDesktopPinStyle(hwnd, pinAcrossDesktops: true, showInTaskbar: false, noActivate: true);
            }
            else
            {
                Win32Interop.ApplyVirtualDesktopPinStyle(hwnd, _pinAllDesktops, _controller.Settings.ShowDisplayInTaskbar, noActivate: false);
            }
        }

        ApplyGeometryForCurrentMode(isInitialPlacement: true);
        if (!_isDirectDisplayMode)
        {
            StartTopMostWatchdog();
        }
    }

    private async void DisplayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-confirm exact physical bounds after WPF finishes DPI realization on target monitor
        ApplyGeometryForCurrentMode(isInitialPlacement: false);
        await InitializeWebViewAsync();
        if (_isDirectDisplayMode && !_isDisposing)
        {
            StartDirectDisplayFramePump();
        }
    }

    private void StartTopMostWatchdog()
    {
        _topMostWatchdog?.Stop();
        _topMostWatchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _topMostWatchdog.Tick += (_, _) => EnforceTopMostIfNeeded();
        _topMostWatchdog.Start();
    }

    private void EnforceTopMostIfNeeded()
    {
        if (_isDisposing || _isDirectDisplayMode || !Topmost || !_geometryAppliedOnce) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        bool wasMinimized = Win32Interop.IsIconic(hwnd);
        if (wasMinimized)
        {
            Win32Interop.ShowWindow(hwnd, Win32Interop.SW_SHOWNOACTIVATE);
        }

        if (wasMinimized || Win32Interop.IsCoveredByDesktopShellOrDemoted(hwnd))
        {
            _applyingGeometry = true;
            try
            {
                Win32Interop.SetWindowPos(
                    hwnd,
                    Win32Interop.HWND_TOPMOST,
                    _targetPhysicalRect.Left,
                    _targetPhysicalRect.Top,
                    _targetPhysicalRect.Width,
                    _targetPhysicalRect.Height,
                    Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
            }
            finally
            {
                _applyingGeometry = false;
            }
        }
    }

    private void StartDirectDisplayFramePump()
    {
        if (_directSession == null) return;
        _directFrameTimer?.Stop();

        int fps = Math.Clamp(_controller.Settings.DirectDisplayFps, 2, 30);
        _directCaptureBitmap?.Dispose();
        _directCaptureBitmap = new Bitmap(_directSession.Width, _directSession.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        _directFrameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(33, 1000 / fps))
        };
        _directFrameTimer.Tick += (_, _) => PumpOneDirectDisplayFrame();
        _directFrameTimer.Start();
    }

    public void UpdateDirectDisplayFps(int fps)
    {
        if (_directFrameTimer == null) return;
        int clamped = Math.Clamp(fps, 2, 30);
        _directFrameTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(33, 1000 / clamped));
    }

    private void PumpOneDirectDisplayFrame()
    {
        if (_isDisposing || _directPresentBusy || _directSession == null || _directCaptureBitmap == null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _directPresentBusy = true;
        try
        {
            using (var g = Graphics.FromImage(_directCaptureBitmap))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    Win32Interop.PrintWindow(hwnd, hdc, Win32Interop.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            var lockRect = new Rectangle(0, 0, _directCaptureBitmap.Width, _directCaptureBitmap.Height);
            var bits = _directCaptureBitmap.LockBits(lockRect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                _directSession.PresentBgraFrame(bits.Scan0, bits.Stride, bits.Width, bits.Height);
            }
            finally
            {
                _directCaptureBitmap.UnlockBits(bits);
            }
        }
        catch { }
        finally
        {
            _directPresentBusy = false;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string mode = _isDirectDisplayMode ? "safe" : (_controller.Settings.RenderMode ?? "safe").ToLowerInvariant();
            string browserArgs = mode switch
            {
                "gpu" => "",
                "nodcomp" => "--disable-direct-composition",
                _ => "--disable-gpu-compositing --disable-direct-composition"
            };

            if (_isDirectDisplayMode)
            {
                browserArgs = (browserArgs + " --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows").Trim();
            }

            string folderSuffix = _isDirectDisplayMode
                ? "WebView2_direct"
                : mode switch
                {
                    "gpu" => "WebView2_gpu",
                    "nodcomp" => "WebView2_nodcomp",
                    _ => "WebView2_safe"
                };

            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideScreenHUD",
                folderSuffix);
            Directory.CreateDirectory(userDataDir);

            var options = string.IsNullOrEmpty(browserArgs)
                ? null
                : new CoreWebView2EnvironmentOptions(browserArgs);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
            if (_isDisposing) return;

            await WebView.EnsureCoreWebView2Async(env);
            if (_isDisposing || WebView.CoreWebView2 == null) return;

            var core = WebView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            var webDir = Path.Combine(AppContext.BaseDirectory, "web");
            core.SetVirtualHostNameToFolderMapping(
                "hud.local",
                webDir,
                CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += Core_WebMessageReceived;
            core.NavigationCompleted += (_, _) =>
            {
                if (_isDisposing) return;
                _webReady = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _controller.HydrateDisplayWindow(this);
            };

            core.Navigate("https://hud.local/index.html");
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                LoadingText.Text = $"WebView2 初始化失败：{ex.Message}";
            }
        }
    }

    public void ApplyDisplayModeAndGeometry()
    {
        var s = _controller.Settings;
        bool enteringFullScreen = s.IsFullScreen && !_isFullScreen;

        // If transitioning from unlocked windowed mode to fullscreen, save latest rect first
        if (enteringFullScreen && !_isLocked)
        {
            CaptureCurrentGeometryInternal(notifyController: false, markCustomPreset: false);
        }

        _isFullScreen = s.IsFullScreen;
        _isLocked = s.LockWindowPosition;
        ApplyGeometryForCurrentMode(isInitialPlacement: false);
        PostWindowState();
    }

    public void ApplyLockState(bool locked)
    {
        if (locked && !_isFullScreen && !_isLocked)
        {
            // Persist exact position right before locking so it stays anchored where placed
            CaptureCurrentGeometryInternal(notifyController: false, markCustomPreset: false);
        }

        _isLocked = locked;
        ApplyGeometryForCurrentMode(isInitialPlacement: false);
        PostWindowState();
    }

    public void ApplyShowInTaskbar(bool showInTaskbar)
    {
        if (_isDirectDisplayMode) return;
        ShowInTaskbar = !_pinAllDesktops && showInTaskbar;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.ApplyVirtualDesktopPinStyle(hwnd, _pinAllDesktops, showInTaskbar, noActivate: false);
        }
    }

    public void ApplyPinToAllVirtualDesktops(bool pinAllDesktops)
    {
        if (_isDirectDisplayMode) return;
        _pinAllDesktops = pinAllDesktops;
        ShowInTaskbar = !pinAllDesktops && _controller.Settings.ShowDisplayInTaskbar;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.ApplyVirtualDesktopPinStyle(hwnd, pinAllDesktops, _controller.Settings.ShowDisplayInTaskbar, noActivate: false);
            EnforceTopMostIfNeeded();
        }
    }

    public void ApplyThemeChrome(string? theme)
    {
        bool isDark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        var bgBrush = isDark ? DarkPageBrush : LightPageBrush;
        Background = bgBrush;
        RootBorder.Background = bgBrush;
        LoadingOverlay.Background = bgBrush;
        LoadingTitle.Foreground = isDark ? DarkAccentBrush : LightAccentBrush;
        LoadingText.Foreground = isDark ? DarkMutedBrush : LightMutedBrush;
        RootBorder.BorderBrush = _isLocked
            ? (isDark ? DarkLockedBorderBrush : LightLockedBorderBrush)
            : (isDark ? DarkAccentBrush : LightAccentBrush);

        try
        {
            WebView.DefaultBackgroundColor = isDark
                ? System.Drawing.Color.FromArgb(255, 0x0F, 0x11, 0x15)
                : System.Drawing.Color.FromArgb(255, 0xF7, 0xF8, 0xFA);
        }
        catch { }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: !_isFullScreen);
        }
    }

    public void ApplyAlwaysOnTop(bool topMost)
    {
        Topmost = topMost;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && Win32Interop.GetWindowRect(hwnd, out var rect))
        {
            _applyingGeometry = true;
            try
            {
                SetWindowPhysicalRect(hwnd, rect.Left, rect.Top, rect.Width, rect.Height, topMost);
            }
            finally
            {
                _applyingGeometry = false;
            }
        }
        PostWindowState();
    }

    public void CaptureCurrentGeometryNow(bool markCustomPreset)
    {
        CaptureCurrentGeometryInternal(notifyController: true, markCustomPreset: markCustomPreset);
    }

    public void PostSettingsSync(AppSettings settings)
    {
        PostToWeb("settingsUpdated", settings);
    }

    public void PostWindowState()
    {
        var monitors = _controller.DisplayManager.GetMonitors();
        PostToWeb("windowState", new
        {
            isFullScreen = _isFullScreen,
            isDirectExclusive = _isDirectDisplayMode,
            alwaysOnTop = Topmost,
            lockWindowPosition = _isLocked,
            windowPositionPreset = _controller.Settings.WindowPositionPreset,
            targetMonitorDevice = CurrentMonitor.DeviceName,
            monitors
        });
    }

    public void PostToWeb(string type, object data)
    {
        if (!_webReady || _isDisposing) return;
        try
        {
            string json = JsonSerializer.Serialize(new { type, data }, JsonOptions);
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_webReady && !_isDisposing && WebView.CoreWebView2 != null)
                    {
                        WebView.CoreWebView2.PostWebMessageAsJson(json);
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    public void ForceCloseAndDispose()
    {
        if (!_isFullScreen && !_isLocked && !_isDisposing)
        {
            CaptureCurrentGeometryInternal(notifyController: false, markCustomPreset: false);
        }

        _isDisposing = true;
        _webReady = false;
        StopTimersAndDirectResources();
        DetachWndProc();
        try
        {
            if (WebView != null)
            {
                WebView.Dispose();
            }
        }
        catch { }
        try
        {
            Close();
        }
        catch { }
    }

    private void StopTimersAndDirectResources()
    {
        try { _topMostWatchdog?.Stop(); _topMostWatchdog = null; } catch { }
        try { _directFrameTimer?.Stop(); _directFrameTimer = null; } catch { }
        try { _directCaptureBitmap?.Dispose(); _directCaptureBitmap = null; } catch { }
        try { _directSession?.Dispose(); } catch { }
    }

    private void DetachWndProc()
    {
        try
        {
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }
        catch { }
    }

    private void ApplyGeometryForCurrentMode(bool isInitialPlacement)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (_isDirectDisplayMode && _directSession != null)
        {
            _targetPhysicalRect = new Win32Interop.RECT
            {
                Left = -16000,
                Top = -16000,
                Right = -16000 + _directSession.Width,
                Bottom = -16000 + _directSession.Height
            };
        }
        else
        {
            // Refresh CurrentMonitor geometry in case taskbar/resolution changed
            var latestMon = _controller.DisplayManager.ResolveTargetMonitor(CurrentMonitor.DeviceName, preferSecondary: false);
            if (latestMon != null) CurrentMonitor = latestMon;
            _targetPhysicalRect = ComputeTargetRect(CurrentMonitor);
        }

        bool isDark = string.Equals(_controller.Settings.Theme, "dark", StringComparison.OrdinalIgnoreCase);

        _applyingGeometry = true;
        try
        {
            if (_isFullScreen)
            {
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.NoResize;
                Chrome.ResizeBorderThickness = new Thickness(0);
                RootBorder.Padding = new Thickness(0);
                RootBorder.BorderThickness = new Thickness(0);
                Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: false);
            }
            else if (_isLocked)
            {
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.NoResize;
                Chrome.ResizeBorderThickness = new Thickness(0);
                RootBorder.Padding = new Thickness(0);
                RootBorder.BorderThickness = new Thickness(1);
                RootBorder.BorderBrush = isDark ? DarkLockedBorderBrush : LightLockedBorderBrush;
                Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
            }
            else
            {
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.CanResize;
                Chrome.ResizeBorderThickness = new Thickness(6);
                RootBorder.Padding = new Thickness(4);
                RootBorder.BorderThickness = new Thickness(1);
                RootBorder.BorderBrush = isDark ? DarkAccentBrush : LightAccentBrush;
                Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
            }

            SetWindowPhysicalRect(
                hwnd,
                _targetPhysicalRect.Left,
                _targetPhysicalRect.Top,
                _targetPhysicalRect.Width,
                _targetPhysicalRect.Height,
                Topmost);
            _geometryAppliedOnce = true;
        }
        finally
        {
            _applyingGeometry = false;
        }

        if (!isInitialPlacement)
        {
            var expected = _targetPhysicalRect;
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(45);
                if (_isDisposing) return;
                if (expected.Left == _targetPhysicalRect.Left &&
                    expected.Top == _targetPhysicalRect.Top &&
                    expected.Width == _targetPhysicalRect.Width &&
                    expected.Height == _targetPhysicalRect.Height)
                {
                    ReapplyTargetPhysicalRect();
                }
            });
        }
    }

    private void ReapplyTargetPhysicalRect()
    {
        if (_isDisposing || !_geometryAppliedOnce) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || Win32Interop.IsIconic(hwnd)) return;

        _applyingGeometry = true;
        try
        {
            SetWindowPhysicalRect(
                hwnd,
                _targetPhysicalRect.Left,
                _targetPhysicalRect.Top,
                _targetPhysicalRect.Width,
                _targetPhysicalRect.Height,
                Topmost);
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_isDisposing) return IntPtr.Zero;

        switch (msg)
        {
            case Win32Interop.WM_SYSCOMMAND:
                {
                    int cmd = wParam.ToInt32() & 0xFFF0;
                    if ((_isFullScreen || _isLocked) && (cmd == Win32Interop.SC_MOVE || cmd == Win32Interop.SC_SIZE || cmd == Win32Interop.SC_MAXIMIZE))
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                    if (!_isDirectDisplayMode && Topmost && (_isFullScreen || _isLocked || _pinAllDesktops) && cmd == Win32Interop.SC_MINIMIZE)
                    {
                        // Block Win+D / Win+M from minimizing the always-on-top monitor window
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                break;

            case Win32Interop.WM_ACTIVATEAPP:
                if (!_isDirectDisplayMode && Topmost && _geometryAppliedOnce)
                {
                    Dispatcher.BeginInvoke(EnforceTopMostIfNeeded, DispatcherPriority.Background);
                }
                break;

            case Win32Interop.WM_WINDOWPOSCHANGING:
                if (!_applyingGeometry && _geometryAppliedOnce && lParam != IntPtr.Zero && !Win32Interop.IsIconic(hwnd))
                {
                    var wp = Marshal.PtrToStructure<Win32Interop.WINDOWPOS>(lParam);
                    bool modified = false;

                    // Keep HWND_TOPMOST intact even when Windows/Win+D tries to demote Z-order
                    if (!_isDirectDisplayMode && Topmost && (wp.flags & Win32Interop.SWP_NOZORDER) == 0 && wp.hwndInsertAfter != Win32Interop.HWND_TOPMOST)
                    {
                        wp.hwndInsertAfter = Win32Interop.HWND_TOPMOST;
                        modified = true;
                    }

                    if (_isFullScreen || _isLocked)
                    {
                        bool hasMove = (wp.flags & Win32Interop.SWP_NOMOVE) == 0;
                        bool hasSize = (wp.flags & Win32Interop.SWP_NOSIZE) == 0;

                        if (hasMove || hasSize)
                        {
                            bool isMinimizing = (hasMove && (wp.x <= -10000 || wp.y <= -10000)) ||
                                                (hasSize && (wp.cx <= 0 || wp.cy <= 0));
                            if (!isMinimizing)
                            {
                                if (hasMove && (wp.x != _targetPhysicalRect.Left || wp.y != _targetPhysicalRect.Top))
                                {
                                    wp.x = _targetPhysicalRect.Left;
                                    wp.y = _targetPhysicalRect.Top;
                                    modified = true;
                                }
                                if (hasSize && (wp.cx != _targetPhysicalRect.Width || wp.cy != _targetPhysicalRect.Height))
                                {
                                    wp.cx = _targetPhysicalRect.Width;
                                    wp.cy = _targetPhysicalRect.Height;
                                    modified = true;
                                }
                            }
                        }
                    }

                    if (modified)
                    {
                        Marshal.StructureToPtr(wp, lParam, false);
                    }
                }
                break;

            case Win32Interop.WM_EXITSIZEMOVE:
                if (!_isFullScreen && !_isLocked && !_applyingGeometry)
                {
                    HandleAfterWindowMoveOrResize();
                }
                break;

            case Win32Interop.WM_DPICHANGED:
                if (_geometryAppliedOnce && (_isFullScreen || _isLocked || _applyingGeometry))
                {
                    Dispatcher.BeginInvoke(ReapplyTargetPhysicalRect);
                }
                break;

            case Win32Interop.WM_SIZE:
                if (wParam.ToInt32() == Win32Interop.SIZE_RESTORED && _geometryAppliedOnce && (_isFullScreen || _isLocked) && !_applyingGeometry)
                {
                    Dispatcher.BeginInvoke(ReapplyTargetPhysicalRect);
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_isDisposing) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            string type = typeProp.GetString() ?? "";

            switch (type)
            {
                case "startDrag":
                    if (!_isFullScreen && !_isLocked)
                    {
                        var hwnd = new WindowInteropHelper(this).Handle;
                        Win32Interop.ReleaseCapture();
                        Win32Interop.SendMessage(hwnd, Win32Interop.WM_NCLBUTTONDOWN, (IntPtr)Win32Interop.HTCAPTION, IntPtr.Zero);
                        HandleAfterWindowMoveOrResize();
                    }
                    break;

                case "toggleFullScreen":
                    _controller.ToggleFullScreen();
                    break;

                case "setFullScreen":
                    if (root.TryGetProperty("value", out var fsVal))
                        _controller.SetFullScreen(fsVal.GetBoolean());
                    break;

                case "setMonitor":
                    if (root.TryGetProperty("deviceName", out var devProp))
                    {
                        var dev = devProp.GetString();
                        if (!string.IsNullOrWhiteSpace(dev))
                            _controller.SwitchMonitor(dev);
                    }
                    break;

                case "setAlwaysOnTop":
                    if (root.TryGetProperty("value", out var topVal))
                        _controller.SetAlwaysOnTop(topVal.GetBoolean());
                    break;

                case "openControlWindow":
                    _controller.ShowControlWindow();
                    break;

                case "recreateDisplay":
                    _ = _controller.RecreateDisplayWindowAsync();
                    break;

                case "windowMinimize":
                    WindowState = WindowState.Minimized;
                    break;

                case "windowClose":
                    _controller.CloseDisplayWindow();
                    break;

                case "selectSymbol":
                    if (root.TryGetProperty("symbolId", out var symId))
                        _controller.SelectSymbol(symId.GetString() ?? "");
                    break;

                case "selectCategory":
                    if (root.TryGetProperty("category", out var catVal))
                        _controller.SelectCategory(catVal.GetString() ?? "");
                    break;

                case "selectInterval":
                    if (root.TryGetProperty("interval", out var intv))
                        _controller.SelectInterval(intv.GetString() ?? "");
                    break;

                case "setCryptoConfig":
                    {
                        string? ex = root.TryGetProperty("exchange", out var exP) ? exP.GetString() : null;
                        string? mt = root.TryGetProperty("marketType", out var mtP) ? mtP.GetString() : null;
                        _controller.SetCryptoConfig(ex, mt);
                    }
                    break;

                case "refreshMarket":
                    _controller.RefreshMarketNow();
                    break;

                case "refreshNetworkIp":
                    _controller.RefreshNetworkIpNow();
                    break;

                case "updateSettings":
                    if (root.TryGetProperty("patch", out var patch))
                        ApplySettingsPatch(patch);
                    break;

                case "addCustomSymbol":
                    if (root.TryGetProperty("symbol", out var symObj))
                    {
                        string code = symObj.GetProperty("code").GetString() ?? "";
                        string name = symObj.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        string mode = symObj.TryGetProperty("mode", out var m) ? (m.GetString() ?? "us_stock") : "us_stock";
                        _controller.AddCustomSymbol(code, name, mode);
                    }
                    break;

                case "removeCustomSymbol":
                    if (root.TryGetProperty("symbolId", out var remId))
                        _controller.RemoveCustomSymbol(remId.GetString() ?? "");
                    break;
            }
        }
        catch { }
    }

    private void CaptureCurrentGeometryInternal(bool notifyController, bool markCustomPreset)
    {
        if (_isFullScreen || _isDisposing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || Win32Interop.IsIconic(hwnd)) return;
        if (!Win32Interop.GetWindowRect(hwnd, out var rect) || rect.Width < 360 || rect.Height < 240) return;

        var mon = _controller.DisplayManager.GetMonitorForWindow(hwnd) ?? CurrentMonitor;
        CurrentMonitor = mon;
        _targetPhysicalRect = rect;

        if (notifyController)
        {
            _controller.NotifyWindowGeometryChangedFromDisplay(mon, rect, markCustomPreset);
        }
        else
        {
            var s = _controller.Settings;
            s.TargetMonitorDevice = mon.DeviceName;
            s.WindowOffsetX = rect.Left - mon.WorkLeft;
            s.WindowOffsetY = rect.Top - mon.WorkTop;
            s.WindowLeft = rect.Left;
            s.WindowTop = rect.Top;
            s.WindowWidth = rect.Width;
            s.WindowHeight = rect.Height;
            s.HasWindowOffset = true;
            _controller.SaveSettingsOnly();
        }
    }

    private void HandleAfterWindowMoveOrResize()
    {
        if (_isFullScreen || _isLocked || _isDisposing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || Win32Interop.IsIconic(hwnd)) return;
        if (!Win32Interop.GetWindowRect(hwnd, out var rect) || rect.Width < 360 || rect.Height < 240) return;

        var prevMon = CurrentMonitor;
        var newMon = _controller.DisplayManager.GetMonitorForWindow(hwnd) ?? prevMon;
        bool monitorChanged = !string.Equals(newMon.DeviceName, prevMon.DeviceName, StringComparison.OrdinalIgnoreCase);
        bool gpuChanged = monitorChanged && !string.Equals(newMon.AdapterName, prevMon.AdapterName, StringComparison.OrdinalIgnoreCase);

        CurrentMonitor = newMon;
        _targetPhysicalRect = rect;
        _controller.NotifyWindowGeometryChangedFromDisplay(newMon, rect, markCustomPreset: true);

        // If user dragged across heterogeneous GPUs while in GPU/NoDComp mode, seamlessly recreate on target GPU!
        if (gpuChanged && !string.Equals(_controller.Settings.RenderMode, "safe", StringComparison.OrdinalIgnoreCase))
        {
            _ = _controller.RecreateDisplayWindowAsync(newMon.DeviceName);
            return;
        }

        if (monitorChanged)
        {
            PostWindowState();
        }
    }

    private void ApplySettingsPatch(JsonElement patch)
    {
        _controller.UpdateSettings(s =>
        {
            if (patch.TryGetProperty("theme", out var theme)) s.Theme = theme.GetString() ?? s.Theme;
            if (patch.TryGetProperty("layoutMode", out var layout)) s.LayoutMode = layout.GetString() ?? s.LayoutMode;
            if (patch.TryGetProperty("colorScheme", out var cs)) s.ColorScheme = cs.GetString() ?? s.ColorScheme;
            if (patch.TryGetProperty("activeCategory", out var cat)) s.ActiveCategory = cat.GetString() ?? s.ActiveCategory;
            if (patch.TryGetProperty("telemetryIntervalMs", out var interval) && interval.TryGetInt32(out int ms))
                s.TelemetryIntervalMs = ms;
            if (patch.TryGetProperty("showMA", out var ma)) s.ShowMA = ma.GetBoolean();
            if (patch.TryGetProperty("showBoll", out var boll)) s.ShowBoll = boll.GetBoolean();
            if (patch.TryGetProperty("showVolume", out var vol)) s.ShowVolume = vol.GetBoolean();
            if (patch.TryGetProperty("oledPixelShift", out var oled)) s.OledPixelShift = oled.GetBoolean();
        }, pushVisualToDisplay: false);
    }

    private static void SetWindowPhysicalRect(IntPtr hwnd, int x, int y, int width, int height, bool topMost)
    {
        var zOrder = topMost ? Win32Interop.HWND_TOPMOST : Win32Interop.HWND_NOTOPMOST;
        Win32Interop.SetWindowPos(
            hwnd,
            zOrder,
            x,
            y,
            width,
            height,
            Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_SHOWWINDOW | Win32Interop.SWP_NOACTIVATE);
    }

    private void DisplayWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            _controller.ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullScreen)
        {
            _controller.SetFullScreen(false);
            e.Handled = true;
        }
        else if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _controller.ShowControlWindow();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int idx = (int)e.Key - (int)Key.D1;
            var monitors = _controller.DisplayManager.GetMonitors();
            if (idx >= 0 && idx < monitors.Count)
            {
                _controller.SwitchMonitor(monitors[idx].DeviceName);
                e.Handled = true;
            }
        }
    }

    private void DisplayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isFullScreen && !_isLocked && !_isDisposing)
        {
            CaptureCurrentGeometryInternal(notifyController: false, markCustomPreset: false);
        }
        _isDisposing = true;
        _webReady = false;
        StopTimersAndDirectResources();
        DetachWndProc();
        try { WebView?.Dispose(); } catch { }
    }
}
