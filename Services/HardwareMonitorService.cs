using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using SideScreenHUD.Models;
using SideScreenHUD.Native;

namespace SideScreenHUD.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly object _sync = new();
    private Computer? _computer;
    private bool _lhmReady;

    // Static hardware info
    private string _cpuName = "CPU";
    private int _physicalCores = Environment.ProcessorCount;
    private int _logicalProcessors = Environment.ProcessorCount;
    private double _baseClockGhz = 3.0;
    private int? _ramSpeedMhz;
    private readonly bool _isElevated;
    private List<DxgiAdapterInfo> _dxgiAdapters = new();

    // CPU GetSystemTimes delta state
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuTimes;

    // PDH counters
    private PerformanceCounter? _cpuPerfPercentCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private readonly List<PerformanceCounter> _coreCounters = new();
    private PerformanceCounterCategory? _gpuEngineCategory;
    private PerformanceCounterCategory? _gpuMemCategory;
    private Dictionary<string, CounterSample> _prevGpuEngineSamples = new(StringComparer.OrdinalIgnoreCase);

    // Network delta state
    private readonly Dictionary<string, (long Rx, long Tx)> _prevNetBytes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _prevNetTimeUtc = DateTime.MinValue;
    private long _sessionRxBytes;
    private long _sessionTxBytes;

    // Drive cache (refresh every 5s)
    private DateTime _lastDriveRefreshUtc = DateTime.MinValue;
    private List<DriveItem> _cachedDrives = new();

    // Process CPU delta cache (refresh every 2s)
    private DateTime _lastProcSampleUtc = DateTime.MinValue;
    private Dictionary<int, TimeSpan> _prevProcCpu = new();
    private List<ProcessItem> _cachedTopProcs = new();

    private bool _disposed;

    public HardwareMonitorService()
    {
        _isElevated = CheckIsElevated();
        InitializeStaticInfo();
        InitializeCounters();
        InitializeLibreHardwareMonitor();
    }

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

    private void InitializeStaticInfo()
    {
        // DXGI Adapters (accurate 64-bit VRAM + LUID)
        _dxgiAdapters = DxgiHelper.EnumerateAdapters();

        // CPU info via WMI
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _cpuName = CleanHardwareName(name);
                }
                if (obj["NumberOfCores"] != null) _physicalCores = Convert.ToInt32(obj["NumberOfCores"]);
                if (obj["NumberOfLogicalProcessors"] != null) _logicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                if (obj["MaxClockSpeed"] != null) _baseClockGhz = Convert.ToDouble(obj["MaxClockSpeed"]) / 1000.0;
                break;
            }
        }
        catch { }

        // RAM speed via WMI (optional)
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            foreach (var obj in searcher.Get())
            {
                var cfg = obj["ConfiguredClockSpeed"] ?? obj["Speed"];
                if (cfg != null)
                {
                    int mhz = Convert.ToInt32(cfg);
                    if (mhz > 0)
                    {
                        _ramSpeedMhz = mhz;
                        break;
                    }
                }
            }
        }
        catch { }
    }

    private void InitializeCounters()
    {
        // Seed GetSystemTimes
        if (Win32Interop.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _prevIdle = idle.ToUInt64();
            _prevKernel = kernel.ToUInt64();
            _prevUser = user.ToUInt64();
            _hasCpuTimes = true;
        }

        // CPU % Processor Performance (for live frequency estimation when MSR unavailable)
        try
        {
            _cpuPerfPercentCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            _cpuPerfPercentCounter.NextValue();
        }
        catch
        {
            _cpuPerfPercentCounter = null;
        }

        // Per-core counters
        try
        {
            var cat = new PerformanceCounterCategory("Processor Information");
            var instances = cat.GetInstanceNames()
                .Where(n => !n.Equals("_Total", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(ParseCoreSortKey)
                .ToList();

            foreach (var inst in instances)
            {
                try
                {
                    var pc = new PerformanceCounter("Processor Information", "% Processor Time", inst, true);
                    pc.NextValue();
                    _coreCounters.Add(pc);
                }
                catch { }
            }
        }
        catch
        {
            try
            {
                var cat = new PerformanceCounterCategory("Processor");
                var instances = cat.GetInstanceNames()
                    .Where(n => !n.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => int.TryParse(n, out var i) ? i : 999)
                    .ToList();

                foreach (var inst in instances)
                {
                    try
                    {
                        var pc = new PerformanceCounter("Processor", "% Processor Time", inst, true);
                        pc.NextValue();
                        _coreCounters.Add(pc);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Disk Read/Write
        try
        {
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
            _diskReadCounter.NextValue();
            _diskWriteCounter.NextValue();
        }
        catch
        {
            _diskReadCounter = null;
            _diskWriteCounter = null;
        }

        // GPU PDH Categories (works for AMD, NVIDIA, Intel on Win10/11)
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
                _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
            if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                _gpuMemCategory = new PerformanceCounterCategory("GPU Adapter Memory");
        }
        catch { }
    }

    private void InitializeLibreHardwareMonitor()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false
            };
            _computer.Open();
            _lhmReady = true;
        }
        catch
        {
            _lhmReady = false;
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    public TelemetrySnapshot Collect()
    {
        lock (_sync)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = new TelemetrySnapshot
            {
                TimestampMs = nowMs,
                HostName = Environment.MachineName,
                UptimeSeconds = Environment.TickCount64 / 1000,
                IsElevated = _isElevated
            };

            // 1. Update LibreHardwareMonitor safely
            var lhmCpu = new LhmCpuData();
            var lhmGpus = new List<LhmGpuData>();
            if (_lhmReady && _computer != null)
            {
                ReadLibreHardwareMonitor(lhmCpu, lhmGpus);
            }

            // 2. CPU
            snapshot.Cpu = CollectCpu(lhmCpu);

            // 3. Memory
            snapshot.Memory = CollectMemory();

            // 4. GPU (DXGI + PDH + LibreHardwareMonitor fusion)
            var (primaryGpu, allGpus) = CollectGpu(lhmGpus);
            snapshot.Gpu = primaryGpu;
            snapshot.AllGpus = allGpus;

            // 5. Network
            snapshot.Network = CollectNetwork();

            // 6. Disk
            snapshot.Disk = CollectDisk();

            // 7. Top Processes (sampled every 2s)
            snapshot.TopProcesses = CollectTopProcesses();

            return snapshot;
        }
    }

    private sealed class LhmCpuData
    {
        public double? TempC;
        public double? PowerW;
        public double? AvgClockGhz;
        public double? TotalLoad;
    }

    private sealed class LhmGpuData
    {
        public string Name = "";
        public double? LoadPercent;
        public double? TempC;
        public double? HotspotC;
        public double? FanRpm;
        public double? FanPercent;
        public double? CoreClockMhz;
        public double? MemoryClockMhz;
        public double? PowerW;
        public double? VramUsedGb;
        public double? VramTotalGb;
    }

    private void ReadLibreHardwareMonitor(LhmCpuData cpuOut, List<LhmGpuData> gpusOut)
    {
        if (_computer == null) return;
        try
        {
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                    {
                        try { sub.Update(); } catch { }
                    }

                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        var clocks = new List<double>();
                        double? pkgTemp = null;
                        double? fallbackTemp = null;

                        foreach (var s in hw.Sensors)
                        {
                            if (!s.Value.HasValue || float.IsNaN(s.Value.Value)) continue;
                            double val = s.Value.Value;
                            var name = s.Name ?? "";

                            switch (s.SensorType)
                            {
                                case SensorType.Temperature:
                                    if (val > 5 && val < 125)
                                    {
                                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                            || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                                            || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
                                            || name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                                        {
                                            pkgTemp ??= val;
                                        }
                                        fallbackTemp ??= val;
                                    }
                                    break;
                                case SensorType.Power:
                                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) && val >= 0)
                                        cpuOut.PowerW = Math.Round(val, 1);
                                    break;
                                case SensorType.Clock:
                                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) && val > 200)
                                        clocks.Add(val);
                                    break;
                                case SensorType.Load:
                                    if (name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                                        cpuOut.TotalLoad = val;
                                    break;
                            }
                        }

                        cpuOut.TempC = pkgTemp ?? fallbackTemp;
                        if (clocks.Count > 0)
                        {
                            cpuOut.AvgClockGhz = Math.Round(clocks.Max() / 1000.0, 2);
                        }
                    }
                    else if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                    {
                        var g = new LhmGpuData { Name = hw.Name ?? "" };
                        double? coreLoad = null;
                        double? d3dLoad = null;

                        foreach (var s in hw.Sensors)
                        {
                            if (!s.Value.HasValue || float.IsNaN(s.Value.Value)) continue;
                            double val = s.Value.Value;
                            var name = s.Name ?? "";

                            switch (s.SensorType)
                            {
                                case SensorType.Load:
                                    if (name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                                        coreLoad = val;
                                    else if (name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase))
                                        d3dLoad = val;
                                    break;
                                case SensorType.Temperature:
                                    if (val > 0 && val < 130)
                                    {
                                        if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                                            g.HotspotC = Math.Round(val, 1);
                                        else if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) || name.Contains("VR", StringComparison.OrdinalIgnoreCase) == false)
                                            g.TempC ??= Math.Round(val, 1);
                                    }
                                    break;
                                case SensorType.Fan:
                                    if (val >= 0) g.FanRpm ??= Math.Round(val, 0);
                                    break;
                                case SensorType.Control:
                                    if (name.Contains("Fan", StringComparison.OrdinalIgnoreCase) && val >= 0)
                                        g.FanPercent ??= Math.Round(val, 0);
                                    break;
                                case SensorType.Clock:
                                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) && val > 0)
                                        g.CoreClockMhz = Math.Round(val, 0);
                                    else if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && val > 0)
                                        g.MemoryClockMhz = Math.Round(val, 0);
                                    break;
                                case SensorType.Power:
                                    if ((name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("Total", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("PPT", StringComparison.OrdinalIgnoreCase)) && val >= 0)
                                    {
                                        g.PowerW ??= Math.Round(val, 1);
                                    }
                                    break;
                                case SensorType.SmallData:
                                    // LibreHardwareMonitor reports GPU Memory in MB under SmallData
                                    if (name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase) || name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                                        g.VramUsedGb ??= Math.Round(val / 1024.0, 2);
                                    else if (name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                                        g.VramTotalGb ??= Math.Round(val / 1024.0, 2);
                                    break;
                            }
                        }

                        g.LoadPercent = coreLoad ?? d3dLoad;
                        gpusOut.Add(g);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private CpuMetrics CollectCpu(LhmCpuData lhmCpu)
    {
        double totalUsage = 0;

        if (Win32Interop.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idle = idleFt.ToUInt64();
            ulong kernel = kernelFt.ToUInt64();
            ulong user = userFt.ToUInt64();

            if (_hasCpuTimes)
            {
                ulong idleDelta = idle - _prevIdle;
                ulong kernelDelta = kernel - _prevKernel;
                ulong userDelta = user - _prevUser;
                ulong totalDelta = kernelDelta + userDelta;
                if (totalDelta > 0)
                {
                    totalUsage = Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
                }
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuTimes = true;
        }
        else if (lhmCpu.TotalLoad.HasValue)
        {
            totalUsage = Math.Clamp(lhmCpu.TotalLoad.Value, 0, 100);
        }

        // Per-core loads
        var coreUsages = new double[_coreCounters.Count];
        for (int i = 0; i < _coreCounters.Count; i++)
        {
            try
            {
                coreUsages[i] = Math.Round(Math.Clamp(_coreCounters[i].NextValue(), 0, 100), 1);
            }
            catch
            {
                coreUsages[i] = 0;
            }
        }

        // Live CPU clock
        double clockGhz = _baseClockGhz;
        if (lhmCpu.AvgClockGhz.HasValue && lhmCpu.AvgClockGhz.Value > 0.4)
        {
            clockGhz = lhmCpu.AvgClockGhz.Value;
        }
        else if (_cpuPerfPercentCounter != null)
        {
            try
            {
                double perfPct = _cpuPerfPercentCounter.NextValue();
                if (perfPct > 10 && perfPct < 400)
                {
                    clockGhz = Math.Round(_baseClockGhz * perfPct / 100.0, 2);
                }
            }
            catch { }
        }

        return new CpuMetrics
        {
            Name = _cpuName,
            PhysicalCores = _physicalCores,
            LogicalProcessors = _logicalProcessors,
            TotalUsagePercent = Math.Round(totalUsage, 1),
            CurrentClockGhz = Math.Round(clockGhz, 2),
            BaseClockGhz = Math.Round(_baseClockGhz, 2),
            TemperatureC = lhmCpu.TempC.HasValue ? Math.Round(lhmCpu.TempC.Value, 1) : null,
            PowerWatts = lhmCpu.PowerW,
            CoreUsages = coreUsages
        };
    }

    private MemoryMetrics CollectMemory()
    {
        var mem = new Win32Interop.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX>()
        };

        if (Win32Interop.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double availGb = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGb = Math.Max(0, totalGb - availGb);
            double pct = Math.Clamp(usedGb / totalGb * 100.0, 0, 100);

            return new MemoryMetrics
            {
                TotalGb = Math.Round(totalGb, 1),
                UsedGb = Math.Round(usedGb, 1),
                AvailableGb = Math.Round(availGb, 1),
                UsagePercent = Math.Round(pct, 1),
                SpeedMhz = _ramSpeedMhz
            };
        }

        return new MemoryMetrics();
    }

    private (GpuMetrics Primary, List<GpuSummary> All) CollectGpu(List<LhmGpuData> lhmGpus)
    {
        // 1. Read PDH GPU Engine utilization per LUID & EngineType
        var pdhUsageByLuid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var pdhDedicatedByLuid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (_gpuEngineCategory != null)
        {
            try
            {
                var catData = _gpuEngineCategory.ReadCategory();
                if (catData.Contains("Utilization Percentage"))
                {
                    var instances = catData["Utilization Percentage"];
                    var nextSamples = new Dictionary<string, CounterSample>(instances.Count, StringComparer.OrdinalIgnoreCase);
                    // Group by (luid, engtype) -> sum across PIDs, then max across engine types per LUID (Task Manager formula)
                    var engineSums = new Dictionary<(string Luid, string EngType), double>();

                    foreach (InstanceData inst in instances.Values)
                    {
                        var instName = inst.InstanceName;
                        var sample = inst.Sample;
                        nextSamples[instName] = sample;

                        if (_prevGpuEngineSamples.TryGetValue(instName, out var prevSample))
                        {
                            float val = CounterSample.Calculate(prevSample, sample);
                            if (val > 0 && !float.IsNaN(val))
                            {
                                var luid = ExtractLuidKey(instName);
                                var eng = ExtractEngType(instName);
                                if (!string.IsNullOrEmpty(luid))
                                {
                                    var key = (luid, eng);
                                    engineSums[key] = engineSums.TryGetValue(key, out var cur) ? cur + val : val;
                                }
                            }
                        }
                    }

                    _prevGpuEngineSamples = nextSamples;

                    foreach (var kv in engineSums)
                    {
                        double clamped = Math.Clamp(kv.Value, 0, 100);
                        if (!pdhUsageByLuid.TryGetValue(kv.Key.Luid, out var curMax) || clamped > curMax)
                        {
                            pdhUsageByLuid[kv.Key.Luid] = clamped;
                        }
                    }
                }
            }
            catch { }
        }

        if (_gpuMemCategory != null)
        {
            try
            {
                var catData = _gpuMemCategory.ReadCategory();
                if (catData.Contains("Dedicated Usage"))
                {
                    var instances = catData["Dedicated Usage"];
                    foreach (InstanceData inst in instances.Values)
                    {
                        var luid = ExtractLuidKey(inst.InstanceName);
                        if (!string.IsNullOrEmpty(luid))
                        {
                            double gb = inst.RawValue / (1024.0 * 1024.0 * 1024.0);
                            if (!pdhDedicatedByLuid.TryGetValue(luid, out var existing) || gb > existing)
                            {
                                pdhDedicatedByLuid[luid] = gb;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 2. Fuse DXGI + PDH + LibreHardwareMonitor (deduplicate virtual-display mirrored adapters)
        var summaries = new List<GpuSummary>();
        GpuMetrics? primary = null;

        var groupedAdapters = _dxgiAdapters
            .GroupBy(a => $"{a.VendorId:x4}:{a.DeviceId:x4}:{CleanHardwareName(a.Name)}", StringComparer.OrdinalIgnoreCase);

        foreach (var grp in groupedAdapters)
        {
            // Pick the LUID instance with highest real PDH activity/VRAM usage
            var adapter = grp
                .OrderByDescending(a => pdhDedicatedByLuid.TryGetValue(a.LuidKey, out var v) ? v : 0)
                .ThenByDescending(a => pdhUsageByLuid.TryGetValue(a.LuidKey, out var u) ? u : 0)
                .First();

            double pdhLoad = grp.Max(a => pdhUsageByLuid.TryGetValue(a.LuidKey, out var u) ? u : 0);
            double pdhVramUsed = grp.Max(a => pdhDedicatedByLuid.TryGetValue(a.LuidKey, out var v) ? v : 0);

            var matchedLhm = MatchLhmGpu(adapter.Name, lhmGpus);

            double load = matchedLhm?.LoadPercent is > 0 ? matchedLhm.LoadPercent.Value : pdhLoad;
            if (pdhLoad > load && matchedLhm?.LoadPercent is null or 0) load = pdhLoad;
            load = Math.Clamp(load, 0, 100);

            double vramTotal = adapter.DedicatedVideoMemoryGb > 0.1
                ? adapter.DedicatedVideoMemoryGb
                : (matchedLhm?.VramTotalGb ?? 0);

            double vramUsed = pdhVramUsed > 0.01 ? pdhVramUsed : (matchedLhm?.VramUsedGb ?? 0);
            if (vramTotal > 0 && vramUsed > vramTotal) vramUsed = vramTotal;

            summaries.Add(new GpuSummary
            {
                Name = CleanHardwareName(adapter.Name),
                LuidKey = adapter.LuidKey,
                UsagePercent = Math.Round(load, 1),
                VramUsedGb = Math.Round(vramUsed, 2),
                VramTotalGb = Math.Round(vramTotal, 1),
                TemperatureC = matchedLhm?.TempC,
                IsDiscrete = adapter.IsLikelyDiscrete
            });

            if (primary == null)
            {
                primary = new GpuMetrics
                {
                    Name = CleanHardwareName(adapter.Name),
                    UsagePercent = Math.Round(load, 1),
                    VramUsedGb = Math.Round(vramUsed, 2),
                    VramTotalGb = Math.Round(vramTotal, 1),
                    VramUsagePercent = vramTotal > 0 ? Math.Round(Math.Clamp(vramUsed / vramTotal * 100.0, 0, 100), 1) : 0,
                    TemperatureC = matchedLhm?.TempC,
                    HotspotTempC = matchedLhm?.HotspotC,
                    FanRpm = matchedLhm?.FanRpm,
                    FanPercent = matchedLhm?.FanPercent,
                    CoreClockMhz = matchedLhm?.CoreClockMhz,
                    MemoryClockMhz = matchedLhm?.MemoryClockMhz,
                    PowerWatts = matchedLhm?.PowerW
                };
            }
        }

        // Fallback if DXGI found nothing but LHM found a GPU
        if (primary == null && lhmGpus.Count > 0)
        {
            var g = lhmGpus[0];
            double total = g.VramTotalGb ?? 0;
            double used = g.VramUsedGb ?? 0;
            primary = new GpuMetrics
            {
                Name = CleanHardwareName(g.Name),
                UsagePercent = Math.Round(g.LoadPercent ?? 0, 1),
                VramUsedGb = Math.Round(used, 2),
                VramTotalGb = Math.Round(total, 1),
                VramUsagePercent = total > 0 ? Math.Round(used / total * 100.0, 1) : 0,
                TemperatureC = g.TempC,
                HotspotTempC = g.HotspotC,
                FanRpm = g.FanRpm,
                FanPercent = g.FanPercent,
                CoreClockMhz = g.CoreClockMhz,
                MemoryClockMhz = g.MemoryClockMhz,
                PowerWatts = g.PowerW
            };
        }

        return (primary ?? new GpuMetrics(), summaries);
    }

    private NetworkMetrics CollectNetwork()
    {
        var nowUtc = DateTime.UtcNow;
        double elapsedSec = _prevNetTimeUtc == DateTime.MinValue ? 0 : (nowUtc - _prevNetTimeUtc).TotalSeconds;
        double totalRxRate = 0;
        double totalTxRate = 0;
        string busiestAdapter = "Network";
        double busiestRate = -1;

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !nic.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var stats = nic.GetIPStatistics();
                    long rx = stats.BytesReceived;
                    long tx = stats.BytesSent;

                    if (elapsedSec > 0.05 && _prevNetBytes.TryGetValue(nic.Id, out var prev))
                    {
                        long dRx = Math.Max(0, rx - prev.Rx);
                        long dTx = Math.Max(0, tx - prev.Tx);
                        double rxRate = dRx / elapsedSec;
                        double txRate = dTx / elapsedSec;

                        totalRxRate += rxRate;
                        totalTxRate += txRate;
                        _sessionRxBytes += dRx;
                        _sessionTxBytes += dTx;

                        if (rxRate + txRate > busiestRate)
                        {
                            busiestRate = rxRate + txRate;
                            busiestAdapter = CleanHardwareName(nic.Description);
                        }
                    }
                    else if (busiestRate < 0)
                    {
                        busiestAdapter = CleanHardwareName(nic.Description);
                    }

                    _prevNetBytes[nic.Id] = (rx, tx);
                }
                catch { }
            }
        }
        catch { }

        _prevNetTimeUtc = nowUtc;

        return new NetworkMetrics
        {
            AdapterName = busiestAdapter,
            DownloadBytesPerSec = Math.Round(totalRxRate, 0),
            UploadBytesPerSec = Math.Round(totalTxRate, 0),
            TotalDownloadedMb = Math.Round(_sessionRxBytes / (1024.0 * 1024.0), 1),
            TotalUploadedMb = Math.Round(_sessionTxBytes / (1024.0 * 1024.0), 1)
        };
    }

    private DiskMetrics CollectDisk()
    {
        double readRate = 0;
        double writeRate = 0;

        try { readRate = _diskReadCounter?.NextValue() ?? 0; } catch { }
        try { writeRate = _diskWriteCounter?.NextValue() ?? 0; } catch { }

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastDriveRefreshUtc).TotalSeconds >= 5.0 || _cachedDrives.Count == 0)
        {
            var drives = new List<DriveItem>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    double totalGb = d.TotalSize / (1024.0 * 1024.0 * 1024.0);
                    double freeGb = d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                    double usedGb = Math.Max(0, totalGb - freeGb);
                    drives.Add(new DriveItem
                    {
                        Name = d.Name.TrimEnd('\\'),
                        Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel,
                        TotalGb = Math.Round(totalGb, 0),
                        UsedGb = Math.Round(usedGb, 0),
                        FreeGb = Math.Round(freeGb, 0),
                        UsagePercent = totalGb > 0 ? Math.Round(usedGb / totalGb * 100.0, 1) : 0
                    });
                }
            }
            catch { }

            _cachedDrives = drives;
            _lastDriveRefreshUtc = nowUtc;
        }

        return new DiskMetrics
        {
            ReadBytesPerSec = Math.Round(Math.Max(0, readRate), 0),
            WriteBytesPerSec = Math.Round(Math.Max(0, writeRate), 0),
            Drives = _cachedDrives
        };
    }

    private List<ProcessItem> CollectTopProcesses()
    {
        var nowUtc = DateTime.UtcNow;
        if (_prevProcCpu.Count > 0 && (nowUtc - _lastProcSampleUtc).TotalSeconds < 1.8 && _cachedTopProcs.Count > 0 && _cachedTopProcs.Any(p => p.CpuPercent > 0))
        {
            return _cachedTopProcs;
        }

        double elapsedMs = _lastProcSampleUtc == DateTime.MinValue ? 0 : (nowUtc - _lastProcSampleUtc).TotalMilliseconds;
        var nextProcCpu = new Dictionary<int, TimeSpan>();
        var items = new List<ProcessItem>();

        try
        {
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id <= 4) continue; // Skip System Idle / System
                    var cpuTime = p.TotalProcessorTime;
                    nextProcCpu[p.Id] = cpuTime;

                    double cpuPct = 0;
                    if (elapsedMs > 100 && _prevProcCpu.TryGetValue(p.Id, out var prevTime))
                    {
                        double deltaMs = (cpuTime - prevTime).TotalMilliseconds;
                        cpuPct = Math.Clamp(deltaMs / (elapsedMs * Math.Max(1, _logicalProcessors)) * 100.0, 0, 100);
                    }

                    double memMb = p.WorkingSet64 / (1024.0 * 1024.0);
                    if (cpuPct >= 0.1 || memMb >= 80)
                    {
                        items.Add(new ProcessItem
                        {
                            Pid = p.Id,
                            Name = p.ProcessName,
                            CpuPercent = Math.Round(cpuPct, 1),
                            MemoryMb = Math.Round(memMb, 0)
                        });
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        _prevProcCpu = nextProcCpu;
        _lastProcSampleUtc = nowUtc;

        _cachedTopProcs = items
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.MemoryMb)
            .Take(6)
            .ToList();

        return _cachedTopProcs;
    }

    private static LhmGpuData? MatchLhmGpu(string dxgiName, List<LhmGpuData> lhmGpus)
    {
        if (lhmGpus.Count == 0) return null;
        var cleanDxgi = CleanHardwareName(dxgiName);

        var exact = lhmGpus.FirstOrDefault(g =>
            string.Equals(CleanHardwareName(g.Name), cleanDxgi, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Token overlap match (e.g., "RX 7800 XT", "RTX 4090", "UHD Graphics")
        var tokens = cleanDxgi.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToArray();

        return lhmGpus
            .Select(g => new
            {
                Gpu = g,
                Score = tokens.Count(t => g.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Gpu)
            .FirstOrDefault();
    }

    private static string ExtractLuidKey(string instanceName)
    {
        int idx = instanceName.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        // Format: luid_0x00000000_0x00010eca (length 26)
        int end = idx + 26;
        if (end <= instanceName.Length)
            return instanceName.Substring(idx, 26).ToLowerInvariant();
        return "";
    }

    private static string ExtractEngType(string instanceName)
    {
        int idx = instanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "other";
        return instanceName.Substring(idx + 8).ToLowerInvariant();
    }

    private static (int Group, int Core) ParseCoreSortKey(string inst)
    {
        var parts = inst.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int c))
            return (g, c);
        return (0, int.TryParse(inst, out int single) ? single : 999);
    }

    private static string CleanHardwareName(string raw)
    {
        return raw
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            _cpuPerfPercentCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            foreach (var c in _coreCounters) c.Dispose();
            _coreCounters.Clear();
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
