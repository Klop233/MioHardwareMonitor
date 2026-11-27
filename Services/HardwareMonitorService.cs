using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using SideScreenHUD.Models;
using SideScreenHUD.Native;

namespace SideScreenHUD.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly string[] VirtualAdapterKeywords =
    {
        "vEthernet", "Hyper-V", "VMware", "VirtualBox", "Wintun", "WireGuard",
        "Tailscale", "ZeroTier", "TAP-Windows", "Clash", "Mihomo", "Meta",
        "Sing-box", "Npcap", "Virtual", "Loopback", "Pseudo", "WAN Miniport"
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    private readonly object _sync = new();
    private Computer? _computer;
    private bool _lhmReady;

    // Static hardware info (supports multi-socket / multi-CPU systems)
    private sealed class StaticCpuSocket
    {
        public int Index;
        public string DeviceId = "";
        public string SocketDesignation = "";
        public string Name = "CPU";
        public int PhysicalCores;
        public int LogicalProcessors;
        public double BaseClockGhz = 3.0;
    }

    private readonly List<StaticCpuSocket> _cpuSockets = new();
    private string _cpuName = "CPU";
    private int _physicalCores = Environment.ProcessorCount;
    private int _logicalProcessors = Environment.ProcessorCount;
    private double _baseClockGhz = 3.0;
    private int? _ramSpeedMhz;
    private readonly bool _isElevated;
    private List<DxgiAdapterInfo> _dxgiAdapters = new();
    private readonly Dictionary<string, string> _driveLetterToDiskModel = new(StringComparer.OrdinalIgnoreCase);

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

    // Process CPU delta cache
    private DateTime _lastProcSampleUtc = DateTime.MinValue;
    private Dictionary<int, TimeSpan> _prevProcCpu = new();
    private List<ProcessItem> _cachedTopProcs = new();

    // Self-footprint delta cache (host + WebView2 child process tree)
    private DateTime _prevSelfSampleUtc = DateTime.MinValue;
    private Dictionary<int, TimeSpan> _prevSelfProcCpu = new();

    // Heavy-sensor throttle (LibreHardwareMonitor, GPU PDH, process scan). Cheap counters
    // (GetSystemTimes, PDH processor, memory, NIC, disk) still update on every tick.
    private DateTime _lastHeavySampleUtc = DateTime.MinValue;
    private DateTime _lastSlowSampleUtc = DateTime.MinValue; // 15s cadence for SMART storage sensors
    private List<LhmCpuData> _cachedLhmCpus = new();
    private List<LhmGpuData> _cachedLhmGpus = new();
    private string _cachedMotherboardName = "";
    private List<FanSensorItem> _cachedFans = new();
    private List<TempSensorItem> _cachedMoboTemps = new();
    private List<StorageTempItem> _cachedStorageTemps = new();
    private LhmBatteryData? _cachedLhmBattery;
    private GpuMetrics? _cachedGpu;
    private List<GpuSummary> _cachedGpuSummaries = new();
    private List<ProcessItem> _cachedTopProcsHeavy = new();

    // Session Min/Avg/Max peak tracker
    private sealed class RunningRange
    {
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private double _sum;
        private int _count;

        public void Add(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return;
            double v = value.Value;
            if (v < _min) _min = v;
            if (v > _max) _max = v;
            _sum += v;
            _count++;
        }

        public MetricRange ToMetricRange(int decimals = 1)
        {
            if (_count <= 0) return new MetricRange();
            return new MetricRange
            {
                Min = Math.Round(_min, decimals),
                Avg = Math.Round(_sum / _count, decimals),
                Max = Math.Round(_max, decimals)
            };
        }

        public void Reset()
        {
            _min = double.MaxValue;
            _max = double.MinValue;
            _sum = 0;
            _count = 0;
        }
    }

    private long _sessionStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private int _sessionSampleCount;
    private readonly RunningRange _statCpuUsage = new();
    private readonly RunningRange _statCpuTemp = new();
    private readonly RunningRange _statCpuPower = new();
    private readonly RunningRange _statCpuClock = new();
    private readonly RunningRange _statGpuUsage = new();
    private readonly RunningRange _statGpuTemp = new();
    private readonly RunningRange _statGpuHotspot = new();
    private readonly RunningRange _statGpuPower = new();
    private readonly RunningRange _statRamUsage = new();
    private double _statNetDownPeak;
    private double _statNetUpPeak;

    // Thermal & load alert debounce/hysteresis/cooldown state
    private sealed class AlertRuleState
    {
        public int ConsecutiveWarnTicks;
        public int ConsecutiveCritTicks;
        public string ActiveSeverity = "";
        public DateTime LastTrayNotifiedUtc = DateTime.MinValue;
        public string LastTraySeverity = "";
    }

    private readonly Dictionary<string, AlertRuleState> _alertStates = new(StringComparer.OrdinalIgnoreCase);

    private int _heavySampleIntervalMs = 3000;

    /// <summary>Minimum interval between heavy sensor reads (ms). Thread-safe via Volatile.</summary>
    public int HeavySampleIntervalMs
    {
        get => Volatile.Read(ref _heavySampleIntervalMs);
        set => Volatile.Write(ref _heavySampleIntervalMs, Math.Clamp(value, 500, 30000));
    }

    public bool IsElevated => _isElevated;

    private volatile bool _disposed;

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

        // CPU info via WMI (enumerate ALL physical CPU sockets for multi-CPU servers/workstations)
        _cpuSockets.Clear();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            using var results = searcher.Get();
            var rawSockets = new List<StaticCpuSocket>();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString()?.Trim();
                    string cleanName = string.IsNullOrWhiteSpace(name) ? "CPU" : CleanHardwareName(name);
                    string devId = obj["DeviceID"]?.ToString()?.Trim() ?? "";
                    string sockDesig = obj["SocketDesignation"]?.ToString()?.Trim() ?? "";
                    int phys = obj["NumberOfCores"] != null ? Convert.ToInt32(obj["NumberOfCores"]) : 0;
                    int logi = obj["NumberOfLogicalProcessors"] != null ? Convert.ToInt32(obj["NumberOfLogicalProcessors"]) : 0;
                    double baseClk = obj["MaxClockSpeed"] != null ? Convert.ToDouble(obj["MaxClockSpeed"]) / 1000.0 : 3.0;

                    rawSockets.Add(new StaticCpuSocket
                    {
                        DeviceId = devId,
                        SocketDesignation = sockDesig,
                        Name = cleanName,
                        PhysicalCores = Math.Max(1, phys),
                        LogicalProcessors = Math.Max(1, logi),
                        BaseClockGhz = baseClk > 0.1 ? baseClk : 3.0
                    });
                }
            }

            rawSockets.Sort((a, b) => string.Compare(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < rawSockets.Count; i++)
            {
                rawSockets[i].Index = i;
                _cpuSockets.Add(rawSockets[i]);
            }
        }
        catch { }

        if (_cpuSockets.Count == 0)
        {
            _cpuSockets.Add(new StaticCpuSocket
            {
                Index = 0,
                DeviceId = "CPU0",
                Name = "CPU",
                PhysicalCores = Environment.ProcessorCount,
                LogicalProcessors = Environment.ProcessorCount,
                BaseClockGhz = 3.0
            });
        }

        _physicalCores = Math.Max(1, _cpuSockets.Sum(s => s.PhysicalCores));
        _logicalProcessors = Math.Max(Environment.ProcessorCount, _cpuSockets.Sum(s => s.LogicalProcessors));
        _baseClockGhz = _cpuSockets[0].BaseClockGhz;
        _cpuName = FormatAggregateCpuName(_cpuSockets.Select(s => s.Name).ToList());

        // RAM speed via WMI (optional)
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
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
        }
        catch { }

        // Map logical drive letters (e.g. "C:") to physical disk model names once for SMART temp matching
        PopulateDriveLetterToDiskModelMap();
    }

    private void PopulateDriveLetterToDiskModelMap()
    {
        try
        {
            using var diskSearcher = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            using var disks = diskSearcher.Get();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    string model = CleanHardwareName(disk["Model"]?.ToString() ?? "");
                    if (string.IsNullOrWhiteSpace(model)) continue;

                    try
                    {
                        using var parts = disk.GetRelated("Win32_DiskPartition");
                        foreach (ManagementObject part in parts)
                        {
                            using (part)
                            {
                                using var logicals = part.GetRelated("Win32_LogicalDisk");
                                foreach (ManagementObject logical in logicals)
                                {
                                    using (logical)
                                    {
                                        string letter = (logical["DeviceID"]?.ToString() ?? "").Trim().TrimEnd('\\');
                                        if (!string.IsNullOrWhiteSpace(letter))
                                        {
                                            _driveLetterToDiskModel[letter] = model;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
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

        // GPU PDH Categories (works for AMD, NVIDIA, Intel on Win10/11) + initial baseline seed
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
                try
                {
                    var catData = _gpuEngineCategory.ReadCategory();
                    if (catData.Contains("Utilization Percentage"))
                    {
                        var instances = catData["Utilization Percentage"];
                        var initial = new Dictionary<string, CounterSample>(instances.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (InstanceData inst in instances.Values)
                        {
                            initial[inst.InstanceName] = inst.Sample;
                        }
                        _prevGpuEngineSamples = initial;
                    }
                }
                catch { }
            }
            if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                _gpuMemCategory = new PerformanceCounterCategory("GPU Adapter Memory");
        }
        catch { }

        // Seed process CPU baselines so tick #1 immediately calculates real process CPU %
        try
        {
            var now = DateTime.UtcNow;
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id > 4)
                    {
                        _prevProcCpu[p.Id] = p.TotalProcessorTime;
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            _lastProcSampleUtc = now;
        }
        catch { }

        // Seed self-footprint CPU baseline
        try
        {
            using var self = Process.GetCurrentProcess();
            _prevSelfProcCpu[self.Id] = self.TotalProcessorTime;
            _prevSelfSampleUtc = DateTime.UtcNow;
        }
        catch { }

        // Seed network byte baselines so tick #1 can compute non-zero rates
        try
        {
            var nics = SelectNetworkInterfaces(NetworkInterface.GetAllNetworkInterfaces());
            foreach (var nic in nics)
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    _prevNetBytes[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                catch { }
            }
            _prevNetTimeUtc = DateTime.UtcNow;
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
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsStorageEnabled = true,
                IsBatteryEnabled = true,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false
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
            if (_disposed) return new TelemetrySnapshot();

            long collectStartTicks = Stopwatch.GetTimestamp();
            var nowUtc = DateTime.UtcNow;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = new TelemetrySnapshot
            {
                TimestampMs = nowMs,
                HostName = Environment.MachineName,
                UptimeSeconds = Environment.TickCount64 / 1000,
                IsElevated = _isElevated
            };

            // 1. Update LibreHardwareMonitor safely (throttled to HeavySampleIntervalMs; SMART storage on 15s cadence).
            bool heavyDue = _lastHeavySampleUtc == DateTime.MinValue ||
                            (nowUtc - _lastHeavySampleUtc).TotalMilliseconds >= Math.Max(500, HeavySampleIntervalMs);
            if (heavyDue)
            {
                _lastHeavySampleUtc = nowUtc;
                bool includeSlowStorage = _lastSlowSampleUtc == DateTime.MinValue ||
                                          (nowUtc - _lastSlowSampleUtc).TotalSeconds >= 15.0;
                if (includeSlowStorage)
                {
                    _lastSlowSampleUtc = nowUtc;
                }

                if (_lhmReady && _computer != null)
                {
                    ReadLibreHardwareMonitor(includeSlowStorage);
                }
            }

            var lhmCpus = new List<LhmCpuData>(_cachedLhmCpus);
            var lhmGpus = new List<LhmGpuData>(_cachedLhmGpus);

            // 2. CPU (aggregate + per-socket metrics)
            var (aggCpu, socketCpus) = CollectCpus(lhmCpus);
            snapshot.Cpu = aggCpu;
            snapshot.Cpus = socketCpus;

            // 3. Memory (including commit charge & limit)
            snapshot.Memory = CollectMemory();

            // 4. GPU (DXGI + PDH + LibreHardwareMonitor fusion)
            if (heavyDue || _cachedGpu == null)
            {
                var (primaryGpu, allGpus) = CollectGpu(lhmGpus);
                _cachedGpu = primaryGpu;
                _cachedGpuSummaries = allGpus;
            }
            snapshot.Gpu = _cachedGpu;
            snapshot.AllGpus = _cachedGpuSummaries;

            // 5. Network (excluding virtual/TUN/hypervisor double-counting)
            snapshot.Network = CollectNetwork();

            // 6. Disk (attaching SMART storage temperatures when matched)
            snapshot.Disk = CollectDisk();

            // 7. System Sensors (Motherboard/VRM, SuperIO/AIO fans, NVMe SMART temps, Battery, Est. System Power)
            double? estSysPower = (snapshot.Cpu.PowerWatts.HasValue || snapshot.Gpu.PowerWatts.HasValue)
                ? Math.Round((snapshot.Cpu.PowerWatts ?? 0) + (snapshot.Gpu.PowerWatts ?? 0), 1)
                : null;

            snapshot.Sensors = new SystemSensorsMetrics
            {
                MotherboardName = _cachedMotherboardName,
                Fans = _cachedFans.ToList(),
                MotherboardTemps = _cachedMoboTemps.ToList(),
                StorageTemps = _cachedStorageTemps.ToList(),
                EstimatedSystemPowerWatts = estSysPower,
                Battery = CollectBattery(_cachedLhmBattery)
            };

            // 8. Top Processes (merged Top 6 CPU + Top 6 RAM, deduplicated by PID)
            if (heavyDue || _cachedTopProcsHeavy.Count == 0)
            {
                _cachedTopProcsHeavy = CollectTopProcesses();
            }
            snapshot.TopProcesses = _cachedTopProcsHeavy;

            // 9. Update Session Min/Avg/Max peaks
            UpdateSessionStatsLocked(snapshot);
            snapshot.SessionStats = BuildSessionStatsSnapshotLocked();

            // 10. Lightweight App Self-Footprint (Host + WebView2 child process tree)
            var footprint = CollectSelfFootprint(nowUtc);
            double collectMs = (Stopwatch.GetTimestamp() - collectStartTicks) * 1000.0 / Stopwatch.Frequency;
            footprint.CollectDurationMs = Math.Round(collectMs, 2);
            snapshot.SelfFootprint = footprint;

            return snapshot;
        }
    }

    /// <summary>Returns a thread-safe snapshot of the current session Min/Avg/Max statistics.</summary>
    public SessionStatsSnapshot GetSessionStats()
    {
        lock (_sync)
        {
            return BuildSessionStatsSnapshotLocked();
        }
    }

    /// <summary>Resets all session Min/Avg/Max statistics and network peak counters.</summary>
    public void ResetSessionStats()
    {
        lock (_sync)
        {
            _sessionStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sessionSampleCount = 0;
            _statCpuUsage.Reset();
            _statCpuTemp.Reset();
            _statCpuPower.Reset();
            _statCpuClock.Reset();
            _statGpuUsage.Reset();
            _statGpuTemp.Reset();
            _statGpuHotspot.Reset();
            _statGpuPower.Reset();
            _statRamUsage.Reset();
            _statNetDownPeak = 0;
            _statNetUpPeak = 0;
        }
    }

    /// <summary>Alias for <see cref="ResetSessionStats"/>.</summary>
    public void ResetPeaks() => ResetSessionStats();

    /// <summary>
    /// Evaluates thermal and resource load alerts with 2-tick debounce, 3°C / 3% hysteresis,
    /// and per-alert tray notification cooldown. Assigns active alerts onto <paramref name="snapshot"/>.Alerts.
    /// </summary>
    public List<TelemetryAlertItem> EvaluateAlerts(
        TelemetrySnapshot snapshot,
        AlertSettings? alertSettings,
        out List<TelemetryAlertItem> trayNotifications)
    {
        trayNotifications = new List<TelemetryAlertItem>();
        if (snapshot == null) return new List<TelemetryAlertItem>();

        lock (_sync)
        {
            if (alertSettings == null || !alertSettings.Enabled)
            {
                _alertStates.Clear();
                snapshot.Alerts = new List<TelemetryAlertItem>();
                return snapshot.Alerts;
            }

            var cfg = SettingsService.NormalizeAlerts(alertSettings);
            var active = new List<TelemetryAlertItem>();
            var nowUtc = DateTime.UtcNow;

            // 1. CPU Temperature
            if (snapshot.Cpu?.TemperatureC is double cpuTemp && !double.IsNaN(cpuTemp))
            {
                var item = EvaluateDualThresholdAlertLocked(
                    id: "cpu_temp",
                    widgetId: "cpu",
                    value: cpuTemp,
                    warnThreshold: cfg.CpuTempWarnC,
                    critThreshold: cfg.CpuTempCritC,
                    hysteresis: 3.0,
                    warnTitle: "CPU 温度偏高",
                    critTitle: "CPU 温度过高",
                    formatMsg: (sev, v, thr) => $"CPU 温度达到 {v:0.#}°C（{(sev == "danger" ? "严重" : "预警")}阈值 {thr:0}°C）",
                    cfg: cfg,
                    nowUtc: nowUtc,
                    trayNotifications: trayNotifications);
                if (item != null) active.Add(item);
            }
            else
            {
                ResetAlertRuleLocked("cpu_temp");
            }

            // 2. GPU Temperature
            if (snapshot.Gpu?.TemperatureC is double gpuTemp && !double.IsNaN(gpuTemp))
            {
                var item = EvaluateDualThresholdAlertLocked(
                    id: "gpu_temp",
                    widgetId: "gpu",
                    value: gpuTemp,
                    warnThreshold: cfg.GpuTempWarnC,
                    critThreshold: cfg.GpuTempCritC,
                    hysteresis: 3.0,
                    warnTitle: "GPU 温度偏高",
                    critTitle: "GPU 温度过高",
                    formatMsg: (sev, v, thr) => $"GPU 温度达到 {v:0.#}°C（{(sev == "danger" ? "严重" : "预警")}阈值 {thr:0}°C）",
                    cfg: cfg,
                    nowUtc: nowUtc,
                    trayNotifications: trayNotifications);
                if (item != null) active.Add(item);
            }
            else
            {
                ResetAlertRuleLocked("gpu_temp");
            }

            // 3. RAM Usage %
            if (snapshot.Memory != null && snapshot.Memory.TotalGb > 0)
            {
                double ramPct = snapshot.Memory.UsagePercent;
                double ramCrit = Math.Max(cfg.RamUsageWarnPct + 6, 96);
                var item = EvaluateSingleOrEscalatingAlertLocked(
                    id: "ram_usage",
                    widgetId: "ram",
                    value: ramPct,
                    warnThreshold: cfg.RamUsageWarnPct,
                    critThreshold: ramCrit <= 99 ? ramCrit : 101,
                    hysteresis: 3.0,
                    warnTitle: "内存占用偏高",
                    critTitle: "内存占用严重偏高",
                    formatMsg: (_, v, _) => $"内存已用 {v:0.#}%（阈值 {cfg.RamUsageWarnPct}%）",
                    cfg: cfg,
                    nowUtc: nowUtc,
                    trayNotifications: trayNotifications);
                if (item != null) active.Add(item);
            }
            else
            {
                ResetAlertRuleLocked("ram_usage");
            }

            // 4. Highest Disk Usage %
            DriveItem? hottestDrive = snapshot.Disk?.Drives?
                .Where(d => d != null && d.TotalGb > 0)
                .OrderByDescending(d => d.UsagePercent)
                .FirstOrDefault();

            if (hottestDrive != null)
            {
                double diskCrit = Math.Max(cfg.DiskUsageWarnPct + 5, 96);
                var item = EvaluateSingleOrEscalatingAlertLocked(
                    id: "disk_usage",
                    widgetId: "disk",
                    value: hottestDrive.UsagePercent,
                    warnThreshold: cfg.DiskUsageWarnPct,
                    critThreshold: diskCrit <= 99 ? diskCrit : 101,
                    hysteresis: 3.0,
                    warnTitle: $"磁盘 {hottestDrive.Name} 空间偏紧",
                    critTitle: $"磁盘 {hottestDrive.Name} 空间告急",
                    formatMsg: (_, v, _) => $"磁盘 {hottestDrive.Name} 已用 {v:0.#}%（剩余 {hottestDrive.FreeGb:0.#} GB，阈值 {cfg.DiskUsageWarnPct}%）",
                    cfg: cfg,
                    nowUtc: nowUtc,
                    trayNotifications: trayNotifications);
                if (item != null) active.Add(item);
            }
            else
            {
                ResetAlertRuleLocked("disk_usage");
            }

            snapshot.Alerts = active;
            return active;
        }
    }

    public void ResetAlertStates()
    {
        lock (_sync)
        {
            _alertStates.Clear();
        }
    }

    private TelemetryAlertItem? EvaluateDualThresholdAlertLocked(
        string id,
        string widgetId,
        double value,
        double warnThreshold,
        double critThreshold,
        double hysteresis,
        string warnTitle,
        string critTitle,
        Func<string, double, double, string> formatMsg,
        AlertSettings cfg,
        DateTime nowUtc,
        List<TelemetryAlertItem> trayNotifications)
    {
        if (!_alertStates.TryGetValue(id, out var st))
        {
            st = new AlertRuleState();
            _alertStates[id] = st;
        }

        if (value >= critThreshold)
        {
            st.ConsecutiveWarnTicks++;
            st.ConsecutiveCritTicks++;
            if (st.ActiveSeverity == "danger" || st.ConsecutiveCritTicks >= 2)
                st.ActiveSeverity = "danger";
            else if (st.ActiveSeverity == "warning" || st.ConsecutiveWarnTicks >= 2)
                st.ActiveSeverity = "warning";
        }
        else if (value >= warnThreshold)
        {
            st.ConsecutiveWarnTicks++;
            if (st.ActiveSeverity == "danger")
            {
                if (value < critThreshold - hysteresis)
                {
                    st.ActiveSeverity = "warning";
                    st.ConsecutiveCritTicks = 0;
                }
            }
            else
            {
                st.ConsecutiveCritTicks = 0;
                if (st.ActiveSeverity == "warning" || st.ConsecutiveWarnTicks >= 2)
                    st.ActiveSeverity = "warning";
            }
        }
        else
        {
            st.ConsecutiveCritTicks = 0;
            if (!string.IsNullOrEmpty(st.ActiveSeverity) && value >= warnThreshold - hysteresis)
            {
                // Hold active at warning within hysteresis band to prevent flicker
                st.ActiveSeverity = "warning";
            }
            else
            {
                st.ActiveSeverity = "";
                st.ConsecutiveWarnTicks = 0;
            }
        }

        if (string.IsNullOrEmpty(st.ActiveSeverity)) return null;

        bool isCrit = st.ActiveSeverity == "danger";
        double effectiveThreshold = isCrit ? critThreshold : warnThreshold;
        var item = new TelemetryAlertItem
        {
            Id = id,
            WidgetId = widgetId,
            Severity = st.ActiveSeverity,
            Title = isCrit ? critTitle : warnTitle,
            Message = formatMsg(st.ActiveSeverity, value, effectiveThreshold),
            Value = Math.Round(value, 1),
            Threshold = effectiveThreshold
        };

        MaybeQueueTrayAlertLocked(st, item, cfg, nowUtc, trayNotifications);
        return item;
    }

    private TelemetryAlertItem? EvaluateSingleOrEscalatingAlertLocked(
        string id,
        string widgetId,
        double value,
        double warnThreshold,
        double critThreshold,
        double hysteresis,
        string warnTitle,
        string critTitle,
        Func<string, double, double, string> formatMsg,
        AlertSettings cfg,
        DateTime nowUtc,
        List<TelemetryAlertItem> trayNotifications)
    {
        var item = EvaluateDualThresholdAlertLocked(
            id, widgetId, value, warnThreshold, critThreshold, hysteresis,
            warnTitle, critTitle, formatMsg, cfg, nowUtc, trayNotifications);
        if (item != null)
        {
            item.Threshold = warnThreshold;
        }
        return item;
    }

    private static void MaybeQueueTrayAlertLocked(
        AlertRuleState st,
        TelemetryAlertItem item,
        AlertSettings cfg,
        DateTime nowUtc,
        List<TelemetryAlertItem> trayNotifications)
    {
        if (!cfg.TrayNotifyEnabled) return;

        bool neverSent = st.LastTrayNotifiedUtc == DateTime.MinValue;
        double sinceLastSec = neverSent ? double.MaxValue : (nowUtc - st.LastTrayNotifiedUtc).TotalSeconds;
        bool escalated = item.Severity == "danger" && st.LastTraySeverity != "danger" && sinceLastSec >= 10.0;

        if (neverSent || sinceLastSec >= cfg.CooldownSeconds || escalated)
        {
            st.LastTrayNotifiedUtc = nowUtc;
            st.LastTraySeverity = item.Severity;
            trayNotifications.Add(item);
        }
    }

    private void ResetAlertRuleLocked(string id)
    {
        if (_alertStates.TryGetValue(id, out var st))
        {
            st.ConsecutiveWarnTicks = 0;
            st.ConsecutiveCritTicks = 0;
            st.ActiveSeverity = "";
        }
    }

    private void UpdateSessionStatsLocked(TelemetrySnapshot snap)
    {
        _sessionSampleCount++;
        _statCpuUsage.Add(snap.Cpu.TotalUsagePercent);
        _statCpuTemp.Add(snap.Cpu.TemperatureC);
        _statCpuPower.Add(snap.Cpu.PowerWatts);
        if (snap.Cpu.CurrentClockGhz > 0.1) _statCpuClock.Add(snap.Cpu.CurrentClockGhz);

        _statGpuUsage.Add(snap.Gpu.UsagePercent);
        _statGpuTemp.Add(snap.Gpu.TemperatureC);
        _statGpuHotspot.Add(snap.Gpu.HotspotTempC);
        _statGpuPower.Add(snap.Gpu.PowerWatts);

        if (snap.Memory.TotalGb > 0) _statRamUsage.Add(snap.Memory.UsagePercent);

        if (snap.Network.DownloadBytesPerSec > _statNetDownPeak)
            _statNetDownPeak = snap.Network.DownloadBytesPerSec;
        if (snap.Network.UploadBytesPerSec > _statNetUpPeak)
            _statNetUpPeak = snap.Network.UploadBytesPerSec;
    }

    private SessionStatsSnapshot BuildSessionStatsSnapshotLocked() => new()
    {
        SessionStartedMs = _sessionStartedMs,
        SampleCount = _sessionSampleCount,
        CpuUsage = _statCpuUsage.ToMetricRange(1),
        CpuTemp = _statCpuTemp.ToMetricRange(1),
        CpuPower = _statCpuPower.ToMetricRange(1),
        CpuClock = _statCpuClock.ToMetricRange(2),
        GpuUsage = _statGpuUsage.ToMetricRange(1),
        GpuTemp = _statGpuTemp.ToMetricRange(1),
        GpuHotspot = _statGpuHotspot.ToMetricRange(1),
        GpuPower = _statGpuPower.ToMetricRange(1),
        RamUsage = _statRamUsage.ToMetricRange(1),
        NetDownPeakBytesPerSec = Math.Round(_statNetDownPeak, 0),
        NetUpPeakBytesPerSec = Math.Round(_statNetUpPeak, 0)
    };

    private sealed class LhmCpuData
    {
        public string Identifier = "";
        public string Name = "";
        public double? TempC;
        public double? PowerW;
        public double? AvgClockGhz;
        public double? TotalLoad;
        public List<double> CoreLoads = new();
    }

    private sealed class LhmGpuData
    {
        public string Name = "";
        public double? LoadPercent;
        public double? TempC;
        public double? HotspotC;
        public double? MemoryTempC;
        public double? FanRpm;
        public double? FanPercent;
        public double? CoreClockMhz;
        public double? MemoryClockMhz;
        public double? PowerW;
        public double? VramUsedGb;
        public double? VramTotalGb;
    }

    private sealed class LhmBatteryData
    {
        public double? ChargePercent;
        public double? PowerWatts;
        public int? RemainingMinutes;
        public double? WearPercent;
        public bool? IsCharging;
    }

    private void ReadLibreHardwareMonitor(bool includeSlowStorage)
    {
        if (_computer == null) return;

        // Build into local temporary collections and swap atomically on completion so transient
        // driver errors never wipe previously valid sensor readings.
        var tmpCpus = new List<LhmCpuData>();
        var tmpGpus = new List<LhmGpuData>();
        var tmpFans = new List<FanSensorItem>();
        var tmpMoboTemps = new List<(int Priority, TempSensorItem Item)>();
        var tmpStorageTemps = includeSlowStorage ? new List<StorageTempItem>() : null;
        string tmpMoboName = _cachedMotherboardName;
        LhmBatteryData? tmpBattery = null;

        try
        {
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    if (hw.HardwareType == HardwareType.Storage && !includeSlowStorage)
                    {
                        continue;
                    }

                    hw.Update();
                    UpdateSubHardwareRecursive(hw);

                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            tmpCpus.Add(ExtractLhmCpu(hw));
                            break;

                        case HardwareType.GpuAmd:
                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuIntel:
                            tmpGpus.Add(ExtractLhmGpu(hw));
                            break;

                        case HardwareType.Motherboard:
                            if (!string.IsNullOrWhiteSpace(hw.Name))
                            {
                                tmpMoboName = CleanHardwareName(hw.Name);
                            }
                            CollectMotherboardOrControllerSensors(hw, tmpFans, tmpMoboTemps);
                            break;

                        case HardwareType.SuperIO:
                        case HardwareType.Cooler:
                        case HardwareType.EmbeddedController:
                            CollectMotherboardOrControllerSensors(hw, tmpFans, tmpMoboTemps);
                            break;

                        case HardwareType.Storage:
                            if (tmpStorageTemps != null)
                            {
                                var item = ExtractStorageTemp(hw);
                                if (item != null) tmpStorageTemps.Add(item);
                            }
                            break;

                        case HardwareType.Battery:
                            tmpBattery ??= ExtractLhmBattery(hw);
                            break;
                    }
                }
                catch { }
            }

            // Atomic swaps after full enumeration succeeds
            if (tmpCpus.Count > 0 || _cachedLhmCpus.Count == 0)
                _cachedLhmCpus = tmpCpus;
            if (tmpGpus.Count > 0 || _cachedLhmGpus.Count == 0)
                _cachedLhmGpus = tmpGpus;

            _cachedMotherboardName = tmpMoboName;
            _cachedFans = DeduplicateAndRankFans(tmpFans);
            _cachedMoboTemps = DeduplicateAndRankMoboTemps(tmpMoboTemps);
            if (tmpStorageTemps != null)
            {
                _cachedStorageTemps = DeduplicateAndRankStorageTemps(tmpStorageTemps);
            }
            _cachedLhmBattery = tmpBattery;
        }
        catch { }
    }

    private static void UpdateSubHardwareRecursive(IHardware hw)
    {
        foreach (var sub in hw.SubHardware)
        {
            try
            {
                sub.Update();
                if (sub.SubHardware.Length > 0)
                {
                    UpdateSubHardwareRecursive(sub);
                }
            }
            catch { }
        }
    }

    private static IEnumerable<ISensor> EnumerateAllSensors(IHardware hw)
    {
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
        {
            foreach (var s in EnumerateAllSensors(sub)) yield return s;
        }
    }

    private static LhmCpuData ExtractLhmCpu(IHardware hw)
    {
        var c = new LhmCpuData
        {
            Identifier = hw.Identifier.ToString(),
            Name = CleanHardwareName(hw.Name ?? "")
        };

        var clocks = new List<double>();
        var effectiveClocks = new List<double>();
        var coreLoadPairs = new List<(int Idx, double Val)>();

        int bestTempRank = int.MaxValue;
        double? bestTemp = null;
        int bestPowerRank = int.MaxValue;
        double? bestPower = null;

        foreach (var s in EnumerateAllSensors(hw))
        {
            if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || float.IsInfinity(s.Value.Value)) continue;
            double val = s.Value.Value;
            string name = s.Name ?? "";

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (val > 5 && val < 125)
                    {
                        if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("TjMax", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        int rank;
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            rank = 1;
                        else if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                 (name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) &&
                                  !name.Contains("CCD", StringComparison.OrdinalIgnoreCase)))
                            rank = 2;
                        else if (name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                            rank = 3;
                        else if (name.Contains("Core Average", StringComparison.OrdinalIgnoreCase))
                            rank = 4;
                        else
                            rank = 5;

                        if (rank < bestTempRank)
                        {
                            bestTempRank = rank;
                            bestTemp = Math.Round(val, 1);
                        }
                    }
                    break;

                case SensorType.Power:
                    if (val >= 0 && val < 2000)
                    {
                        int rank = 0;
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            rank = 1;
                        else if (name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("PPT", StringComparison.OrdinalIgnoreCase))
                            rank = 2;
                        else if (name.Equals("CPU Cores", StringComparison.OrdinalIgnoreCase))
                            rank = 3;

                        if (rank > 0 && rank < bestPowerRank)
                        {
                            bestPowerRank = rank;
                            bestPower = Math.Round(val, 1);
                        }
                    }
                    break;

                case SensorType.Clock:
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) && val > 200 && val < 10000)
                    {
                        if (name.Contains("Effective", StringComparison.OrdinalIgnoreCase))
                            effectiveClocks.Add(val);
                        else
                            clocks.Add(val);
                    }
                    break;

                case SensorType.Load:
                    if (name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                    {
                        c.TotalLoad = val;
                    }
                    else if (name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))
                    {
                        coreLoadPairs.Add((s.Index, Math.Round(Math.Clamp(val, 0, 100), 1)));
                    }
                    break;
            }
        }

        c.TempC = bestTemp;
        c.PowerW = bestPower;

        var chosenClocks = clocks.Count > 0 ? clocks : effectiveClocks;
        if (chosenClocks.Count > 0)
        {
            c.AvgClockGhz = Math.Round(chosenClocks.Max() / 1000.0, 2);
        }

        if (coreLoadPairs.Count > 0)
        {
            c.CoreLoads = coreLoadPairs.OrderBy(p => p.Idx).Select(p => p.Val).ToList();
        }

        return c;
    }

    private static LhmGpuData ExtractLhmGpu(IHardware hw)
    {
        var g = new LhmGpuData { Name = hw.Name ?? "" };
        double? coreLoad = null;
        double? d3dLoad = null;
        int bestTempRank = int.MaxValue;
        int bestMemTempRank = int.MaxValue;
        int bestPowerRank = int.MaxValue;

        foreach (var s in EnumerateAllSensors(hw))
        {
            if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || float.IsInfinity(s.Value.Value)) continue;
            double val = s.Value.Value;
            string name = s.Name ?? "";

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
                        if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                        {
                            g.HotspotC = Math.Round(val, 1);
                        }
                        else if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("Mem Junction", StringComparison.OrdinalIgnoreCase))
                        {
                            int memRank = name.Contains("Junction", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                            if (memRank < bestMemTempRank)
                            {
                                bestMemTempRank = memRank;
                                g.MemoryTempC = Math.Round(val, 1);
                            }
                        }
                        else
                        {
                            bool excluded =
                                name.Contains("VR", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Junction", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Board", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Liquid", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("PLX", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("SoC", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase);

                            if (!excluded)
                            {
                                int tempRank = name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)
                                    ? 1
                                    : (name.Contains("Core", StringComparison.OrdinalIgnoreCase) ? 2 : 3);
                                if (tempRank < bestTempRank)
                                {
                                    bestTempRank = tempRank;
                                    g.TempC = Math.Round(val, 1);
                                }
                            }
                        }
                    }
                    break;

                case SensorType.Fan:
                    if (val >= 0 && val < 20000)
                    {
                        if (!g.FanRpm.HasValue || (g.FanRpm.Value <= 0 && val > 0))
                        {
                            g.FanRpm = Math.Round(val, 0);
                        }
                    }
                    break;

                case SensorType.Control:
                    if (name.Contains("Fan", StringComparison.OrdinalIgnoreCase) && val >= 0 && val <= 100)
                    {
                        if (!g.FanPercent.HasValue || (g.FanPercent.Value <= 0 && val > 0))
                        {
                            g.FanPercent = Math.Round(val, 0);
                        }
                    }
                    break;

                case SensorType.Clock:
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) && val > 0 && val < 10000)
                        g.CoreClockMhz = Math.Round(val, 0);
                    else if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && val > 0 && val < 50000)
                        g.MemoryClockMhz = Math.Round(val, 0);
                    break;

                case SensorType.Power:
                    if (val >= 0 && val < 2000)
                    {
                        int powerRank = 0;
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Board", StringComparison.OrdinalIgnoreCase))
                        {
                            powerRank = 1;
                        }
                        else if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            powerRank = 2;
                        }

                        if (powerRank > 0 && powerRank < bestPowerRank)
                        {
                            bestPowerRank = powerRank;
                            g.PowerW = Math.Round(val, 1);
                        }
                    }
                    break;

                case SensorType.SmallData:
                    if (name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                        g.VramUsedGb ??= Math.Round(val / 1024.0, 2);
                    else if (name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                        g.VramTotalGb ??= Math.Round(val / 1024.0, 2);
                    break;
            }
        }

        g.LoadPercent = coreLoad ?? d3dLoad;
        return g;
    }

    private static void CollectMotherboardOrControllerSensors(
        IHardware hw,
        List<FanSensorItem> fansOut,
        List<(int Priority, TempSensorItem Item)> tempsOut)
    {
        ExtractNodeFansAndTemps(hw, fansOut, tempsOut);
        foreach (var sub in hw.SubHardware)
        {
            CollectMotherboardOrControllerSensors(sub, fansOut, tempsOut);
        }
    }

    private static void ExtractNodeFansAndTemps(
        IHardware node,
        List<FanSensorItem> fansOut,
        List<(int Priority, TempSensorItem Item)> tempsOut)
    {
        string source = node.HardwareType switch
        {
            HardwareType.Cooler => "AIO",
            HardwareType.EmbeddedController => "EC",
            HardwareType.SuperIO => "主板",
            HardwareType.Motherboard => "主板",
            _ => "控制器"
        };

        var controlsByIndex = new Dictionary<int, double>();
        var controlsByKey = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in node.Sensors)
        {
            if (s.SensorType != SensorType.Control) continue;
            if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || float.IsInfinity(s.Value.Value)) continue;
            double val = s.Value.Value;
            if (val < 0 || val > 100) continue;

            controlsByIndex.TryAdd(s.Index, Math.Round(val, 0));
            string norm = NormalizeSensorMatchKey(s.Name);
            if (!string.IsNullOrEmpty(norm))
            {
                controlsByKey.TryAdd(norm, Math.Round(val, 0));
            }
        }

        foreach (var s in node.Sensors)
        {
            if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || float.IsInfinity(s.Value.Value)) continue;
            double val = s.Value.Value;
            string rawName = (s.Name ?? "").Trim();

            if (s.SensorType == SensorType.Fan)
            {
                if (val <= 0 || val >= 20000) continue;
                string displayName = string.IsNullOrWhiteSpace(rawName) ? $"Fan #{s.Index}" : rawName;
                double? pct = null;
                string norm = NormalizeSensorMatchKey(rawName);
                if (!string.IsNullOrEmpty(norm) && controlsByKey.TryGetValue(norm, out double byName))
                {
                    pct = byName;
                }
                else if (controlsByIndex.TryGetValue(s.Index, out double byIdx))
                {
                    pct = byIdx;
                }

                fansOut.Add(new FanSensorItem
                {
                    Name = displayName,
                    Rpm = Math.Round(val, 0),
                    Percent = pct,
                    Source = source
                });
            }
            else if (s.SensorType == SensorType.Temperature)
            {
                // Skip uncalibrated / bogus readings (<=10°C or >115°C)
                if (val <= 10.0 || val > 115.0) continue;
                if (rawName.Contains("Distance", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Contains("TjMax", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Contains("Limit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string displayName = string.IsNullOrWhiteSpace(rawName) ? $"Temp #{s.Index}" : rawName;
                int prio = GetMotherboardTempPriority(displayName, node.HardwareType);
                tempsOut.Add((prio, new TempSensorItem
                {
                    Name = displayName,
                    TempC = Math.Round(val, 1)
                }));
            }
        }
    }

    private static int GetMotherboardTempPriority(string name, HardwareType hwType)
    {
        if (name.Contains("VRM", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MOS", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VR ", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (name.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PCH", StringComparison.OrdinalIgnoreCase))
            return 2;

        if (hwType == HardwareType.Cooler ||
            name.Contains("Coolant", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Water", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Liquid", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Pump", StringComparison.OrdinalIgnoreCase))
            return 3;

        if (name.Contains("System", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mainboard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ambient", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Chassis", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("T_Sensor", StringComparison.OrdinalIgnoreCase))
            return 4;

        return 5;
    }

    private static List<FanSensorItem> DeduplicateAndRankFans(List<FanSensorItem> raw)
    {
        if (raw.Count == 0) return new List<FanSensorItem>();

        var dedup = new Dictionary<string, FanSensorItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var fan in raw)
        {
            string key = $"{fan.Source}|{fan.Name}";
            if (!dedup.TryGetValue(key, out var existing) || fan.Rpm > existing.Rpm)
            {
                dedup[key] = fan;
            }
        }

        return dedup.Values
            .OrderBy(f => GetFanSortPriority(f))
            .ThenByDescending(f => f.Rpm)
            .Take(8)
            .ToList();
    }

    private static int GetFanSortPriority(FanSensorItem fan)
    {
        string n = fan.Name;
        if (n.Contains("CPU", StringComparison.OrdinalIgnoreCase)) return 1;
        if (n.Contains("Pump", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("AIO", StringComparison.OrdinalIgnoreCase) ||
            fan.Source == "AIO") return 2;
        if (n.Contains("Chassis", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("System", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Case", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static List<TempSensorItem> DeduplicateAndRankMoboTemps(List<(int Priority, TempSensorItem Item)> raw)
    {
        if (raw.Count == 0) return new List<TempSensorItem>();

        var dedup = new Dictionary<string, (int Priority, TempSensorItem Item)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw)
        {
            string key = entry.Item.Name;
            if (!dedup.TryGetValue(key, out var existing) ||
                entry.Priority < existing.Priority ||
                (entry.Priority == existing.Priority && entry.Item.TempC > existing.Item.TempC))
            {
                dedup[key] = entry;
            }
        }

        return dedup.Values
            .OrderBy(x => x.Priority)
            .ThenByDescending(x => x.Item.TempC)
            .Take(6)
            .Select(x => x.Item)
            .ToList();
    }

    private static StorageTempItem? ExtractStorageTemp(IHardware hw)
    {
        string diskName = CleanHardwareName(hw.Name ?? "Storage");
        int bestTempRank = int.MaxValue;
        double? bestTemp = null;
        int bestLifeRank = int.MaxValue;
        double? bestLife = null;

        foreach (var s in EnumerateAllSensors(hw))
        {
            if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || float.IsInfinity(s.Value.Value)) continue;
            double val = s.Value.Value;
            string name = s.Name ?? "";

            if (s.SensorType == SensorType.Temperature && val > 5 && val < 105)
            {
                if (name.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Critical", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Limit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int rank = (name.Equals("Temperature", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Composite", StringComparison.OrdinalIgnoreCase))
                    ? 1
                    : (name.Equals("Temperature 1", StringComparison.OrdinalIgnoreCase) ? 2 : 3);

                if (rank < bestTempRank)
                {
                    bestTempRank = rank;
                    bestTemp = Math.Round(val, 1);
                }
            }
            else if (s.SensorType == SensorType.Level && val >= 0 && val <= 100)
            {
                if (name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase))
                {
                    if (1 < bestLifeRank)
                    {
                        bestLifeRank = 1;
                        bestLife = Math.Round(Math.Clamp(val, 0, 100), 0);
                    }
                }
                else if (name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase))
                {
                    if (2 < bestLifeRank)
                    {
                        bestLifeRank = 2;
                        bestLife = Math.Round(Math.Clamp(100.0 - val, 0, 100), 0);
                    }
                }
                else if (name.Contains("Life", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                {
                    if (3 < bestLifeRank)
                    {
                        bestLifeRank = 3;
                        bestLife = Math.Round(Math.Clamp(val, 0, 100), 0);
                    }
                }
                else if (name.Contains("Available Spare", StringComparison.OrdinalIgnoreCase))
                {
                    if (4 < bestLifeRank)
                    {
                        bestLifeRank = 4;
                        bestLife = Math.Round(Math.Clamp(val, 0, 100), 0);
                    }
                }
            }
        }

        if (!bestTemp.HasValue && !bestLife.HasValue) return null;

        return new StorageTempItem
        {
            Name = string.IsNullOrWhiteSpace(diskName) ? "Storage" : diskName,
            TempC = bestTemp,
            LifeRemainingPercent = bestLife
        };
    }

    private static List<StorageTempItem> DeduplicateAndRankStorageTemps(List<StorageTempItem> raw)
    {
        if (raw.Count == 0) return new List<StorageTempItem>();

        return raw
            .OrderByDescending(s => s.TempC.HasValue)
            .ThenByDescending(s => s.TempC ?? 0)
            .Take(6)
            .ToList();
    }

    private static LhmBatteryData? ExtractLhmBattery(IHardware hw)
    {
        var b = new LhmBatteryData();
        double? designedEnergy = null;
        double? fullChargeEnergy = null;
        bool hasAny = false;

        foreach (var s in EnumerateAllSensors(hw))
        {
            if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || float.IsInfinity(s.Value.Value)) continue;
            double val = s.Value.Value;
            string name = s.Name ?? "";

            switch (s.SensorType)
            {
                case SensorType.Level:
                    if (name.Contains("Charge", StringComparison.OrdinalIgnoreCase) && val >= 0 && val <= 100)
                    {
                        b.ChargePercent = Math.Round(val, 1);
                        hasAny = true;
                    }
                    else if ((name.Contains("Degradation", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Wear", StringComparison.OrdinalIgnoreCase)) && val >= 0 && val <= 100)
                    {
                        b.WearPercent = Math.Round(val, 1);
                        hasAny = true;
                    }
                    break;

                case SensorType.Power:
                    if (name.Contains("Charge Rate", StringComparison.OrdinalIgnoreCase) && val > 0.05)
                    {
                        b.PowerWatts = Math.Round(val, 1);
                        b.IsCharging = true;
                        hasAny = true;
                    }
                    else if (name.Contains("Discharge Rate", StringComparison.OrdinalIgnoreCase) && val > 0.05)
                    {
                        b.PowerWatts = Math.Round(val, 1);
                        b.IsCharging = false;
                        hasAny = true;
                    }
                    else if (!b.PowerWatts.HasValue && val > 0.05 && val < 500)
                    {
                        b.PowerWatts = Math.Round(val, 1);
                        hasAny = true;
                    }
                    break;

                case SensorType.TimeSpan:
                    if (name.Contains("Remaining", StringComparison.OrdinalIgnoreCase) && val > 0 && val < 604800)
                    {
                        b.RemainingMinutes = (int)Math.Round(val / 60.0);
                        hasAny = true;
                    }
                    break;

                case SensorType.Energy:
                    if (name.Contains("Designed", StringComparison.OrdinalIgnoreCase) && val > 0)
                        designedEnergy = val;
                    else if (name.Contains("Full", StringComparison.OrdinalIgnoreCase) && val > 0)
                        fullChargeEnergy = val;
                    break;
            }
        }

        if (!b.WearPercent.HasValue && designedEnergy is > 0 && fullChargeEnergy is > 0)
        {
            double wear = Math.Clamp((1.0 - fullChargeEnergy.Value / designedEnergy.Value) * 100.0, 0, 100);
            b.WearPercent = Math.Round(wear, 1);
            hasAny = true;
        }

        return hasAny ? b : null;
    }

    private static BatteryMetrics? CollectBattery(LhmBatteryData? lhm)
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool noWinBattery =
                ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery ||
                ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.Unknown;

            if (noWinBattery && lhm == null)
            {
                return null;
            }

            bool onAc = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            bool isCharging = lhm?.IsCharging ??
                              ((ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.Charging) != 0);

            double chargePct;
            if (lhm?.ChargePercent.HasValue == true)
            {
                chargePct = Math.Clamp(lhm.ChargePercent.Value, 0, 100);
            }
            else if (!noWinBattery && ps.BatteryLifePercent >= 0 && ps.BatteryLifePercent <= 1.0f)
            {
                chargePct = Math.Clamp(ps.BatteryLifePercent * 100.0, 0, 100);
            }
            else
            {
                return null;
            }

            int? remainingMin = lhm?.RemainingMinutes;
            if (!remainingMin.HasValue && !noWinBattery && ps.BatteryLifeRemaining > 0)
            {
                remainingMin = (int)Math.Round(ps.BatteryLifeRemaining / 60.0);
            }

            return new BatteryMetrics
            {
                ChargePercent = Math.Round(chargePct, 1),
                IsCharging = isCharging,
                OnAcPower = onAc,
                PowerWatts = lhm?.PowerWatts,
                RemainingMinutes = remainingMin,
                WearPercent = lhm?.WearPercent
            };
        }
        catch
        {
            if (lhm?.ChargePercent.HasValue == true)
            {
                return new BatteryMetrics
                {
                    ChargePercent = Math.Round(Math.Clamp(lhm.ChargePercent.Value, 0, 100), 1),
                    IsCharging = lhm.IsCharging ?? false,
                    OnAcPower = lhm.IsCharging ?? false,
                    PowerWatts = lhm.PowerWatts,
                    RemainingMinutes = lhm.RemainingMinutes,
                    WearPercent = lhm.WearPercent
                };
            }
            return null;
        }
    }

    private (CpuMetrics Aggregate, List<CpuSocketMetrics> Sockets) CollectCpus(List<LhmCpuData> lhmCpus)
    {
        lhmCpus.Sort((a, b) => string.Compare(a.Identifier, b.Identifier, StringComparison.OrdinalIgnoreCase));

        double systemTotalUsage = 0;
        bool hasSysTimesDelta = false;

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
                    systemTotalUsage = Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
                    hasSysTimesDelta = true;
                }
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuTimes = true;
        }

        // Per-core loads across all processor groups / sockets
        var allCoreUsages = new double[_coreCounters.Count];
        for (int i = 0; i < _coreCounters.Count; i++)
        {
            try
            {
                allCoreUsages[i] = Math.Round(Math.Clamp(_coreCounters[i].NextValue(), 0, 100), 1);
            }
            catch
            {
                allCoreUsages[i] = 0;
            }
        }

        // Fallback PDH frequency ratio when MSR clock is unavailable
        double? pdhPerfRatio = null;
        if (_cpuPerfPercentCounter != null)
        {
            try
            {
                double perfPct = _cpuPerfPercentCounter.NextValue();
                if (perfPct > 10 && perfPct < 400)
                    pdhPerfRatio = perfPct / 100.0;
            }
            catch { }
        }

        // Reconcile effective socket list (if WMI saw 1 entry but LHM reports multiple physical CPUs)
        var effectiveSockets = _cpuSockets;
        if (effectiveSockets.Count == 1 && lhmCpus.Count > 1)
        {
            int perPhys = Math.Max(1, _physicalCores / lhmCpus.Count);
            int perLogi = Math.Max(1, _logicalProcessors / lhmCpus.Count);
            effectiveSockets = new List<StaticCpuSocket>(lhmCpus.Count);
            for (int i = 0; i < lhmCpus.Count; i++)
            {
                effectiveSockets.Add(new StaticCpuSocket
                {
                    Index = i,
                    DeviceId = $"CPU{i}",
                    SocketDesignation = $"CPU {i + 1}",
                    Name = !string.IsNullOrWhiteSpace(lhmCpus[i].Name) ? lhmCpus[i].Name : _cpuSockets[0].Name,
                    PhysicalCores = perPhys,
                    LogicalProcessors = perLogi,
                    BaseClockGhz = _cpuSockets[0].BaseClockGhz
                });
            }
        }

        var sockets = new List<CpuSocketMetrics>(effectiveSockets.Count);
        int coreCursor = 0;

        for (int i = 0; i < effectiveSockets.Count; i++)
        {
            var spec = effectiveSockets[i];
            var lhm = i < lhmCpus.Count ? lhmCpus[i] : null;
            int startCore = coreCursor;

            double[] socketCores;
            if (allCoreUsages.Length > 0 && coreCursor < allCoreUsages.Length)
            {
                int remaining = allCoreUsages.Length - coreCursor;
                int take = (i == effectiveSockets.Count - 1)
                    ? remaining
                    : Math.Min(spec.LogicalProcessors, remaining);
                socketCores = new double[take];
                Array.Copy(allCoreUsages, coreCursor, socketCores, 0, take);
                coreCursor += take;
            }
            else if (lhm != null && lhm.CoreLoads.Count > 0)
            {
                socketCores = lhm.CoreLoads.ToArray();
            }
            else
            {
                socketCores = Array.Empty<double>();
            }

            double socketUsage;
            if (effectiveSockets.Count == 1)
            {
                socketUsage = hasSysTimesDelta
                    ? systemTotalUsage
                    : (lhm?.TotalLoad is > 0 ? Math.Clamp(lhm.TotalLoad.Value, 0, 100) : (socketCores.Length > 0 ? socketCores.Average() : 0));
            }
            else
            {
                socketUsage = socketCores.Length > 0
                    ? Math.Clamp(socketCores.Average(), 0, 100)
                    : (lhm?.TotalLoad.HasValue == true ? Math.Clamp(lhm.TotalLoad.Value, 0, 100) : systemTotalUsage);
            }

            double socketClock = spec.BaseClockGhz;
            if (lhm?.AvgClockGhz is > 0.4)
            {
                socketClock = lhm.AvgClockGhz.Value;
            }
            else if (pdhPerfRatio.HasValue)
            {
                socketClock = Math.Round(spec.BaseClockGhz * pdhPerfRatio.Value, 2);
            }

            string displayName = !string.IsNullOrWhiteSpace(spec.Name) && spec.Name != "CPU"
                ? spec.Name
                : (!string.IsNullOrWhiteSpace(lhm?.Name) ? lhm.Name : "CPU");

            sockets.Add(new CpuSocketMetrics
            {
                Index = i,
                Label = $"CPU {i + 1}",
                SocketDesignation = spec.SocketDesignation,
                Name = displayName,
                PhysicalCores = spec.PhysicalCores,
                LogicalProcessors = socketCores.Length > 0 ? socketCores.Length : spec.LogicalProcessors,
                StartCoreIndex = startCore,
                TotalUsagePercent = Math.Round(socketUsage, 1),
                CurrentClockGhz = Math.Round(socketClock, 2),
                BaseClockGhz = Math.Round(spec.BaseClockGhz, 2),
                TemperatureC = lhm?.TempC.HasValue == true ? Math.Round(lhm.TempC.Value, 1) : null,
                PowerWatts = lhm?.PowerW,
                CoreUsages = socketCores
            });
        }

        // Aggregate summary across all physical CPUs
        double aggUsage = hasSysTimesDelta
            ? systemTotalUsage
            : (sockets.Count > 0 ? sockets.Average(s => s.TotalUsagePercent) : 0);

        double? maxTemp = sockets.Where(s => s.TemperatureC.HasValue).Select(s => s.TemperatureC!.Value).DefaultIfEmpty().Max();
        bool hasAnyTemp = sockets.Any(s => s.TemperatureC.HasValue);

        bool hasAnyPower = sockets.Any(s => s.PowerWatts.HasValue);
        double totalPower = sockets.Where(s => s.PowerWatts.HasValue).Sum(s => s.PowerWatts!.Value);

        double aggClock = sockets.Count > 0 ? sockets.Max(s => s.CurrentClockGhz) : _baseClockGhz;
        double aggBaseClock = sockets.Count > 0 ? sockets[0].BaseClockGhz : _baseClockGhz;
        double[] combinedCores = allCoreUsages.Length > 0
            ? allCoreUsages
            : sockets.SelectMany(s => s.CoreUsages).ToArray();

        var agg = new CpuMetrics
        {
            Name = FormatAggregateCpuName(sockets.Select(s => s.Name).ToList()),
            SocketCount = Math.Max(1, sockets.Count),
            PhysicalCores = sockets.Sum(s => s.PhysicalCores),
            LogicalProcessors = Math.Max(combinedCores.Length, sockets.Sum(s => s.LogicalProcessors)),
            TotalUsagePercent = Math.Round(aggUsage, 1),
            CurrentClockGhz = Math.Round(aggClock, 2),
            BaseClockGhz = Math.Round(aggBaseClock, 2),
            TemperatureC = hasAnyTemp ? Math.Round(maxTemp ?? 0, 1) : null,
            PowerWatts = hasAnyPower ? Math.Round(totalPower, 1) : null,
            CoreUsages = combinedCores
        };

        return (agg, sockets);
    }

    private static string FormatAggregateCpuName(List<string> names)
    {
        if (names.Count == 0) return "CPU";
        if (names.Count == 1) return names[0];
        var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 1) return $"{names.Count} × {distinct[0]}";
        return string.Join(" + ", distinct);
    }

    private MemoryMetrics CollectMemory()
    {
        var mem = new Win32Interop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX>()
        };

        if (Win32Interop.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
            double totalGb = mem.ullTotalPhys / bytesPerGb;
            double availGb = mem.ullAvailPhys / bytesPerGb;
            double usedGb = Math.Max(0, totalGb - availGb);
            double pct = Math.Clamp(usedGb / totalGb * 100.0, 0, 100);

            double commitLimitGb = mem.ullTotalPageFile / bytesPerGb;
            double availCommitGb = mem.ullAvailPageFile / bytesPerGb;
            double committedGb = Math.Max(0, commitLimitGb - availCommitGb);

            return new MemoryMetrics
            {
                TotalGb = Math.Round(totalGb, 1),
                UsedGb = Math.Round(usedGb, 1),
                AvailableGb = Math.Round(availGb, 1),
                UsagePercent = Math.Round(pct, 1),
                SpeedMhz = _ramSpeedMhz,
                CommittedGb = Math.Round(committedGb, 1),
                CommitLimitGb = Math.Round(commitLimitGb, 1)
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
            var adapter = grp
                .OrderByDescending(a => pdhDedicatedByLuid.TryGetValue(a.LuidKey, out var v) ? v : 0)
                .ThenByDescending(a => pdhUsageByLuid.TryGetValue(a.LuidKey, out var u) ? u : 0)
                .First();

            double pdhLoad = grp.Max(a => pdhUsageByLuid.TryGetValue(a.LuidKey, out var u) ? u : 0);
            double pdhVramUsed = grp.Max(a => pdhDedicatedByLuid.TryGetValue(a.LuidKey, out var v) ? v : 0);

            var matchedLhm = MatchLhmGpu(adapter.Name, lhmGpus);

            // Fuse max(LHM, PDH) so NVENC/NVDEC/Compute workloads aren't hidden by low 3D load
            double load = Math.Clamp(Math.Max(matchedLhm?.LoadPercent ?? 0, pdhLoad), 0, 100);

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
                    MemoryTempC = matchedLhm?.MemoryTempC,
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
                UsagePercent = Math.Round(Math.Clamp(g.LoadPercent ?? 0, 0, 100), 1),
                VramUsedGb = Math.Round(used, 2),
                VramTotalGb = Math.Round(total, 1),
                VramUsagePercent = total > 0 ? Math.Round(Math.Clamp(used / total * 100.0, 0, 100), 1) : 0,
                TemperatureC = g.TempC,
                HotspotTempC = g.HotspotC,
                MemoryTempC = g.MemoryTempC,
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
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var selected = SelectNetworkInterfaces(NetworkInterface.GetAllNetworkInterfaces());

            foreach (var nic in selected)
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    long rx = stats.BytesReceived;
                    long tx = stats.BytesSent;
                    activeIds.Add(nic.Id);

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

            // Prune disconnected / inactive NIC IDs so dictionary never grows unbounded
            if (_prevNetBytes.Count > activeIds.Count)
            {
                var staleKeys = _prevNetBytes.Keys.Where(id => !activeIds.Contains(id)).ToList();
                foreach (var stale in staleKeys)
                {
                    _prevNetBytes.Remove(stale);
                }
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

    private static List<NetworkInterface> SelectNetworkInterfaces(NetworkInterface[] allNics)
    {
        var physical = new List<NetworkInterface>();
        var fallbackUp = new List<NetworkInterface>();

        foreach (var nic in allNics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            fallbackUp.Add(nic);

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualOrTunnelAdapter(nic.Description, nic.Name)) continue;

            physical.Add(nic);
        }

        return physical.Count > 0 ? physical : fallbackUp;
    }

    private static bool IsVirtualOrTunnelAdapter(string? description, string? name)
    {
        string desc = description ?? "";
        string nicName = name ?? "";
        foreach (var kw in VirtualAdapterKeywords)
        {
            if (desc.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                nicName.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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
                    string driveName = d.Name.TrimEnd('\\');
                    drives.Add(new DriveItem
                    {
                        Name = driveName,
                        Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel,
                        TotalGb = Math.Round(totalGb, 0),
                        UsedGb = Math.Round(usedGb, 0),
                        FreeGb = Math.Round(freeGb, 0),
                        UsagePercent = totalGb > 0 ? Math.Round(usedGb / totalGb * 100.0, 1) : 0,
                        TemperatureC = MatchDriveTemperature(driveName)
                    });
                }
            }
            catch { }

            _cachedDrives = drives;
            _lastDriveRefreshUtc = nowUtc;
        }

        // Produce immutable per-snapshot DriveItem copies with up-to-date SMART temperatures
        var drivesSnapshot = new List<DriveItem>(_cachedDrives.Count);
        foreach (var d in _cachedDrives)
        {
            drivesSnapshot.Add(new DriveItem
            {
                Name = d.Name,
                Label = d.Label,
                TotalGb = d.TotalGb,
                UsedGb = d.UsedGb,
                FreeGb = d.FreeGb,
                UsagePercent = d.UsagePercent,
                TemperatureC = MatchDriveTemperature(d.Name) ?? d.TemperatureC
            });
        }

        return new DiskMetrics
        {
            ReadBytesPerSec = Math.Round(Math.Max(0, readRate), 0),
            WriteBytesPerSec = Math.Round(Math.Max(0, writeRate), 0),
            Drives = drivesSnapshot
        };
    }

    private double? MatchDriveTemperature(string driveName)
    {
        if (_cachedStorageTemps.Count == 0) return null;

        if (_driveLetterToDiskModel.TryGetValue(driveName, out var model) && !string.IsNullOrWhiteSpace(model))
        {
            var exact = _cachedStorageTemps.FirstOrDefault(s =>
                s.TempC.HasValue &&
                (s.Name.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                 model.Contains(s.Name, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact.TempC;

            var tokens = model.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 3)
                .ToArray();
            if (tokens.Length > 0)
            {
                var best = _cachedStorageTemps
                    .Where(s => s.TempC.HasValue)
                    .Select(s => new { s.TempC, Score = tokens.Count(t => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();
                if (best != null) return best.TempC;
            }
        }

        // Single-drive fallback when only one storage device reports SMART temperature
        if (_cachedStorageTemps.Count == 1)
        {
            return _cachedStorageTemps[0].TempC;
        }

        return null;
    }

    private List<ProcessItem> CollectTopProcesses()
    {
        var nowUtc = DateTime.UtcNow;
        if (_prevProcCpu.Count > 0 &&
            (nowUtc - _lastProcSampleUtc).TotalSeconds < 0.4 &&
            _cachedTopProcs.Count > 0)
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
                    if (elapsedMs > 50 && _prevProcCpu.TryGetValue(p.Id, out var prevTime))
                    {
                        double deltaMs = (cpuTime - prevTime).TotalMilliseconds;
                        cpuPct = Math.Clamp(deltaMs / (elapsedMs * Math.Max(1, _logicalProcessors)) * 100.0, 0, 100);
                    }

                    double memMb = p.WorkingSet64 / (1024.0 * 1024.0);
                    if (cpuPct >= 0.1 || memMb >= 40)
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

        // Merge Top 6 by CPU% + Top 6 by MemoryMb (deduplicated by PID) so ProcessSortBy="mem"
        // and ProcessSortBy="cpu" both display accurate top workloads.
        var topCpu = items
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.MemoryMb)
            .Take(6);

        var topMem = items
            .OrderByDescending(p => p.MemoryMb)
            .ThenByDescending(p => p.CpuPercent)
            .Take(6);

        var merged = new List<ProcessItem>(12);
        var seenPids = new HashSet<int>();
        foreach (var item in topCpu)
        {
            if (seenPids.Add(item.Pid)) merged.Add(item);
        }
        foreach (var item in topMem)
        {
            if (seenPids.Add(item.Pid)) merged.Add(item);
        }

        _cachedTopProcs = merged;
        return _cachedTopProcs;
    }

    private SelfFootprintMetrics CollectSelfFootprint(DateTime nowUtc)
    {
        int hostPid = Environment.ProcessId;
        double elapsedMs = _prevSelfSampleUtc == DateTime.MinValue ? 0 : (nowUtc - _prevSelfSampleUtc).TotalMilliseconds;
        var nextCpu = new Dictionary<int, TimeSpan>();

        double hostCpu = 0;
        double webViewCpu = 0;
        long totalWorkingSet = 0;
        int procCount = 0;

        try
        {
            var treePids = DiscoverDescendantPids(hostPid);
            int logical = Math.Max(1, _logicalProcessors);

            foreach (int pid in treePids)
            {
                Process? p = null;
                try
                {
                    p = Process.GetProcessById(pid);
                    var cpuTime = p.TotalProcessorTime;
                    nextCpu[pid] = cpuTime;
                    totalWorkingSet += Math.Max(0, p.WorkingSet64);
                    procCount++;

                    if (elapsedMs > 50 && _prevSelfProcCpu.TryGetValue(pid, out var prevTime))
                    {
                        double deltaMs = Math.Max(0, (cpuTime - prevTime).TotalMilliseconds);
                        double pct = Math.Clamp(deltaMs / (elapsedMs * logical) * 100.0, 0, 100);
                        if (pid == hostPid) hostCpu += pct;
                        else webViewCpu += pct;
                    }
                }
                catch { }
                finally
                {
                    p?.Dispose();
                }
            }
        }
        catch { }

        _prevSelfProcCpu = nextCpu;
        _prevSelfSampleUtc = nowUtc;

        double totalCpu = hostCpu + webViewCpu;
        return new SelfFootprintMetrics
        {
            HostCpuPercent = Math.Round(hostCpu, 2),
            WebViewCpuPercent = Math.Round(webViewCpu, 2),
            TotalCpuPercent = Math.Round(totalCpu, 2),
            TotalMemoryMb = Math.Round(totalWorkingSet / (1024.0 * 1024.0), 1),
            ProcessCount = Math.Max(1, procCount)
        };
    }

    private static List<int> DiscoverDescendantPids(int rootPid)
    {
        var result = new List<int> { rootPid };
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == InvalidHandleValue)
            return result;

        try
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };

            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    int pid = (int)entry.th32ProcessID;
                    int ppid = (int)entry.th32ParentProcessID;
                    if (pid > 0 && pid != ppid)
                    {
                        if (!childrenByParent.TryGetValue(ppid, out var list))
                        {
                            list = new List<int>();
                            childrenByParent[ppid] = list;
                        }
                        list.Add(pid);
                    }
                }
                while (Process32NextW(snap, ref entry));
            }

            var visited = new HashSet<int> { rootPid };
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (!childrenByParent.TryGetValue(cur, out var kids)) continue;
                foreach (int kid in kids)
                {
                    if (visited.Add(kid))
                    {
                        result.Add(kid);
                        queue.Enqueue(kid);
                    }
                }
            }
        }
        catch { }
        finally
        {
            Win32Interop.CloseHandle(snap);
        }

        return result;
    }

    private static LhmGpuData? MatchLhmGpu(string dxgiName, List<LhmGpuData> lhmGpus)
    {
        if (lhmGpus.Count == 0) return null;
        var cleanDxgi = CleanHardwareName(dxgiName);

        var exact = lhmGpus.FirstOrDefault(g =>
            string.Equals(CleanHardwareName(g.Name), cleanDxgi, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

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

    private static string NormalizeSensorMatchKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        Span<char> buf = stackalloc char[Math.Min(name.Length, 64)];
        int len = 0;
        for (int i = 0; i < name.Length && len < buf.Length; i++)
        {
            char ch = name[i];
            if (char.IsLetterOrDigit(ch))
            {
                buf[len++] = char.ToLowerInvariant(ch);
            }
        }
        return new string(buf[..len]);
    }

    private static string ExtractLuidKey(string instanceName)
    {
        int idx = instanceName.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
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
