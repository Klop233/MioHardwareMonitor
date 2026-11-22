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
                .ToDictionary(a => a.LuidKey, a => a.Name, StringComparer.OrdinalIgnoreCase);

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

                bool isSpecialized = t.UsageKind is DisplayMonitorUsageKind.SpecialPurpose or DisplayMonitorUsageKind.HeadMounted;
                string targetKey = $"{(uint)luid.HighPart:X8}_{luid.LowPart:X8}:{t.AdapterRelativeId}";
                string resPart = w > 0 && h > 0 ? $" {w}×{h}" : "";
                string gpuPart = string.IsNullOrWhiteSpace(adapterName) ? "" : $" · {adapterName}";
                string badge = isSpecialized ? "[已脱离桌面 · 可硬件独占]" : "[桌面托管 · 需先从桌面删除]";

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
            .OrderByDescending(x => x.IsSpecialized)
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
                target = connected.FirstOrDefault(t =>
                    string.Equals(BuildTargetKey(t), preferredTargetKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.StableMonitorId, preferredTargetKey, StringComparison.OrdinalIgnoreCase));
            }
            target ??= connected.FirstOrDefault(t => t.UsageKind is DisplayMonitorUsageKind.SpecialPurpose or DisplayMonitorUsageKind.HeadMounted);

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
            if (modes.Count == 0)
            {
                modes = path.FindModes(DisplayModeQueryOptions.None);
            }

            var bestMode = SelectBestMode(modes);
            if (bestMode == null)
            {
                errorMessage = "未找到该显示器支持的 BGRA 硬件扫描输出模式。";
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

            var descriptor = new DirectDisplayTargetDescriptor
            {
                TargetKey = BuildTargetKey(target),
                StableMonitorId = target.StableMonitorId ?? "",
                DisplayName = dispName,
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

    private static DisplayModeInfo? SelectBestMode(IReadOnlyList<DisplayModeInfo> modes)
    {
        if (modes == null || modes.Count == 0) return null;

        var bgraModes = modes
            .Where(m => m.SourcePixelFormat == DirectXPixelFormat.B8G8R8A8UIntNormalized && !m.IsStereo && !m.IsInterlaced)
            .ToList();

        var pool = bgraModes.Count > 0 ? bgraModes : modes.Where(m => !m.IsStereo).ToList();
        if (pool.Count == 0) pool = modes.ToList();

        return pool
            .OrderByDescending(m => (long)m.SourceResolution.Width * m.SourceResolution.Height)
            .ThenByDescending(m => m.PresentationRate.VerticalSyncRate.Denominator > 0
                ? (double)m.PresentationRate.VerticalSyncRate.Numerator / m.PresentationRate.VerticalSyncRate.Denominator
                : 0)
            .FirstOrDefault();
    }

    private static string BuildTargetKey(DisplayTarget t) =>
        $"{(uint)t.Adapter.Id.HighPart:X8}_{t.Adapter.Id.LowPart:X8}:{t.AdapterRelativeId}";
}

public sealed class DirectDisplaySession : IDisposable
{
    private readonly object _presentLock = new();
    private readonly Windows.Devices.Display.Core.DisplayManager _manager;
    private readonly DisplayTarget _target;
    private readonly DisplayDevice _device;
    private readonly DisplaySource _source;
    private readonly DisplaySurface[] _primaries;
    private readonly DisplayScanout[] _scanouts;
    private readonly DisplayTaskPool _taskPool;
    private readonly D3D11DirectPresenter _presenter;

    private int _backIndex;
    private bool _disposed;

    public DirectDisplayTargetDescriptor Descriptor { get; }
    public int Width => Descriptor.Width;
    public int Height => Descriptor.Height;
    public int RefreshRateHz => Descriptor.RefreshRateHz;

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
        if (!_disposed)
            SessionInvalidated?.Invoke("硬件独占显示路径已失效（显示器可能已断开或重新回到桌面）。");
    }

    private void Manager_Disabled(Windows.Devices.Display.Core.DisplayManager sender, DisplayManagerDisabledEventArgs args)
    {
        if (!_disposed)
            SessionInvalidated?.Invoke("系统已停用该硬件独占显示会话。");
    }

    public bool PresentBgraFrame(IntPtr pixels, int stride, int width, int height)
    {
        lock (_presentLock)
        {
            if (_disposed) return false;
            try
            {
                int idx = _backIndex;
                _presenter.UploadBgraFrame(pixels, stride, width, height, idx);

                var task = _taskPool.CreateTask();
                task.SetScanout(_scanouts[idx]);
                var result = _taskPool.TryExecuteTask(task);

                if (result.PresentStatus is DisplayPresentStatus.SourceStatusPreventedPresent)
                {
                    return false;
                }

                _backIndex = 1 - idx;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_presentLock)
        {
            if (_disposed) return;
            _disposed = true;

            try { _manager.PathsFailedOrInvalidated -= Manager_PathsFailedOrInvalidated; } catch { }
            try { _manager.Disabled -= Manager_Disabled; } catch { }
            try { _manager.Stop(); } catch { }
            try { _presenter.Dispose(); } catch { }
            try { _manager.ReleaseTarget(_target); } catch { }
            try { _manager.Dispose(); } catch { }
        }
    }
}
