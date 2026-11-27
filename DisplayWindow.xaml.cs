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
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Window chrome brushes come from the active skin palette (including OLED True Black).
    private readonly AppController _controller;
    private readonly DirectDisplaySession? _directSession;
    private readonly bool _isDirectDisplayMode;
    private readonly object _directBitmapLock = new();
    private HwndSource? _hwndSource;
    private DispatcherTimer? _topMostWatchdog;
    private DispatcherTimer? _directFrameTimer;
    private Bitmap? _directCaptureBitmap;
    private byte[]? _lastDirectFrameBuffer;
    private int _directForcePresentFrames = 3;
    private bool _directPresentBusy;
    private volatile bool _webReady;
    private bool _isFullScreen;
    private bool _isLocked;
    private bool _pinAllDesktops;
    private volatile bool _isDisposing;
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
        if (directSession != null)
            directSession.Rotation = controller.Settings.DirectDisplayRotation;

        InitializeComponent();
        ShowActivated = false;

        var s = _controller.Settings;
        _isFullScreen = _isDirectDisplayMode || s.IsFullScreen;
        _isLocked = _isDirectDisplayMode || s.LockWindowPosition;
        _pinAllDesktops = s.PinToAllVirtualDesktops;
        Topmost = !_isDirectDisplayMode && s.AlwaysOnTop;
        // Performance monitor window never occupies the Windows Taskbar; only the management panel stays on taskbar.
        ShowInTaskbar = false;

        // Clear XAML MinWidth/MinHeight in fullscreen & direct display modes so narrow/bar/rotated screens never clip.
        if (_isFullScreen || _isDirectDisplayMode)
        {
            MinWidth = 0;
            MinHeight = 0;
        }
        else
        {
            MinWidth = 280;
            MinHeight = 180;
        }

        LoadingText.Text = _isDirectDisplayMode
            ? $"正在硬件独占屏幕 {directSession!.Descriptor.DisplayName} ({directSession.Width}×{directSession.Height}) 上初始化..."
            : $"正在 {targetMonitor.FriendlyName} 上初始化监控窗口...";
        ApplyThemeChrome(s);

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
            ? new Win32Interop.RECT { Left = -16000, Top = -16000, Right = -16000 + directSession!.ContentWidth, Bottom = -16000 + directSession.ContentHeight }
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
                Win32Interop.ApplyVirtualDesktopPinStyle(hwnd, _pinAllDesktops, showInTaskbar: false, noActivate: false);
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
        if (_directSession == null || _isDisposing) return;

        if (_directFrameTimer != null)
        {
            _directFrameTimer.Stop();
            _directFrameTimer.Tick -= DirectFrameTimer_Tick;
            _directFrameTimer = null;
        }

        int fps = Math.Clamp(_controller.Settings.DirectDisplayFps, 2, 30);
        int captureW = Math.Max(1, _directSession.ContentWidth);
        int captureH = Math.Max(1, _directSession.ContentHeight);

        lock (_directBitmapLock)
        {
            _directCaptureBitmap?.Dispose();
            _directCaptureBitmap = new Bitmap(captureW, captureH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            _lastDirectFrameBuffer = null;
            _directForcePresentFrames = 3;
        }

        _directFrameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(33, 1000 / fps))
        };
        _directFrameTimer.Tick += DirectFrameTimer_Tick;
        _directFrameTimer.Start();
    }

    private void DirectFrameTimer_Tick(object? sender, EventArgs e)
    {
        PumpOneDirectDisplayFrame();
    }

    /// <summary>
    /// Live-updates the Direct Display output rotation. Switching between landscape and portrait
    /// re-lays out the off-screen render window and capture buffer to the swapped size.
    /// </summary>
    public void UpdateDirectDisplayRotation(int degrees)
    {
        if (_directSession == null || _isDisposing) return;
        int prevRotation = _directSession.Rotation;
        bool wasPortrait = _directSession.IsPortraitRotation;
        _directSession.Rotation = degrees;

        if (prevRotation == _directSession.Rotation) return;

        // Always force fresh uploads after any rotation change (including 0 <-> 180 or 90 <-> 270).
        lock (_directBitmapLock)
        {
            _lastDirectFrameBuffer = null;
            _directForcePresentFrames = 3;
        }

        if (wasPortrait == _directSession.IsPortraitRotation) return;

        ApplyGeometryForCurrentMode(isInitialPlacement: false);
        if (_directFrameTimer != null)
        {
            StartDirectDisplayFramePump();
        }
    }

    public void UpdateDirectDisplayFps(int fps)
    {
        if (_directFrameTimer == null) return;
        int clamped = Math.Clamp(fps, 2, 30);
        _directFrameTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(33, 1000 / clamped));
    }

    public DirectDisplayDiagnostics GetDirectDisplayDiagnostics()
    {
        if (_isDisposing || !_isDirectDisplayMode || _directSession == null)
        {
            return new DirectDisplayDiagnostics { IsActive = false };
        }
        return _directSession.GetDiagnostics(_controller.Settings.DirectDisplayFps);
    }

    private void PumpOneDirectDisplayFrame()
    {
        if (_isDisposing || _directPresentBusy || _directSession == null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _directPresentBusy = true;
        try
        {
            lock (_directBitmapLock)
            {
                if (_isDisposing || _directSession == null || _directCaptureBitmap == null) return;

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
                    if (bits.Scan0 == IntPtr.Zero || bits.Stride <= 0 || bits.Height <= 0) return;

                    int totalBytes = bits.Stride * bits.Height;
                    bool sameAsLast = false;

                    unsafe
                    {
                        var srcSpan = new ReadOnlySpan<byte>((void*)bits.Scan0, totalBytes);
                        if (_directForcePresentFrames == 0 &&
                            _lastDirectFrameBuffer != null &&
                            _lastDirectFrameBuffer.Length == totalBytes &&
                            srcSpan.SequenceEqual(_lastDirectFrameBuffer))
                        {
                            sameAsLast = true;
                        }
                        else
                        {
                            if (_lastDirectFrameBuffer == null || _lastDirectFrameBuffer.Length != totalBytes)
                            {
                                _lastDirectFrameBuffer = new byte[totalBytes];
                            }
                            srcSpan.CopyTo(_lastDirectFrameBuffer);
                        }
                    }

                    if (sameAsLast)
                    {
                        _directSession.RecordSkippedFrame();
                    }
                    else
                    {
                        bool presented = _directSession.PresentBgraFrame(bits.Scan0, bits.Stride, bits.Width, bits.Height);
                        if (presented)
                        {
                            if (_directForcePresentFrames > 0) _directForcePresentFrames--;
                        }
                        else
                        {
                            // Retry upload on next tick if scanout presentation failed
                            _lastDirectFrameBuffer = null;
                        }
                    }
                }
                finally
                {
                    _directCaptureBitmap.UnlockBits(bits);
                }
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
            string mode = _isDirectDisplayMode ? "safe" : SettingsService.NormalizeRenderMode(_controller.Settings.RenderMode);
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
        if (_isDirectDisplayMode) return;

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
        if (_isDirectDisplayMode) return;

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
        ShowInTaskbar = false;
    }

    public void ApplyPinToAllVirtualDesktops(bool pinAllDesktops)
    {
        if (_isDirectDisplayMode) return;
        _pinAllDesktops = pinAllDesktops;
        ShowInTaskbar = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.ApplyVirtualDesktopPinStyle(hwnd, pinAllDesktops, showInTaskbar: false, noActivate: false);
            EnforceTopMostIfNeeded();
        }
    }

    public void ApplyThemeChrome(AppSettings settings)
    {
        bool isDark = SettingsService.NormalizeTheme(settings.Theme) == "dark";
        var palette = SkinCatalog.ForSettings(settings);
        var bgBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(palette.BgPage.R, palette.BgPage.G, palette.BgPage.B));
        var lockedBorder = new SolidColorBrush(System.Windows.Media.Color.FromRgb(palette.Border.R, palette.Border.G, palette.Border.B));
        var accentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(palette.AccentText.R, palette.AccentText.G, palette.AccentText.B));
        var mutedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(palette.TextMuted.R, palette.TextMuted.G, palette.TextMuted.B));

        Background = bgBrush;
        RootBorder.Background = bgBrush;
        LoadingOverlay.Background = bgBrush;
        LoadingTitle.Foreground = accentBrush;
        LoadingText.Foreground = mutedBrush;
        RootBorder.BorderBrush = _isLocked ? lockedBorder : accentBrush;

        try
        {
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, palette.BgPage.R, palette.BgPage.G, palette.BgPage.B);
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
        if (_isDirectDisplayMode) return;

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
        PostToWeb("settingsUpdated", BuildSettingsPayload(settings));
    }

    /// <summary>
    /// Serializes AppSettings for the dashboard and appends the resolved skin palette as inline
    /// CSS custom properties, so the WebView picks up the same colors as the WPF windows.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject BuildSettingsPayload(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        node["cssVariables"] = JsonSerializer.SerializeToNode(
            SideScreenHUD.Models.SkinCatalog.CssVariablesFor(settings), JsonOptions);
        return node;
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
        if (!_isDirectDisplayMode && !_isFullScreen && !_isLocked && !_isDisposing)
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
        try
        {
            if (_directFrameTimer != null)
            {
                _directFrameTimer.Stop();
                _directFrameTimer.Tick -= DirectFrameTimer_Tick;
                _directFrameTimer = null;
            }
        }
        catch { }

        lock (_directBitmapLock)
        {
            try { _directCaptureBitmap?.Dispose(); _directCaptureBitmap = null; } catch { }
            _lastDirectFrameBuffer = null;
        }

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
                Right = -16000 + _directSession.ContentWidth,
                Bottom = -16000 + _directSession.ContentHeight
            };
        }
        else
        {
            // Refresh CurrentMonitor geometry in case taskbar/resolution changed
            var latestMon = _controller.DisplayManager.ResolveTargetMonitor(CurrentMonitor.DeviceName, preferSecondary: false);
            if (latestMon != null) CurrentMonitor = latestMon;
            _targetPhysicalRect = ComputeTargetRect(CurrentMonitor);
        }

        bool isDark = SettingsService.NormalizeTheme(_controller.Settings.Theme) == "dark";
        var palette = SkinCatalog.ForSettings(_controller.Settings);
        var lockedBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(palette.Border.R, palette.Border.G, palette.Border.B));
        var accentBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(palette.AccentText.R, palette.AccentText.G, palette.AccentText.B));

        _applyingGeometry = true;
        try
        {
            if (_isFullScreen || _isDirectDisplayMode)
            {
                MinWidth = 0;
                MinHeight = 0;
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.NoResize;
                Chrome.ResizeBorderThickness = new Thickness(0);
                RootBorder.Padding = new Thickness(0);
                RootBorder.BorderThickness = new Thickness(0);
                Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: false);
            }
            else if (_isLocked)
            {
                MinWidth = 280;
                MinHeight = 180;
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.NoResize;
                Chrome.ResizeBorderThickness = new Thickness(0);
                RootBorder.Padding = new Thickness(0);
                RootBorder.BorderThickness = new Thickness(1);
                RootBorder.BorderBrush = lockedBorderBrush;
                Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
            }
            else
            {
                MinWidth = 280;
                MinHeight = 180;
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.CanResize;
                Chrome.ResizeBorderThickness = new Thickness(6);
                RootBorder.Padding = new Thickness(4);
                RootBorder.BorderThickness = new Thickness(1);
                RootBorder.BorderBrush = accentBorderBrush;
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
            case WM_GETMINMAXINFO:
                if ((_isDirectDisplayMode || _isFullScreen) && lParam != IntPtr.Zero)
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize.X = 1;
                    mmi.ptMinTrackSize.Y = 1;
                    int maxW = Math.Max(1, _targetPhysicalRect.Width) + 64;
                    int maxH = Math.Max(1, _targetPhysicalRect.Height) + 64;
                    mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, maxW);
                    mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, maxH);
                    mmi.ptMaxSize.X = Math.Max(mmi.ptMaxSize.X, maxW);
                    mmi.ptMaxSize.Y = Math.Max(mmi.ptMaxSize.Y, maxH);
                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

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

                    if (_isFullScreen || _isLocked || _isDirectDisplayMode)
                    {
                        bool hasMove = (wp.flags & Win32Interop.SWP_NOMOVE) == 0;
                        bool hasSize = (wp.flags & Win32Interop.SWP_NOSIZE) == 0;

                        if (hasMove || hasSize)
                        {
                            bool isMinimizing = (!_isDirectDisplayMode && hasMove && (wp.x <= -10000 || wp.y <= -10000)) ||
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
                if (!_isDirectDisplayMode && !_isFullScreen && !_isLocked && !_applyingGeometry)
                {
                    HandleAfterWindowMoveOrResize();
                }
                break;

            case Win32Interop.WM_DPICHANGED:
                if (_geometryAppliedOnce && (_isFullScreen || _isLocked || _isDirectDisplayMode || _applyingGeometry))
                {
                    Dispatcher.BeginInvoke(ReapplyTargetPhysicalRect);
                }
                break;

            case Win32Interop.WM_SIZE:
                if (wParam.ToInt32() == Win32Interop.SIZE_RESTORED && _geometryAppliedOnce && (_isFullScreen || _isLocked || _isDirectDisplayMode) && !_applyingGeometry)
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
                    if (!_isDirectDisplayMode && !_isFullScreen && !_isLocked)
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
                    if (!_isDirectDisplayMode)
                        WindowState = WindowState.Minimized;
                    break;

                case "windowClose":
                    _controller.CloseDisplayWindow();
                    break;

                case "restartAsAdmin":
                    _controller.RestartAsAdministrator();
                    break;

                case "resetSessionStats":
                    _controller.ResetSessionStats();
                    break;

                case "setMonitorPage":
                    {
                        string? page = root.TryGetProperty("page", out var pageProp)
                            ? pageProp.GetString()
                            : (root.TryGetProperty("value", out var valProp) ? valProp.GetString() : null);
                        if (!string.IsNullOrWhiteSpace(page))
                            _controller.SetMonitorPage(page);
                    }
                    break;

                case "toggleMonitorPage":
                    _controller.ToggleMonitorPage();
                    break;

                case "setProcessTreeOptions":
                    {
                        var src = root.TryGetProperty("options", out var optsObj) && optsObj.ValueKind == JsonValueKind.Object
                            ? optsObj
                            : root;
                        string? viewMode = src.TryGetProperty("viewMode", out var vm) && vm.ValueKind == JsonValueKind.String ? vm.GetString() : null;
                        string? sortBy = src.TryGetProperty("sortBy", out var sb) && sb.ValueKind == JsonValueKind.String ? sb.GetString() : null;
                        bool? autoFollowTop = src.TryGetProperty("autoFollowTop", out var af) && af.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? af.GetBoolean()
                            : null;
                        int? pinnedPid = src.TryGetProperty("pinnedPid", out var pp) && pp.TryGetInt32(out int pidVal)
                            ? pidVal
                            : null;
                        string? filter = src.TryGetProperty("filter", out var flt) && flt.ValueKind == JsonValueKind.String ? flt.GetString() : null;
                        _controller.SetProcessTreeOptions(viewMode, sortBy, autoFollowTop, pinnedPid, filter);
                    }
                    break;

                case "openProcessLocation":
                    if (root.TryGetProperty("pid", out var openPidProp) && openPidProp.TryGetInt32(out int openPid))
                    {
                        bool ok = _controller.TryOpenProcessLocation(openPid, out string err);
                        PostToWeb("processActionResult", new
                        {
                            action = "openLocation",
                            pid = openPid,
                            success = ok,
                            message = ok ? "已在资源管理器中定位可执行文件" : err
                        });
                    }
                    break;

                case "terminateProcess":
                    if (root.TryGetProperty("pid", out var termPidProp) && termPidProp.TryGetInt32(out int termPid))
                    {
                        bool ok = _controller.TryTerminateProcess(termPid, out string err);
                        PostToWeb("processActionResult", new
                        {
                            action = "terminate",
                            pid = termPid,
                            success = ok,
                            message = ok ? $"已结束进程 PID {termPid}" : err
                        });
                    }
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
        if (_isDirectDisplayMode || _isFullScreen || _isDisposing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || Win32Interop.IsIconic(hwnd)) return;
        if (!Win32Interop.GetWindowRect(hwnd, out var rect) || rect.Width < 240 || rect.Height < 150) return;

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
        if (_isDirectDisplayMode || _isFullScreen || _isLocked || _isDisposing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || Win32Interop.IsIconic(hwnd)) return;
        if (!Win32Interop.GetWindowRect(hwnd, out var rect) || rect.Width < 240 || rect.Height < 150) return;

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
        bool needVisualSync = false;
        _controller.UpdateSettings(s =>
        {
            if (patch.TryGetProperty("theme", out var theme))
            {
                s.Theme = theme.GetString() ?? s.Theme;
                needVisualSync = true;
            }
            if (patch.TryGetProperty("skin", out var skin))
            {
                s.Skin = skin.GetString() ?? s.Skin;
                needVisualSync = true;
            }
            if (patch.TryGetProperty("hudStyle", out var hudStyle))
                s.HudStyle = hudStyle.GetString() ?? s.HudStyle;
            if (patch.TryGetProperty("oledTrueBlack", out var oledBlack) && oledBlack.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                s.OledTrueBlack = oledBlack.GetBoolean();
                needVisualSync = true;
            }
            if (patch.TryGetProperty("maskPublicIp", out var maskIp) && maskIp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                s.MaskPublicIp = maskIp.GetBoolean();
            if (patch.TryGetProperty("layoutMode", out var layout)) s.LayoutMode = layout.GetString() ?? s.LayoutMode;
            if (patch.TryGetProperty("colorScheme", out var cs))
            {
                s.ColorScheme = cs.GetString() ?? s.ColorScheme;
                needVisualSync = true;
            }
            if (patch.TryGetProperty("activeCategory", out var cat)) s.ActiveCategory = cat.GetString() ?? s.ActiveCategory;
            if (patch.TryGetProperty("telemetryIntervalMs", out var interval) && interval.TryGetInt32(out int ms))
                s.TelemetryIntervalMs = ms;
            if (patch.TryGetProperty("showMA", out var ma) && ma.ValueKind is JsonValueKind.True or JsonValueKind.False) s.ShowMA = ma.GetBoolean();
            if (patch.TryGetProperty("showBoll", out var boll) && boll.ValueKind is JsonValueKind.True or JsonValueKind.False) s.ShowBoll = boll.GetBoolean();
            if (patch.TryGetProperty("showVolume", out var vol) && vol.ValueKind is JsonValueKind.True or JsonValueKind.False) s.ShowVolume = vol.GetBoolean();
            if (patch.TryGetProperty("oledPixelShift", out var oled) && oled.ValueKind is JsonValueKind.True or JsonValueKind.False) s.OledPixelShift = oled.GetBoolean();
            if (patch.TryGetProperty("cpuViewMode", out var cvm)) s.CpuViewMode = cvm.GetString() ?? s.CpuViewMode;
            if (patch.TryGetProperty("monitorPage", out var mp)) s.MonitorPage = mp.GetString() ?? s.MonitorPage;
            if (patch.TryGetProperty("processTreeViewMode", out var ptvm)) s.ProcessTreeViewMode = ptvm.GetString() ?? s.ProcessTreeViewMode;
            if (patch.TryGetProperty("processTreeSortBy", out var ptsb)) s.ProcessTreeSortBy = ptsb.GetString() ?? s.ProcessTreeSortBy;
            if (patch.TryGetProperty("processTreeAutoFollowTop", out var ptaf) && ptaf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                s.ProcessTreeAutoFollowTop = ptaf.GetBoolean();
            if (patch.TryGetProperty("processTreePinnedPid", out var ptpp) && ptpp.TryGetInt32(out int ptPid))
                s.ProcessTreePinnedPid = ptPid;
            if (patch.TryGetProperty("processTreeFilter", out var ptflt) && ptflt.ValueKind == JsonValueKind.String)
                s.ProcessTreeFilter = ptflt.GetString() ?? "";
        }, pushVisualToDisplay: needVisualSync);
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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.F11)
        {
            _controller.ToggleFullScreen();
            e.Handled = true;
        }
        else if (key == Key.Escape && _isFullScreen)
        {
            _controller.SetFullScreen(false);
            e.Handled = true;
        }
        else if (key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _controller.ToggleMonitorPage();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (key == Key.D1 || key == Key.NumPad1))
        {
            _controller.SetMonitorPage("overview");
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (key == Key.D2 || key == Key.NumPad2))
        {
            _controller.SetMonitorPage("proctree");
            e.Handled = true;
        }
        else if (key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _controller.ShowControlWindow();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D9)
        {
            int idx = (int)key - (int)Key.D1;
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
        if (!_isDirectDisplayMode && !_isFullScreen && !_isLocked && !_isDisposing)
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
