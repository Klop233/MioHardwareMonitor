using System.Diagnostics;
using Windows.Devices.Display;
using Windows.Devices.Display.Core;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using SideScreenHUD.Models;
using SideScreenHUD.Native;

namespace SideScreenHUD.Services;

/// <summary>
/// Discovers and acquires Windows 11 Specialized Displays ("从桌面删除显示器")
/// via Windows.Devices.Display.Core for true hardware-level exclusive scanout,
/// completely bypassing DWM, virtual desktops, Win+D, and the Windows shell.
/// </summary>
public static class DirectDisplayService
{
    public static List<DirectDisplayTargetDescriptor> EnumerateTargets()
    {
        var list = new List<DirectDisplayTargetDescriptor>();
        try
        {
            var dxgiByLuid = DxgiHelper.EnumerateAdapters()
                .GroupBy(a => a.LuidKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            using var mgr = Windows.Devices.Display.Core.DisplayManager.Create(DisplayManagerOptions.None);
            var targets = mgr.GetCurrentTargets();

            foreach (var t in targets)
            {
                if (!t.IsConnected) continue;

                var luid = t.Adapter.Id;
                string luidKey = $"luid_0x{(uint)luid.HighPart:x8}_0x{luid.LowPart:x8}";
                dxgiByLuid.TryGetValue(luidKey, out var adapterName);
                adapterName ??= "";

                var mon = t.TryGetMonitor();
                int w = mon != null ? mon.NativeResolutionInRawPixels.Width : 0;
                int h = mon != null ? mon.NativeResolutionInRawPixels.Height : 0;
                string rawName = mon?.DisplayName?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(rawName))
                    rawName = !string.IsNullOrWhiteSpace(t.StableMonitorId) ? t.StableMonitorId : $"Display #{t.AdapterRelativeId}";

                bool isSpecialPurpose = t.UsageKind == DisplayMonitorUsageKind.SpecialPurpose;
                bool isHeadMounted = t.UsageKind == DisplayMonitorUsageKind.HeadMounted;
                bool isSpecialized = isSpecialPurpose || isHeadMounted;
                string targetKey = BuildTargetKey(t);
                string resPart = w > 0 && h > 0 ? $" {w}×{h}" : "";
                string gpuPart = string.IsNullOrWhiteSpace(adapterName) ? "" : $" · {adapterName}";
                string badge = isSpecialPurpose
                    ? "[已脱离桌面 · 可硬件独占]"
                    : (isHeadMounted ? "[头显专用 · 需手动选择]" : "[桌面托管 · 需先从桌面删除]");

                list.Add(new DirectDisplayTargetDescriptor
                {
                    TargetKey = targetKey,
                    StableMonitorId = t.StableMonitorId ?? "",
                    DisplayName = rawName,
                    AdapterName = adapterName,
                    AdapterLuidLow = luid.LowPart,
                    AdapterLuidHigh = luid.HighPart,
                    AdapterRelativeId = t.AdapterRelativeId,
                    Width = w,
                    Height = h,
                    RefreshRateHz = 60,
                    UsageKind = t.UsageKind.ToString(),
                    IsSpecialized = isSpecialized,
                    FriendlyName = $"{badge} {rawName}{resPart}{gpuPart}"
                });
            }
        }
        catch
        {
            // OS does not support Windows.Devices.Display.Core or unavailable
        }

        return list
            .OrderByDescending(x => string.Equals(x.UsageKind, nameof(DisplayMonitorUsageKind.SpecialPurpose), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.IsSpecialized)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryStartSession(
        string? preferredTargetKey,
        out DirectDisplaySession? session,
        out string errorMessage)
    {
        session = null;
        errorMessage = "";

        Windows.Devices.Display.Core.DisplayManager? mgr = null;
        DisplayTarget? acquiredTarget = null;
        DisplayDevice? displayDevice = null;
        DisplaySource? displaySource = null;
        DisplaySurface[]? primaries = null;
        DisplayScanout[]? scanouts = null;
        DisplayTaskPool? taskPool = null;
        D3D11DirectPresenter? presenter = null;

        try
        {
            mgr = Windows.Devices.Display.Core.DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership);
            var connected = mgr.GetCurrentTargets().Where(t => t.IsConnected).ToList();
            if (connected.Count == 0)
            {
                errorMessage = "未检测到任何已连接的显示器硬件目标。";
                mgr.Dispose();
                return false;
            }

            DisplayTarget? target = null;
            if (!string.IsNullOrWhiteSpace(preferredTargetKey))
            {
                string key = preferredTargetKey.Trim();
                target = connected.FirstOrDefault(t =>
                    string.Equals(t.StableMonitorId, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(BuildTargetKey(t), key, StringComparison.OrdinalIgnoreCase));
            }

            // Auto-fallback strictly prefers SpecialPurpose displays and never hijacks HeadMounted VR devices
            // unless the user explicitly selected that HeadMounted target by key.
            target ??= connected.FirstOrDefault(t => t.UsageKind == DisplayMonitorUsageKind.SpecialPurpose);

            if (target == null)
            {
                errorMessage = "当前没有已脱离桌面的专用显示器。请先点击下方「打开 Windows 脱离桌面设置」，在副屏的高级显示器设置中开启「从桌面删除显示器」，然后点击刷新重试。";
                mgr.Dispose();
                return false;
            }

            bool isSpecialized = target.UsageKind is DisplayMonitorUsageKind.SpecialPurpose or DisplayMonitorUsageKind.HeadMounted;
            var acquireResult = mgr.TryAcquireTargetsAndCreateEmptyState(new[] { target });
            if (acquireResult.ErrorCode != DisplayManagerResult.Success || acquireResult.State == null)
            {
                int hr = acquireResult.ExtendedErrorCode?.HResult ?? 0;
                errorMessage = !isSpecialized
                    ? $"所选显示器目前仍由 Windows 桌面托管（Usage={target.UsageKind}），无法直接硬件独占。请先在 Windows 设置 → 显示 → 高级显示器设置中开启「从桌面删除显示器」。"
                    : $"硬件独占显示器失败：{acquireResult.ErrorCode} (0x{hr:X8})，请确认无其他专用显示应用占用此屏幕。";
                mgr.Dispose();
                return false;
            }

            acquiredTarget = target;
            var state = acquireResult.State;
            var path = state.ConnectTarget(target);
            path.IsInterlaced = false;
            path.Scaling = DisplayPathScaling.Identity;

            var modes = path.FindModes(DisplayModeQueryOptions.OnlyPreferredResolution);
            var bestMode = SelectBestMode(modes);
            if (bestMode == null)
            {
                modes = path.FindModes(DisplayModeQueryOptions.None);
                bestMode = SelectBestMode(modes);
            }

            if (bestMode == null)
            {
                errorMessage = "未找到该显示器支持的 32 位 BGRA/RGBA 硬件扫描输出模式。";
                Cleanup();
                return false;
            }

            path.ApplyPropertiesFromMode(bestMode);

            var funcRes = state.TryFunctionalize(DisplayStateFunctionalizeOptions.None);
            if (funcRes.Status != DisplayStateOperationStatus.Success)
            {
                int hr = funcRes.ExtendedErrorCode?.HResult ?? 0;
                errorMessage = $"初始化硬件显示管线失败：{funcRes.Status} (0x{hr:X8})。";
                Cleanup();
                return false;
            }

            var applyRes = state.TryApply(DisplayStateApplyOptions.None);
            if (applyRes.Status != DisplayStateOperationStatus.Success)
            {
                int hr = applyRes.ExtendedErrorCode?.HResult ?? 0;
                errorMessage = $"应用硬件独占显示模式失败：{applyRes.Status} (0x{hr:X8})。";
                Cleanup();
                return false;
            }

            uint width = (uint)bestMode.SourceResolution.Width;
            uint height = (uint)bestMode.SourceResolution.Height;
            int hz = bestMode.PresentationRate.VerticalSyncRate.Denominator > 0
                ? (int)Math.Round((double)bestMode.PresentationRate.VerticalSyncRate.Numerator / bestMode.PresentationRate.VerticalSyncRate.Denominator)
                : 60;

            displayDevice = mgr.CreateDisplayDevice(target.Adapter);
            displaySource = displayDevice.CreateScanoutSource(target);

            var primaryDesc = new DisplayPrimaryDescription(
                width,
                height,
                bestMode.SourcePixelFormat,
                DirectXColorSpace.RgbFullG22NoneP709,
                false,
                new Direct3DMultisampleDescription { Count = 1, Quality = 0 });

            primaries = new DisplaySurface[2];
            scanouts = new DisplayScanout[2];
            for (int i = 0; i < 2; i++)
            {
                primaries[i] = displayDevice.CreatePrimary(target, primaryDesc);
                scanouts[i] = displayDevice.CreateSimpleScanout(displaySource, primaries[i], 0, 1);
            }

            presenter = new D3D11DirectPresenter(
                displayDevice,
                target.Adapter.Id.LowPart,
                target.Adapter.Id.HighPart,
                width,
                height,
                (uint)bestMode.SourcePixelFormat,
                primaries);

            taskPool = displayDevice.CreateTaskPool();

            var mon = target.TryGetMonitor();
            string dispName = mon?.DisplayName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(dispName))
                dispName = !string.IsNullOrWhiteSpace(target.StableMonitorId) ? target.StableMonitorId : $"Display #{target.AdapterRelativeId}";

            string adapterName = "";
            try
            {
                string luidKey = $"luid_0x{(uint)target.Adapter.Id.HighPart:x8}_0x{target.Adapter.Id.LowPart:x8}";
                adapterName = DxgiHelper.EnumerateAdapters()
                    .FirstOrDefault(a => string.Equals(a.LuidKey, luidKey, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            }
            catch { }

            var descriptor = new DirectDisplayTargetDescriptor
            {
                TargetKey = BuildTargetKey(target),
                StableMonitorId = target.StableMonitorId ?? "",
                DisplayName = dispName,
                AdapterName = adapterName,
                AdapterLuidLow = target.Adapter.Id.LowPart,
                AdapterLuidHigh = target.Adapter.Id.HighPart,
                AdapterRelativeId = target.AdapterRelativeId,
                Width = (int)width,
                Height = (int)height,
                RefreshRateHz = hz,
                UsageKind = target.UsageKind.ToString(),
                IsSpecialized = isSpecialized,
                FriendlyName = $"[硬件独占] {dispName} ({width}×{height} @{hz}Hz)"
            };

            session = new DirectDisplaySession(
                mgr,
                target,
                displayDevice,
                displaySource,
                primaries,
                scanouts,
                taskPool,
                presenter,
                descriptor);

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"启动硬件独占显示器异常：{ex.Message}";
            Cleanup();
            return false;
        }

        void Cleanup()
        {
            try { presenter?.Dispose(); } catch { }
            try
            {
                if (mgr != null && acquiredTarget != null)
                    mgr.ReleaseTarget(acquiredTarget);
            }
            catch { }
            try { mgr?.Dispose(); } catch { }
        }
    }

    private static int GetSupported32BitFormatRank(DirectXPixelFormat format) => format switch
    {
        DirectXPixelFormat.B8G8R8A8UIntNormalized => 1,
        DirectXPixelFormat.B8G8R8A8UIntNormalizedSrgb => 2,
        DirectXPixelFormat.R8G8B8A8UIntNormalized => 3,
        DirectXPixelFormat.R8G8B8A8UIntNormalizedSrgb => 4,
        _ => 0
    };

    private static double GetModeRefreshRateHz(DisplayModeInfo mode)
    {
        var rate = mode.PresentationRate.VerticalSyncRate;
        return rate.Denominator > 0 ? (double)rate.Numerator / rate.Denominator : 0;
    }

    private static DisplayModeInfo? SelectBestMode(IReadOnlyList<DisplayModeInfo>? modes)
    {
        if (modes == null || modes.Count == 0) return null;

        // Strictly restrict to 32-bit BGRA/RGBA formats (never pick 10-bit or FP16 HDR modes for 4-byte staging).
        var supported = modes
            .Where(m => GetSupported32BitFormatRank(m.SourcePixelFormat) > 0 && !m.IsStereo)
            .ToList();

        if (supported.Count == 0) return null;

        var progressive = supported.Where(m => !m.IsInterlaced).ToList();
        var pool = progressive.Count > 0 ? progressive : supported;

        // Within max resolution, prefer highest-ranked 32-bit format and refresh rate closest to 60Hz
        // (preferring >= 50Hz, lowest Hz tie-breaker) to avoid locking discrete GPU VRAM clocks to 240Hz P-states.
        return pool
            .OrderByDescending(m => (long)m.SourceResolution.Width * m.SourceResolution.Height)
            .ThenBy(m => GetSupported32BitFormatRank(m.SourcePixelFormat))
            .ThenBy(m => GetModeRefreshRateHz(m) >= 49.5 ? 0 : 1)
            .ThenBy(m => Math.Abs(GetModeRefreshRateHz(m) - 60.0))
            .ThenBy(m => GetModeRefreshRateHz(m))
            .FirstOrDefault();
    }

    private static string BuildTargetKey(DisplayTarget t) =>
        $"{(uint)t.Adapter.Id.HighPart:X8}_{t.Adapter.Id.LowPart:X8}:{t.AdapterRelativeId}";
}

public sealed class DirectDisplaySession : IDisposable
{
    private const int MaxConsecutivePresentFailures = 8;

    private readonly object _presentLock = new();
    private readonly object _diagLock = new();
    private readonly Windows.Devices.Display.Core.DisplayManager _manager;
    private readonly DisplayTarget _target;
    private readonly DisplayDevice _device;
    private readonly DisplaySource _source;
    private readonly DisplaySurface[] _primaries;
    private readonly DisplayScanout[] _scanouts;
    private readonly DisplayTaskPool _taskPool;
    private readonly D3D11DirectPresenter _presenter;

    private readonly long _sessionStartTicks = Stopwatch.GetTimestamp();
    private readonly Queue<long> _presentTicksWindow = new(64);
    private long _presentedFrames;
    private long _skippedFrames;
    private long _failedFrames;
    private int _consecutiveFailures;
    private double _avgUploadMs;
    private double _lastUploadMs;
    private string _lastStatus = "Ready";

    private int _backIndex;
    private volatile bool _disposed;
    private int _disposeStarted;
    private int _invalidatedRaised;

    public DirectDisplayTargetDescriptor Descriptor { get; }
    public int Width => Descriptor.Width;
    public int Height => Descriptor.Height;
    public int RefreshRateHz => Descriptor.RefreshRateHz;

    private volatile int _rotation;

    /// <summary>Clockwise content rotation in degrees (0/90/180/270) applied when presenting.</summary>
    public int Rotation
    {
        get => _rotation;
        set => _rotation = NormalizeRotation(value);
    }

    public bool IsPortraitRotation => _rotation is 90 or 270;

    /// <summary>Logical size of the rendered content before rotation (swapped for 90/270).</summary>
    public int ContentWidth => IsPortraitRotation ? Height : Width;
    public int ContentHeight => IsPortraitRotation ? Width : Height;

    public static int NormalizeRotation(int degrees)
    {
        int r = ((degrees % 360) + 360) % 360;
        return r switch { < 45 => 0, < 135 => 90, < 225 => 180, < 315 => 270, _ => 0 };
    }

    public event Action<string>? SessionInvalidated;

    internal DirectDisplaySession(
        Windows.Devices.Display.Core.DisplayManager manager,
        DisplayTarget target,
        DisplayDevice device,
        DisplaySource source,
        DisplaySurface[] primaries,
        DisplayScanout[] scanouts,
        DisplayTaskPool taskPool,
        D3D11DirectPresenter presenter,
        DirectDisplayTargetDescriptor descriptor)
    {
        _manager = manager;
        _target = target;
        _device = device;
        _source = source;
        _primaries = primaries;
        _scanouts = scanouts;
        _taskPool = taskPool;
        _presenter = presenter;
        Descriptor = descriptor;

        _manager.PathsFailedOrInvalidated += Manager_PathsFailedOrInvalidated;
        _manager.Disabled += Manager_Disabled;
        try { _manager.Start(); } catch { }
    }

    private void Manager_PathsFailedOrInvalidated(Windows.Devices.Display.Core.DisplayManager sender, DisplayManagerPathsFailedOrInvalidatedEventArgs args)
    {
        lock (_diagLock) { _lastStatus = "PathsInvalidated"; }
        RaiseInvalidatedAsync("硬件独占显示路径已失效（显示器可能已断开或重新回到桌面）。");
    }

    private void Manager_Disabled(Windows.Devices.Display.Core.DisplayManager sender, DisplayManagerDisabledEventArgs args)
    {
        lock (_diagLock) { _lastStatus = "Disabled"; }
        RaiseInvalidatedAsync("系统已停用该硬件独占显示会话。");
    }

    private void RaiseInvalidatedAsync(string reason)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _invalidatedRaised, 1) != 0) return;

        var handler = SessionInvalidated;
        if (handler == null) return;

        // Dispatch on ThreadPool to eliminate AB-BA deadlock between WinRT callback thread and UI Dispatcher.
        _ = Task.Run(() =>
        {
            try { handler.Invoke(reason); } catch { }
        });
    }

    public bool PresentBgraFrame(IntPtr pixels, int stride, int width, int height)
    {
        bool shouldInvalidate = false;
        string invalidateReason = "";

        lock (_presentLock)
        {
            if (_disposed) return false;

            long startTicks = Stopwatch.GetTimestamp();
            try
            {
                int idx = _backIndex;
                int rot = _rotation;
                bool uploaded = _presenter.UploadBgraFrame(pixels, stride, width, height, idx, rot);
                if (!uploaded)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                    RecordFailure("UploadFailed", elapsedMs, forceFatal: false, out shouldInvalidate, out invalidateReason);
                    goto HandleFailure;
                }

                var task = _taskPool.CreateTask();
                task.SetScanout(_scanouts[idx]);
                var result = _taskPool.TryExecuteTask(task);
                long endTicks = Stopwatch.GetTimestamp();
                double totalMs = (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;

                if (result.PresentStatus != DisplayPresentStatus.Success)
                {
                    string statusName = result.PresentStatus.ToString();
                    bool fatal = result.PresentStatus is
                        DisplayPresentStatus.DeviceInvalid or
                        DisplayPresentStatus.ScanoutInvalid or
                        DisplayPresentStatus.SourceInvalid;
                    RecordFailure(statusName, totalMs, fatal, out shouldInvalidate, out invalidateReason);
                    goto HandleFailure;
                }

                _backIndex = 1 - idx;
                RecordSuccess(endTicks, totalMs);
                return true;
            }
            catch (Exception ex)
            {
                double totalMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                RecordFailure($"Exception:{ex.GetType().Name}", totalMs, forceFatal: false, out shouldInvalidate, out invalidateReason);
                goto HandleFailure;
            }
        }

    HandleFailure:
        if (shouldInvalidate)
        {
            RaiseInvalidatedAsync(invalidateReason);
        }
        return false;
    }

    public void RecordSkippedFrame()
    {
        if (_disposed) return;
        lock (_diagLock)
        {
            _skippedFrames++;
        }
    }

    public DirectDisplayDiagnostics GetDiagnostics(int targetFps = 10)
    {
        int rot = _rotation;
        bool portrait = rot is 90 or 270;
        long now = Stopwatch.GetTimestamp();

        lock (_diagLock)
        {
            PruneFpsWindowLocked(now);
            double actualFps = ComputeActualFpsLocked(now);

            return new DirectDisplayDiagnostics
            {
                IsActive = !_disposed && Volatile.Read(ref _invalidatedRaised) == 0,
                TargetFps = Math.Clamp(targetFps, 1, 240),
                ActualFps = Math.Round(actualFps, 1),
                AvgUploadMs = Math.Round(_avgUploadMs, 2),
                LastUploadMs = Math.Round(_lastUploadMs, 2),
                PresentedFrames = _presentedFrames,
                SkippedFrames = _skippedFrames,
                FailedFrames = _failedFrames,
                LastStatus = _lastStatus,
                PanelWidth = Width,
                PanelHeight = Height,
                ContentWidth = portrait ? Height : Width,
                ContentHeight = portrait ? Width : Height,
                Rotation = rot,
                RefreshRateHz = RefreshRateHz
            };
        }
    }

    private void RecordSuccess(long nowTicks, double elapsedMs)
    {
        lock (_diagLock)
        {
            _presentedFrames++;
            _consecutiveFailures = 0;
            _lastUploadMs = elapsedMs;
            _avgUploadMs = _avgUploadMs <= 0 ? elapsedMs : (_avgUploadMs * 0.8 + elapsedMs * 0.2);
            _lastStatus = nameof(DisplayPresentStatus.Success);

            _presentTicksWindow.Enqueue(nowTicks);
            PruneFpsWindowLocked(nowTicks);
        }
    }

    private void RecordFailure(string status, double elapsedMs, bool forceFatal, out bool shouldInvalidate, out string reason)
    {
        shouldInvalidate = false;
        reason = "";

        lock (_diagLock)
        {
            _failedFrames++;
            _consecutiveFailures++;
            _lastUploadMs = elapsedMs;
            _avgUploadMs = _avgUploadMs <= 0 ? elapsedMs : (_avgUploadMs * 0.8 + elapsedMs * 0.2);
            _lastStatus = status;

            if (forceFatal)
            {
                shouldInvalidate = true;
                reason = $"硬件独占显示扫描输出异常（{status}），会话已停止。";
            }
            else if (_consecutiveFailures >= MaxConsecutivePresentFailures)
            {
                shouldInvalidate = true;
                reason = $"硬件独占显示连续失败 {MaxConsecutivePresentFailures} 次（最近状态：{status}），会话已自动停止。";
            }
        }
    }

    private void PruneFpsWindowLocked(long nowTicks)
    {
        long windowTicks = Stopwatch.Frequency * 2; // 2-second rolling window for smooth low-FPS accuracy
        while (_presentTicksWindow.Count > 0 && (nowTicks - _presentTicksWindow.Peek()) > windowTicks)
        {
            _presentTicksWindow.Dequeue();
        }
    }

    private double ComputeActualFpsLocked(long nowTicks)
    {
        int count = _presentTicksWindow.Count;
        if (count == 0) return 0;

        double elapsedSinceStart = (nowTicks - _sessionStartTicks) / (double)Stopwatch.Frequency;
        double denom = Math.Clamp(elapsedSinceStart, 0.5, 2.0);
        return count / denom;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;

        // Detach event handlers BEFORE calling Stop() and outside _presentLock to prevent deadlock.
        try { _manager.PathsFailedOrInvalidated -= Manager_PathsFailedOrInvalidated; } catch { }
        try { _manager.Disabled -= Manager_Disabled; } catch { }
        try { _manager.Stop(); } catch { }

        lock (_presentLock)
        {
            try { _presenter.Dispose(); } catch { }
            try { _manager.ReleaseTarget(_target); } catch { }
            try { _manager.Dispose(); } catch { }
        }
    }
}
