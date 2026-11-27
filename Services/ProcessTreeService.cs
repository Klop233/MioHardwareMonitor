using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Process Hacker / System Informer style Win32 process & thread tree collector.
/// Uses Toolhelp32 snapshots + lightweight native Win32 queries (GetProcessTimes, GetProcessMemoryInfo,
/// GetProcessIoCounters, GetProcessHandleCount, QueryFullProcessImageNameW, Thread32,
/// NtQueryInformationThread ThreadQuerySetWin32StartAddress, Module32 symbol resolution).
/// Every native handle is deterministically closed in a finally block.
/// Zero background threads or overhead unless Collect() is explicitly called.
/// </summary>
public sealed class ProcessTreeService : IDisposable
{
    #region Win32 Interop Constants & Structs

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint TH32CS_SNAPTHREAD = 0x00000004;
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const uint THREAD_QUERY_INFORMATION = 0x0040;
    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int ThreadQuerySetWin32StartAddress = 9;
    private const int THREAD_PRIORITY_ERROR_RETURN = int.MaxValue;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_BAD_LENGTH = 24;

    private const uint IDLE_PRIORITY_CLASS = 0x00000040;
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    private const uint HIGH_PRIORITY_CLASS = 0x00000080;
    private const uint REALTIME_PRIORITY_CLASS = 0x00000100;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", EntryPoint = "Module32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out FILETIME lpCreationTime,
        out FILETIME lpExitTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(
        IntPtr hThread,
        out FILETIME lpCreationTime,
        out FILETIME lpExitTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr hProcess,
        out PROCESS_MEMORY_COUNTERS_EX ppsmemCounters,
        uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadPriority(IntPtr hThread);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out TOKEN_ELEVATION tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(
        IntPtr threadHandle,
        int threadInformationClass,
        out IntPtr threadInformation,
        int threadInformationLength,
        out int returnLength);

    #endregion

    private const int MaxReturnedProcesses = 400;
    private const int MaxReturnedThreads = 120;
    private static readonly TimeSpan NewLifecycleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitedGhostDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ModuleCacheTtl = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "memory compression",
        "secure system",
        "smss",
        "csrss",
        "lsass",
        "wininit",
        "winlogon",
        "services"
    };

    private static readonly HashSet<string> SystemCategoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "memory compression",
        "secure system",
        "smss",
        "csrss",
        "lsass",
        "wininit",
        "winlogon",
        "services",
        "fontdrvhost",
        "dwm",
        "sihost",
        "lsaiso"
    };

    private sealed class ProcessDeltaSample
    {
        public ulong CreateTime { get; init; }
        public ulong TotalCpu100ns { get; init; }
        public ulong IoReadBytes { get; init; }
        public ulong IoWriteBytes { get; init; }
        public long StampTicks { get; init; }
        public DateTime FirstSeenUtc { get; init; }
        public bool? IsElevated { get; init; }
        public string ImagePath { get; init; } = "";
        public ProcessTreeNode LastNode { get; init; } = null!;
    }

    private sealed class ExitedGhost
    {
        public DateTime ExitedUtc { get; init; }
        public ProcessTreeNode Node { get; init; } = null!;
    }

    private sealed class ThreadDeltaSample
    {
        public ulong CreateTime { get; init; }
        public ulong Kernel100ns { get; init; }
        public ulong User100ns { get; init; }
        public long StampTicks { get; init; }
    }

    private sealed record ModuleRange(ulong BaseAddr, ulong EndAddr, string Name);

    private sealed class LiveProcessRecord
    {
        public int Pid { get; init; }
        public int RawParentPid { get; init; }
        public int ValidParentPid { get; set; }
        public ulong CreateTime { get; init; }
        public string Name { get; init; } = "";
        public string ImagePath { get; init; } = "";
        public double CpuPercent { get; init; }
        public double WorkingSetMb { get; init; }
        public double PrivateMb { get; init; }
        public double IoReadBytesPerSec { get; init; }
        public double IoWriteBytesPerSec { get; init; }
        public ulong TotalIoReadBytes { get; init; }
        public ulong TotalIoWriteBytes { get; init; }
        public int ThreadCount { get; init; }
        public int HandleCount { get; init; }
        public string Priority { get; init; } = "";
        public string Category { get; set; } = "normal";
        public string Lifecycle { get; init; } = "normal";
        public bool AccessDenied { get; init; }
        public bool IsExitedGhost { get; init; }
        public bool? IsElevated { get; init; }
        public List<LiveProcessRecord> Children { get; } = new();
        public double SubtreeMetric { get; set; }
    }

    private readonly object _sync = new();
    private readonly int _selfPid = Environment.ProcessId;
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

    private Dictionary<int, ProcessDeltaSample> _prevProcesses = new();
    private readonly Dictionary<int, ExitedGhost> _exitedGhosts = new();
    private bool _hasBaseline;

    // Thread delta & 5-second module cache for the active inspected process
    private int _lastThreadInspectPid;
    private readonly Dictionary<int, ThreadDeltaSample> _prevThreads = new();
    private int _moduleCachePid;
    private ulong _moduleCacheCreateTime;
    private DateTime _moduleCacheUtc = DateTime.MinValue;
    private List<ModuleRange> _moduleCache = new();
    private int _lastFollowedPid;
    private bool _disposed;

    public bool HasBaseline
    {
        get { lock (_sync) return _hasBaseline; }
    }

    public void ResetBaseline()
    {
        lock (_sync)
        {
            _prevProcesses.Clear();
            _exitedGhosts.Clear();
            _prevThreads.Clear();
            _moduleCache.Clear();
            _moduleCachePid = 0;
            _moduleCacheCreateTime = 0;
            _moduleCacheUtc = DateTime.MinValue;
            _lastThreadInspectPid = 0;
            _lastFollowedPid = 0;
            _hasBaseline = false;
        }
    }

    public ProcessTreeSnapshot Collect(
        string viewMode,
        string sortBy,
        bool autoFollowTop,
        int pinnedPid,
        string filter)
    {
        lock (_sync)
        {
            string normView = SettingsService.NormalizeProcessTreeViewMode(viewMode);
            string normSort = SettingsService.NormalizeProcessTreeSortBy(sortBy);
            string normFilter = SettingsService.NormalizeProcessTreeFilter(filter);

            long nowTicks = Stopwatch.GetTimestamp();
            DateTime nowUtc = DateTime.UtcNow;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var liveByPid = new Dictionary<int, LiveProcessRecord>(320);
            var nextPrev = new Dictionary<int, ProcessDeltaSample>(320);

            int totalProcesses = 0;
            int totalThreads = 0;
            int totalHandles = 0;

            var pathBuffer = new char[1024];

            IntPtr hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (hSnap != IntPtr.Zero && hSnap != INVALID_HANDLE_VALUE)
            {
                try
                {
                    var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                    if (Process32FirstW(hSnap, ref pe))
                    {
                        do
                        {
                            int pid = (int)pe.th32ProcessID;
                            if (pid == 0) continue; // Skip System Idle Process

                            int parentPid = (int)pe.th32ParentProcessID;
                            int threads = (int)pe.cntThreads;
                            string name = string.IsNullOrWhiteSpace(pe.szExeFile) ? $"PID {pid}" : pe.szExeFile.Trim();

                            totalProcesses++;
                            totalThreads += threads;

                            _prevProcesses.TryGetValue(pid, out var prev);

                            ulong createTime = 0;
                            ulong totalCpu100ns = 0;
                            ulong ioRead = 0;
                            ulong ioWrite = 0;
                            double wsMb = 0;
                            double privMb = 0;
                            int handles = 0;
                            string priority = MapBasePriority(pe.pcPriClassBase);
                            string imagePath = "";
                            bool accessDenied = false;
                            bool? isElevated = null;

                            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
                            if (hProc != IntPtr.Zero)
                            {
                                try
                                {
                                    if (GetProcessTimes(hProc, out var cFt, out _, out var kFt, out var uFt))
                                    {
                                        createTime = cFt.ToUInt64();
                                        totalCpu100ns = kFt.ToUInt64() + uFt.ToUInt64();
                                    }

                                    var mem = new PROCESS_MEMORY_COUNTERS_EX
                                    {
                                        cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>()
                                    };
                                    if (GetProcessMemoryInfo(hProc, out mem, mem.cb))
                                    {
                                        wsMb = Math.Round(mem.WorkingSetSize / (1024.0 * 1024.0), 1);
                                        privMb = Math.Round(mem.PrivateUsage / (1024.0 * 1024.0), 1);
                                    }

                                    if (GetProcessIoCounters(hProc, out var io))
                                    {
                                        ioRead = io.ReadTransferCount;
                                        ioWrite = io.WriteTransferCount;
                                    }

                                    if (GetProcessHandleCount(hProc, out uint hCount))
                                    {
                                        handles = (int)Math.Min(hCount, int.MaxValue);
                                    }

                                    uint priClass = GetPriorityClass(hProc);
                                    if (priClass != 0)
                                    {
                                        priority = MapPriorityClass(priClass);
                                    }

                                    bool sameInstance = prev != null && prev.CreateTime == createTime && createTime != 0;
                                    if (sameInstance && !string.IsNullOrEmpty(prev!.ImagePath))
                                    {
                                        imagePath = prev.ImagePath;
                                    }
                                    else
                                    {
                                        uint pathLen = (uint)pathBuffer.Length;
                                        if (QueryFullProcessImageNameW(hProc, 0, pathBuffer, ref pathLen) && pathLen > 0)
                                        {
                                            imagePath = new string(pathBuffer, 0, (int)pathLen);
                                        }
                                    }

                                    if (sameInstance && prev!.IsElevated.HasValue)
                                    {
                                        isElevated = prev.IsElevated;
                                    }
                                    else
                                    {
                                        isElevated = QueryIsElevated(hProc);
                                    }
                                }
                                finally
                                {
                                    CloseHandle(hProc);
                                }
                            }
                            else
                            {
                                accessDenied = true;
                            }

                            totalHandles += handles;

                            bool isSamePrev = prev != null && (prev.CreateTime == createTime || (prev.CreateTime == 0 && createTime == 0));
                            double cpuPct = 0;
                            double readBps = 0;
                            double writeBps = 0;
                            DateTime firstSeenUtc;

                            if (isSamePrev && prev != null)
                            {
                                firstSeenUtc = prev.FirstSeenUtc;
                                double dtSec = (nowTicks - prev.StampTicks) / (double)Stopwatch.Frequency;
                                if (dtSec >= 0.05)
                                {
                                    if (totalCpu100ns >= prev.TotalCpu100ns && totalCpu100ns > 0)
                                    {
                                        double delta100ns = totalCpu100ns - prev.TotalCpu100ns;
                                        double denom = dtSec * 10_000_000.0 * _processorCount;
                                        cpuPct = Math.Round(Math.Clamp(delta100ns / denom * 100.0, 0.0, 100.0), 1);
                                    }
                                    if (ioRead >= prev.IoReadBytes)
                                    {
                                        readBps = Math.Round((ioRead - prev.IoReadBytes) / dtSec, 0);
                                    }
                                    if (ioWrite >= prev.IoWriteBytes)
                                    {
                                        writeBps = Math.Round((ioWrite - prev.IoWriteBytes) / dtSec, 0);
                                    }
                                }
                            }
                            else
                            {
                                firstSeenUtc = _hasBaseline ? nowUtc : nowUtc - TimeSpan.FromMinutes(1);
                            }

                            string lifecycle = (_hasBaseline && (nowUtc - firstSeenUtc) <= NewLifecycleDuration)
                                ? "new"
                                : "normal";

                            // Revived PID removes any old ghost
                            _exitedGhosts.Remove(pid);

                            var rec = new LiveProcessRecord
                            {
                                Pid = pid,
                                RawParentPid = parentPid,
                                CreateTime = createTime,
                                Name = name,
                                ImagePath = imagePath,
                                CpuPercent = cpuPct,
                                WorkingSetMb = wsMb,
                                PrivateMb = privMb,
                                IoReadBytesPerSec = readBps,
                                IoWriteBytesPerSec = writeBps,
                                TotalIoReadBytes = ioRead,
                                TotalIoWriteBytes = ioWrite,
                                ThreadCount = threads,
                                HandleCount = handles,
                                Priority = priority,
                                Lifecycle = lifecycle,
                                AccessDenied = accessDenied,
                                IsElevated = isElevated
                            };

                            liveByPid[pid] = rec;

                            nextPrev[pid] = new ProcessDeltaSample
                            {
                                CreateTime = createTime,
                                TotalCpu100ns = totalCpu100ns,
                                IoReadBytes = ioRead,
                                IoWriteBytes = ioWrite,
                                StampTicks = nowTicks,
                                FirstSeenUtc = firstSeenUtc,
                                IsElevated = isElevated,
                                ImagePath = imagePath,
                                LastNode = ToNode(rec, 0, false, false, "")
                            };
                        }
                        while (Process32NextW(hSnap, ref pe));
                    }
                }
                finally
                {
                    CloseHandle(hSnap);
                }
            }

            // Detect newly exited processes -> short-lived red ghost entries (<= 2s)
            if (_hasBaseline)
            {
                foreach (var kv in _prevProcesses)
                {
                    if (!liveByPid.ContainsKey(kv.Key))
                    {
                        var ghostNode = kv.Value.LastNode;
                        _exitedGhosts[kv.Key] = new ExitedGhost
                        {
                            ExitedUtc = nowUtc,
                            Node = new ProcessTreeNode
                            {
                                Pid = ghostNode.Pid,
                                ParentPid = ghostNode.ParentPid,
                                Name = ghostNode.Name,
                                ImagePath = ghostNode.ImagePath,
                                CpuPercent = 0,
                                WorkingSetMb = ghostNode.WorkingSetMb,
                                PrivateMb = ghostNode.PrivateMb,
                                IoReadBytesPerSec = 0,
                                IoWriteBytesPerSec = 0,
                                ThreadCount = 0,
                                HandleCount = 0,
                                Priority = ghostNode.Priority,
                                Category = ghostNode.Category,
                                Lifecycle = "exited"
                            }
                        };
                    }
                }
            }

            // Prune expired ghosts (> 2s)
            var expiredGhostPids = _exitedGhosts
                .Where(kv => (nowUtc - kv.Value.ExitedUtc) > ExitedGhostDuration)
                .Select(kv => kv.Key)
                .ToList();
            foreach (int ep in expiredGhostPids)
            {
                _exitedGhosts.Remove(ep);
            }

            // Resolve PID-reuse-safe parent relationships & categories
            foreach (var rec in liveByPid.Values)
            {
                if (rec.RawParentPid > 0 &&
                    rec.RawParentPid != rec.Pid &&
                    liveByPid.TryGetValue(rec.RawParentPid, out var parent) &&
                    IsValidParent(parent, rec))
                {
                    rec.ValidParentPid = parent.Pid;
                }
                else
                {
                    rec.ValidParentPid = 0;
                }
            }

            // Break any accidental cycles (belt-and-suspenders guard)
            foreach (var rec in liveByPid.Values)
            {
                if (rec.ValidParentPid == 0) continue;
                var visited = new HashSet<int> { rec.Pid };
                int cur = rec.ValidParentPid;
                int depthGuard = 0;
                while (cur != 0 && depthGuard < 64)
                {
                    if (!visited.Add(cur))
                    {
                        rec.ValidParentPid = 0;
                        break;
                    }
                    if (!liveByPid.TryGetValue(cur, out var anc)) break;
                    cur = anc.ValidParentPid;
                    depthGuard++;
                }
                if (depthGuard >= 64)
                {
                    rec.ValidParentPid = 0;
                }
            }

            // Assign category (self / system / service / elevated / normal)
            foreach (var rec in liveByPid.Values)
            {
                string parentName = rec.ValidParentPid > 0 && liveByPid.TryGetValue(rec.ValidParentPid, out var p)
                    ? p.Name
                    : "";
                rec.Category = DetermineCategory(rec.Pid, rec.Name, parentName, rec.IsElevated);
                if (nextPrev.TryGetValue(rec.Pid, out var prevSample))
                {
                    prevSample.LastNode.Category = rec.Category;
                    prevSample.LastNode.ParentPid = rec.ValidParentPid;
                }
            }

            _prevProcesses = nextPrev;
            _hasBaseline = true;

            // Combine live processes + active exited ghosts into display records
            var displayRecords = new Dictionary<int, LiveProcessRecord>(liveByPid.Count + _exitedGhosts.Count);
            foreach (var kv in liveByPid)
            {
                displayRecords[kv.Key] = kv.Value;
            }
            foreach (var kv in _exitedGhosts)
            {
                if (displayRecords.ContainsKey(kv.Key)) continue;
                var g = kv.Value.Node;
                displayRecords[kv.Key] = new LiveProcessRecord
                {
                    Pid = g.Pid,
                    RawParentPid = g.ParentPid,
                    ValidParentPid = displayRecords.ContainsKey(g.ParentPid) ? g.ParentPid : 0,
                    Name = g.Name,
                    ImagePath = g.ImagePath,
                    WorkingSetMb = g.WorkingSetMb,
                    PrivateMb = g.PrivateMb,
                    Priority = g.Priority,
                    Category = g.Category,
                    Lifecycle = "exited",
                    IsExitedGhost = true
                };
            }

            // Build filtered + ordered ProcessTreeNode list
            var nodes = normView == "flat"
                ? BuildFlatList(displayRecords.Values, normSort, normFilter)
                : BuildTreeList(displayRecords, normSort, normFilter);

            // Resolve active inspected PID
            int activePid = ResolveActivePid(liveByPid, nodes, autoFollowTop, pinnedPid, normFilter, out bool pinnedTargetActive);

            // Inspect threads & detail for the single active process
            ProcessDetailInfo? selectedDetail = null;
            if (activePid > 0 && liveByPid.TryGetValue(activePid, out var activeRec))
            {
                string parentName = activeRec.ValidParentPid > 0 && liveByPid.TryGetValue(activeRec.ValidParentPid, out var par)
                    ? par.Name
                    : (activeRec.RawParentPid > 0 ? $"PID {activeRec.RawParentPid}（已退出）" : "—");
                bool isOwnDescendant = IsDescendantOfSelf(activeRec, liveByPid);
                selectedDetail = BuildProcessDetail(activeRec, parentName, isOwnDescendant, nowTicks, nowUtc);
            }

            return new ProcessTreeSnapshot
            {
                TimestampMs = nowMs,
                TotalProcesses = totalProcesses,
                TotalThreads = totalThreads,
                TotalHandles = totalHandles,
                ViewMode = normView,
                SortBy = normSort,
                AutoFollowTop = autoFollowTop,
                PinnedPid = autoFollowTop ? 0 : Math.Max(0, pinnedPid),
                PinnedTargetActive = pinnedTargetActive,
                ActivePid = activePid,
                Filter = normFilter,
                Processes = nodes,
                SelectedProcess = selectedDetail
            };
        }
    }

    public bool TryOpenProcessLocation(int pid, out string error)
    {
        error = "";
        if (pid <= 4)
        {
            error = "系统内核进程没有对应的磁盘可执行文件。";
            return false;
        }

        string path = QueryProcessImagePath(pid);
        if (string.IsNullOrWhiteSpace(path))
        {
            lock (_sync)
            {
                if (_prevProcesses.TryGetValue(pid, out var cached))
                    path = cached.ImagePath;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "无法读取该进程路径（可能是系统受保护进程，或进程已退出）。";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"找不到可执行文件：{path}";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"打开文件目录失败：{ex.Message}";
            return false;
        }
    }

    public bool TryTerminateProcessSafe(int pid, out string error)
    {
        error = "";
        if (pid <= 4)
        {
            error = "禁止结束系统核心进程（PID ≤ 4）。";
            return false;
        }

        if (pid == _selfPid)
        {
            error = "不能结束星澪硬件监控自身进程。";
            return false;
        }

        // Always query live OS state first to prevent PID-reuse TOCTOU against cached snapshots
        string liveImgPath = QueryProcessImagePath(pid);
        string liveImgName = !string.IsNullOrWhiteSpace(liveImgPath) ? Path.GetFileName(liveImgPath) : "";
        QueryLiveProcessSafetyInfo(pid, out string liveSnapName, out bool isOwnDescendant);

        string procName = !string.IsNullOrWhiteSpace(liveImgName) ? liveImgName : liveSnapName;
        if (string.IsNullOrWhiteSpace(procName))
        {
            lock (_sync)
            {
                if (_prevProcesses.TryGetValue(pid, out var cached))
                    procName = cached.LastNode.Name;
            }
        }

        if (isOwnDescendant)
        {
            error = "不能结束星澪硬件监控自身的组件子进程（如 WebView2）。";
            return false;
        }

        if (IsProtectedProcess(pid, liveImgName) || IsProtectedProcess(pid, liveSnapName) || IsProtectedProcess(pid, procName))
        {
            string displayProtected = !string.IsNullOrWhiteSpace(procName) ? procName : $"PID {pid}";
            error = $"禁止结束受保护的关键系统进程（{displayProtected}）。";
            return false;
        }

        IntPtr hProc = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        bool canVerifyOnHandle = hProc != IntPtr.Zero;
        if (hProc == IntPtr.Zero)
        {
            hProc = OpenProcess(PROCESS_TERMINATE, false, (uint)pid);
        }
        if (hProc == IntPtr.Zero)
        {
            int errCode = Marshal.GetLastWin32Error();
            error = errCode == ERROR_ACCESS_DENIED
                ? "权限不足，请以管理员身份运行后重试。"
                : $"无法打开进程 PID {pid}（可能已退出，错误码 {errCode}）。";
            return false;
        }

        try
        {
            if (canVerifyOnHandle)
            {
                string handlePath = QueryProcessImagePathByHandle(hProc);
                string handleName = !string.IsNullOrWhiteSpace(handlePath) ? Path.GetFileName(handlePath) : "";
                if (IsProtectedProcess(pid, handleName))
                {
                    error = $"禁止结束受保护的关键系统进程（{handleName}）。";
                    return false;
                }
            }

            if (!TerminateProcess(hProc, 1))
            {
                int errCode = Marshal.GetLastWin32Error();
                error = errCode == ERROR_ACCESS_DENIED
                    ? "权限不足，系统拒绝结束该进程。"
                    : $"结束进程失败（错误码 {errCode}）。";
                return false;
            }
            return true;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    private bool IsDescendantOfSelf(LiveProcessRecord rec, Dictionary<int, LiveProcessRecord> liveByPid)
    {
        int cur = rec.ValidParentPid;
        int guard = 0;
        while (cur > 0 && guard < 32)
        {
            if (cur == _selfPid) return true;
            if (!liveByPid.TryGetValue(cur, out var parent)) break;
            cur = parent.ValidParentPid;
            guard++;
        }
        return false;
    }

    private void QueryLiveProcessSafetyInfo(int targetPid, out string exeName, out bool isOwnDescendant)
    {
        exeName = "";
        isOwnDescendant = false;

        IntPtr hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnap == IntPtr.Zero || hSnap == INVALID_HANDLE_VALUE)
            return;

        var parentByPid = new Dictionary<int, int>(256);
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(hSnap, ref pe))
            {
                do
                {
                    int pid = (int)pe.th32ProcessID;
                    if (pid == 0) continue;
                    parentByPid[pid] = (int)pe.th32ParentProcessID;
                    if (pid == targetPid && !string.IsNullOrWhiteSpace(pe.szExeFile))
                    {
                        exeName = pe.szExeFile.Trim();
                    }
                }
                while (Process32NextW(hSnap, ref pe));
            }
        }
        finally
        {
            CloseHandle(hSnap);
        }

        if (!parentByPid.TryGetValue(targetPid, out int parentPid))
            return;

        // Walk live ancestor chain using creation-time verification so PID reuse cannot cause false positives
        ulong childCreateTime = QueryProcessCreateTime(targetPid);
        var visited = new HashSet<int> { targetPid };
        int cur = parentPid;
        int guard = 0;
        while (cur > 0 && guard < 32 && visited.Add(cur))
        {
            ulong curCreateTime = QueryProcessCreateTime(cur);
            if (childCreateTime > 0 && curCreateTime > 0 && curCreateTime > childCreateTime)
                break;

            if (cur == _selfPid)
            {
                isOwnDescendant = true;
                return;
            }

            childCreateTime = curCreateTime;
            if (!parentByPid.TryGetValue(cur, out cur))
                break;
            guard++;
        }
    }

    private static ulong QueryProcessCreateTime(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProc == IntPtr.Zero) return 0;
        try
        {
            return GetProcessTimes(hProc, out var cFt, out _, out _, out _) ? cFt.ToUInt64() : 0;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    private static string QueryProcessImagePathByHandle(IntPtr hProc)
    {
        var buf = new char[1024];
        uint len = (uint)buf.Length;
        return QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0
            ? new string(buf, 0, (int)len)
            : "";
    }

    public static bool IsProtectedProcess(int pid, string? name)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return true;
        string norm = NormalizeExeName(name);
        return !string.IsNullOrEmpty(norm) && ProtectedProcessNames.Contains(norm);
    }

    private static bool IsValidParent(LiveProcessRecord parent, LiveProcessRecord child)
    {
        if (parent.Pid == child.Pid) return false;
        if (parent.CreateTime == 0 || child.CreateTime == 0) return true;
        return parent.CreateTime <= child.CreateTime;
    }

    private string DetermineCategory(int pid, string name, string parentName, bool? isElevated)
    {
        if (pid == _selfPid) return "self";
        string normName = NormalizeExeName(name);
        if (pid <= 4 || SystemCategoryNames.Contains(normName)) return "system";
        if (string.Equals(normName, "svchost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeExeName(parentName), "services", StringComparison.OrdinalIgnoreCase))
        {
            return "service";
        }
        if (isElevated == true) return "elevated";
        return "normal";
    }

    private static string NormalizeExeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^4];
        return trimmed;
    }

    private List<ProcessTreeNode> BuildFlatList(
        IEnumerable<LiveProcessRecord> allRecords,
        string sortBy,
        string filter)
    {
        IEnumerable<LiveProcessRecord> query = allRecords;
        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(r => MatchesFilter(r, filter));
        }

        var sorted = SortFlat(query, sortBy).Take(MaxReturnedProcesses);
        var result = new List<ProcessTreeNode>(Math.Min(MaxReturnedProcesses, 256));
        foreach (var rec in sorted)
        {
            result.Add(ToNode(rec, depth: 0, hasChildren: false, isLastSibling: false, treeGuides: ""));
        }
        return result;
    }

    private List<ProcessTreeNode> BuildTreeList(
        Dictionary<int, LiveProcessRecord> records,
        string sortBy,
        string filter)
    {
        foreach (var rec in records.Values)
        {
            rec.Children.Clear();
            rec.SubtreeMetric = 0;
        }

        // Determine which nodes should be included when filter is active (matches + all ancestors)
        HashSet<int>? allowedPids = null;
        if (!string.IsNullOrEmpty(filter))
        {
            allowedPids = new HashSet<int>();
            foreach (var rec in records.Values)
            {
                if (!MatchesFilter(rec, filter)) continue;
                int cur = rec.Pid;
                int guard = 0;
                while (cur != 0 && guard < 64 && allowedPids.Add(cur))
                {
                    if (records.TryGetValue(cur, out var node))
                        cur = node.ValidParentPid;
                    else
                        break;
                    guard++;
                }
            }
        }

        var roots = new List<LiveProcessRecord>();
        foreach (var rec in records.Values)
        {
            if (allowedPids != null && !allowedPids.Contains(rec.Pid)) continue;
            if (rec.ValidParentPid > 0 &&
                records.TryGetValue(rec.ValidParentPid, out var parent) &&
                (allowedPids == null || allowedPids.Contains(parent.Pid)))
            {
                parent.Children.Add(rec);
            }
            else
            {
                roots.Add(rec);
            }
        }

        bool useSubtreeSort = sortBy is "cpu" or "mem" or "io" or "threads" or "handles";
        if (useSubtreeSort)
        {
            foreach (var root in roots)
            {
                ComputeSubtreeMetric(root, sortBy, depth: 0);
            }
        }

        SortSiblings(roots, sortBy, useSubtreeSort);

        var output = new List<ProcessTreeNode>(Math.Min(MaxReturnedProcesses, records.Count));
        var ancestorHasMore = new List<bool>(16);

        for (int i = 0; i < roots.Count && output.Count < MaxReturnedProcesses; i++)
        {
            bool isLast = i == roots.Count - 1;
            AppendTreeDfs(roots[i], depth: 0, isLastSibling: isLast, ancestorHasMore, sortBy, useSubtreeSort, output);
        }

        return output;
    }

    private static double ComputeSubtreeMetric(LiveProcessRecord node, string sortBy, int depth)
    {
        double selfVal = DirectMetric(node, sortBy);
        if (depth >= 48 || node.Children.Count == 0)
        {
            node.SubtreeMetric = selfVal;
            return selfVal;
        }

        double total = selfVal;
        foreach (var child in node.Children)
        {
            total += ComputeSubtreeMetric(child, sortBy, depth + 1);
        }
        node.SubtreeMetric = total;
        return total;
    }

    private static double DirectMetric(LiveProcessRecord r, string sortBy) => sortBy switch
    {
        "mem" => r.WorkingSetMb,
        "io" => r.IoReadBytesPerSec + r.IoWriteBytesPerSec,
        "threads" => r.ThreadCount,
        "handles" => r.HandleCount,
        _ => r.CpuPercent
    };

    private static void SortSiblings(List<LiveProcessRecord> list, string sortBy, bool useSubtreeSort)
    {
        if (list.Count <= 1) return;

        list.Sort((a, b) =>
        {
            if (sortBy == "pid") return a.Pid.CompareTo(b.Pid);
            if (sortBy == "name")
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return cmp != 0 ? cmp : a.Pid.CompareTo(b.Pid);
            }

            if (useSubtreeSort)
            {
                int subCmp = b.SubtreeMetric.CompareTo(a.SubtreeMetric);
                if (subCmp != 0) return subCmp;
            }

            int dirCmp = DirectMetric(b, sortBy).CompareTo(DirectMetric(a, sortBy));
            if (dirCmp != 0) return dirCmp;

            int memCmp = b.WorkingSetMb.CompareTo(a.WorkingSetMb);
            return memCmp != 0 ? memCmp : a.Pid.CompareTo(b.Pid);
        });
    }

    private static IEnumerable<LiveProcessRecord> SortFlat(IEnumerable<LiveProcessRecord> records, string sortBy) => sortBy switch
    {
        "mem" => records.OrderByDescending(r => r.WorkingSetMb).ThenByDescending(r => r.PrivateMb).ThenBy(r => r.Pid),
        "io" => records.OrderByDescending(r => r.IoReadBytesPerSec + r.IoWriteBytesPerSec).ThenByDescending(r => r.CpuPercent).ThenBy(r => r.Pid),
        "threads" => records.OrderByDescending(r => r.ThreadCount).ThenByDescending(r => r.CpuPercent).ThenBy(r => r.Pid),
        "handles" => records.OrderByDescending(r => r.HandleCount).ThenByDescending(r => r.CpuPercent).ThenBy(r => r.Pid),
        "pid" => records.OrderBy(r => r.Pid),
        "name" => records.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
        _ => records.OrderByDescending(r => r.CpuPercent).ThenByDescending(r => r.WorkingSetMb).ThenBy(r => r.Pid)
    };

    private static void AppendTreeDfs(
        LiveProcessRecord rec,
        int depth,
        bool isLastSibling,
        List<bool> ancestorHasMore,
        string sortBy,
        bool useSubtreeSort,
        List<ProcessTreeNode> output)
    {
        if (output.Count >= MaxReturnedProcesses) return;

        string guides = BuildTreeGuides(depth, isLastSibling, ancestorHasMore);
        output.Add(ToNode(rec, depth, rec.Children.Count > 0, isLastSibling, guides));

        if (rec.Children.Count == 0 || depth >= 32) return;

        SortSiblings(rec.Children, sortBy, useSubtreeSort);
        if (depth >= 1)
        {
            ancestorHasMore.Add(!isLastSibling);
        }

        for (int i = 0; i < rec.Children.Count && output.Count < MaxReturnedProcesses; i++)
        {
            bool childLast = i == rec.Children.Count - 1;
            AppendTreeDfs(rec.Children[i], depth + 1, childLast, ancestorHasMore, sortBy, useSubtreeSort, output);
        }

        if (depth >= 1 && ancestorHasMore.Count > 0)
        {
            ancestorHasMore.RemoveAt(ancestorHasMore.Count - 1);
        }
    }

    private static string BuildTreeGuides(int depth, bool isLastSibling, List<bool> ancestorHasMore)
    {
        if (depth <= 0) return "";
        var sb = new System.Text.StringBuilder(depth * 3);
        for (int i = 0; i < ancestorHasMore.Count; i++)
        {
            sb.Append(ancestorHasMore[i] ? "│  " : "   ");
        }
        sb.Append(isLastSibling ? "└─ " : "├─ ");
        return sb.ToString();
    }

    private static bool MatchesFilter(LiveProcessRecord rec, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        if (rec.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(rec.ImagePath) && rec.ImagePath.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (rec.Pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private int ResolveActivePid(
        Dictionary<int, LiveProcessRecord> liveByPid,
        List<ProcessTreeNode> visibleNodes,
        bool autoFollowTop,
        int pinnedPid,
        string filter,
        out bool pinnedTargetActive)
    {
        if (!autoFollowTop && pinnedPid > 0 && liveByPid.ContainsKey(pinnedPid))
        {
            pinnedTargetActive = true;
            _lastFollowedPid = pinnedPid;
            return pinnedPid;
        }

        pinnedTargetActive = false;

        // Candidate pool: if filter is set, prefer matching visible live processes; otherwise all live processes
        IEnumerable<LiveProcessRecord> candidates = liveByPid.Values;
        if (!string.IsNullOrEmpty(filter))
        {
            var matching = liveByPid.Values.Where(r => MatchesFilter(r, filter)).ToList();
            if (matching.Count > 0) candidates = matching;
        }

        var top = candidates
            .OrderByDescending(r => r.CpuPercent)
            .ThenByDescending(r => r.WorkingSetMb)
            .FirstOrDefault();

        if (top == null) return 0;

        // Mild anti-jitter hysteresis during idle moments (< 1.5% CPU) so thread view stays readable
        if ((autoFollowTop || !pinnedTargetActive) &&
            _lastFollowedPid > 0 &&
            liveByPid.TryGetValue(_lastFollowedPid, out var prevFollowed) &&
            (string.IsNullOrEmpty(filter) || MatchesFilter(prevFollowed, filter)) &&
            top.CpuPercent < 1.5 &&
            (top.CpuPercent - prevFollowed.CpuPercent) < 0.5)
        {
            return prevFollowed.Pid;
        }

        _lastFollowedPid = top.Pid;
        return top.Pid;
    }

    private ProcessDetailInfo BuildProcessDetail(
        LiveProcessRecord rec,
        string parentName,
        bool isOwnDescendant,
        long nowTicks,
        DateTime nowUtc)
    {
        string startText = "—";
        long uptimeSec = 0;
        if (rec.CreateTime > 0)
        {
            try
            {
                var createdUtc = DateTime.FromFileTimeUtc((long)rec.CreateTime);
                var createdLocal = createdUtc.ToLocalTime();
                var today = DateTime.Now.Date;
                startText = createdLocal.Date == today
                    ? createdLocal.ToString("HH:mm:ss")
                    : createdLocal.ToString("MM-dd HH:mm:ss");
                uptimeSec = (long)Math.Max(0, (nowUtc - createdUtc).TotalSeconds);
            }
            catch { }
        }

        var threads = CollectProcessThreads(rec.Pid, rec.CreateTime, nowTicks, nowUtc);

        return new ProcessDetailInfo
        {
            Pid = rec.Pid,
            ParentPid = rec.ValidParentPid > 0 ? rec.ValidParentPid : rec.RawParentPid,
            Name = rec.Name,
            ParentName = parentName,
            ImagePath = rec.ImagePath,
            Priority = rec.Priority,
            Category = rec.Category,
            StartTimeText = startText,
            UptimeSeconds = uptimeSec,
            CpuPercent = rec.CpuPercent,
            WorkingSetMb = rec.WorkingSetMb,
            PrivateMb = rec.PrivateMb,
            ThreadCount = rec.ThreadCount,
            HandleCount = rec.HandleCount,
            IoReadBytesPerSec = rec.IoReadBytesPerSec,
            IoWriteBytesPerSec = rec.IoWriteBytesPerSec,
            TotalIoReadMb = Math.Round(rec.TotalIoReadBytes / (1024.0 * 1024.0), 2),
            TotalIoWriteMb = Math.Round(rec.TotalIoWriteBytes / (1024.0 * 1024.0), 2),
            AccessDenied = rec.AccessDenied,
            CanTerminate = !IsProtectedProcess(rec.Pid, rec.Name) && !isOwnDescendant,
            Threads = threads
        };
    }

    private List<ThreadTreeItem> CollectProcessThreads(int targetPid, ulong createTime, long nowTicks, DateTime nowUtc)
    {
        if (_lastThreadInspectPid != targetPid)
        {
            _prevThreads.Clear();
            _lastThreadInspectPid = targetPid;
        }

        var modules = GetCachedModules(targetPid, createTime, nowUtc);
        var items = new List<ThreadTreeItem>();
        var seenTids = new HashSet<int>();

        IntPtr hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (hSnap == IntPtr.Zero || hSnap == INVALID_HANDLE_VALUE)
            return items;

        try
        {
            var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (Thread32First(hSnap, ref te))
            {
                do
                {
                    if (te.th32OwnerProcessID != (uint)targetPid) continue;

                    int tid = (int)te.th32ThreadID;
                    seenTids.Add(tid);

                    double cpuPct = 0;
                    double kernelPct = 0;
                    double userPct = 0;
                    double totalCpuSec = 0;
                    ulong deltaTotal = 0;
                    int basePri = Math.Clamp(te.tpBasePri, 0, 31);
                    int dynPri = Math.Clamp(te.tpBasePri + te.tpDeltaPri, 0, 31);
                    string priLabel = MapBasePriority(basePri);
                    string startAddress = "—";
                    string moduleName = "";

                    IntPtr hThread = OpenThread(THREAD_QUERY_INFORMATION, false, (uint)tid);
                    if (hThread == IntPtr.Zero)
                    {
                        hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, (uint)tid);
                    }

                    if (hThread != IntPtr.Zero)
                    {
                        try
                        {
                            if (GetThreadTimes(hThread, out var cFt, out _, out var kFt, out var uFt))
                            {
                                ulong tCreate = cFt.ToUInt64();
                                ulong k100 = kFt.ToUInt64();
                                ulong u100 = uFt.ToUInt64();
                                totalCpuSec = Math.Round((k100 + u100) / 10_000_000.0, 2);

                                if (_prevThreads.TryGetValue(tid, out var prevT) && prevT.CreateTime == tCreate)
                                {
                                    double dtSec = (nowTicks - prevT.StampTicks) / (double)Stopwatch.Frequency;
                                    if (dtSec >= 0.05)
                                    {
                                        ulong dk = k100 >= prevT.Kernel100ns ? (k100 - prevT.Kernel100ns) : 0;
                                        ulong du = u100 >= prevT.User100ns ? (u100 - prevT.User100ns) : 0;
                                        deltaTotal = dk + du;
                                        double denom = dtSec * 10_000_000.0 * _processorCount;
                                        kernelPct = Math.Round(Math.Clamp(dk / denom * 100.0, 0.0, 100.0), 2);
                                        userPct = Math.Round(Math.Clamp(du / denom * 100.0, 0.0, 100.0), 2);
                                        cpuPct = Math.Round(Math.Clamp((dk + du) / denom * 100.0, 0.0, 100.0), 2);
                                    }
                                }

                                _prevThreads[tid] = new ThreadDeltaSample
                                {
                                    CreateTime = tCreate,
                                    Kernel100ns = k100,
                                    User100ns = u100,
                                    StampTicks = nowTicks
                                };
                            }

                            int relPri = GetThreadPriority(hThread);
                            if (relPri != THREAD_PRIORITY_ERROR_RETURN)
                            {
                                priLabel = MapThreadRelativePriority(relPri);
                            }

                            int status = NtQueryInformationThread(
                                hThread,
                                ThreadQuerySetWin32StartAddress,
                                out IntPtr rawStartAddr,
                                IntPtr.Size,
                                out _);
                            if (status == 0 && rawStartAddr != IntPtr.Zero)
                            {
                                ulong addr = unchecked((ulong)rawStartAddr.ToInt64());
                                ResolveModuleSymbol(addr, modules, out startAddress, out moduleName);
                            }
                        }
                        finally
                        {
                            CloseHandle(hThread);
                        }
                    }

                    string state = cpuPct >= 0.5
                        ? "Running"
                        : (cpuPct >= 0.05 || deltaTotal > 0 ? "Active" : "Wait");

                    items.Add(new ThreadTreeItem
                    {
                        Tid = tid,
                        CpuPercent = cpuPct,
                        KernelCpuPercent = kernelPct,
                        UserCpuPercent = userPct,
                        BasePriority = basePri,
                        DynamicPriority = dynPri,
                        PriorityLabel = priLabel,
                        StartAddress = startAddress,
                        ModuleName = moduleName,
                        TotalCpuSeconds = totalCpuSec,
                        State = state
                    });
                }
                while (Thread32Next(hSnap, ref te));
            }
        }
        finally
        {
            CloseHandle(hSnap);
        }

        // Remove exited thread delta entries
        var staleTids = _prevThreads.Keys.Where(t => !seenTids.Contains(t)).ToList();
        foreach (int st in staleTids)
        {
            _prevThreads.Remove(st);
        }

        items.Sort((a, b) =>
        {
            int c = b.CpuPercent.CompareTo(a.CpuPercent);
            if (c != 0) return c;
            int t = b.TotalCpuSeconds.CompareTo(a.TotalCpuSeconds);
            if (t != 0) return t;
            return a.Tid.CompareTo(b.Tid);
        });

        if (items.Count > MaxReturnedThreads)
        {
            items.RemoveRange(MaxReturnedThreads, items.Count - MaxReturnedThreads);
        }

        return items;
    }

    private List<ModuleRange> GetCachedModules(int pid, ulong createTime, DateTime nowUtc)
    {
        if (_moduleCachePid == pid &&
            _moduleCacheCreateTime == createTime &&
            (nowUtc - _moduleCacheUtc) < ModuleCacheTtl)
        {
            return _moduleCache;
        }

        var list = new List<ModuleRange>(64);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            IntPtr hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
            if (hSnap == IntPtr.Zero || hSnap == INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_BAD_LENGTH) continue;
                break;
            }

            try
            {
                var me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
                if (Module32FirstW(hSnap, ref me))
                {
                    do
                    {
                        ulong baseAddr = unchecked((ulong)me.modBaseAddr.ToInt64());
                        ulong size = me.modBaseSize;
                        if (baseAddr > 0 && size > 0)
                        {
                            string modName = string.IsNullOrWhiteSpace(me.szModule)
                                ? Path.GetFileName(me.szExePath ?? "")
                                : me.szModule.Trim();
                            list.Add(new ModuleRange(baseAddr, baseAddr + size, modName));
                        }
                    }
                    while (Module32NextW(hSnap, ref me));
                }
                break;
            }
            finally
            {
                CloseHandle(hSnap);
            }
        }

        list.Sort((a, b) => a.BaseAddr.CompareTo(b.BaseAddr));
        _moduleCachePid = pid;
        _moduleCacheCreateTime = createTime;
        _moduleCacheUtc = nowUtc;
        _moduleCache = list;
        return list;
    }

    private static void ResolveModuleSymbol(
        ulong address,
        List<ModuleRange> modules,
        out string formatted,
        out string moduleName)
    {
        int lo = 0;
        int hi = modules.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var m = modules[mid];
            if (address < m.BaseAddr)
            {
                hi = mid - 1;
            }
            else if (address >= m.EndAddr)
            {
                lo = mid + 1;
            }
            else
            {
                moduleName = m.Name;
                formatted = $"{m.Name}+0x{(address - m.BaseAddr):X}";
                return;
            }
        }

        moduleName = "";
        formatted = $"0x{address:X}";
    }

    private static bool? QueryIsElevated(IntPtr hProcess)
    {
        if (!OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hToken) || hToken == IntPtr.Zero)
            return null;

        try
        {
            if (GetTokenInformation(hToken, TokenElevation, out var elevation, (uint)Marshal.SizeOf<TOKEN_ELEVATION>(), out _))
            {
                return elevation.TokenIsElevated != 0;
            }
            return null;
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

    private static string QueryProcessImagePath(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProc == IntPtr.Zero) return "";
        try
        {
            var buf = new char[1024];
            uint len = (uint)buf.Length;
            if (QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0)
            {
                return new string(buf, 0, (int)len);
            }
            return "";
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    private static ProcessTreeNode ToNode(
        LiveProcessRecord rec,
        int depth,
        bool hasChildren,
        bool isLastSibling,
        string treeGuides) => new()
        {
            Pid = rec.Pid,
            ParentPid = rec.ValidParentPid > 0 ? rec.ValidParentPid : rec.RawParentPid,
            Depth = depth,
            HasChildren = hasChildren,
            IsLastSibling = isLastSibling,
            TreeGuides = treeGuides,
            Name = rec.Name,
            ImagePath = rec.ImagePath,
            CpuPercent = rec.CpuPercent,
            WorkingSetMb = rec.WorkingSetMb,
            PrivateMb = rec.PrivateMb,
            IoReadBytesPerSec = rec.IoReadBytesPerSec,
            IoWriteBytesPerSec = rec.IoWriteBytesPerSec,
            ThreadCount = rec.ThreadCount,
            HandleCount = rec.HandleCount,
            Priority = rec.Priority,
            Category = rec.Category,
            Lifecycle = rec.Lifecycle
        };

    private static string MapPriorityClass(uint priClass) => priClass switch
    {
        REALTIME_PRIORITY_CLASS => "实时",
        HIGH_PRIORITY_CLASS => "高",
        ABOVE_NORMAL_PRIORITY_CLASS => "高于正常",
        NORMAL_PRIORITY_CLASS => "正常",
        BELOW_NORMAL_PRIORITY_CLASS => "低于正常",
        IDLE_PRIORITY_CLASS => "低",
        _ => "正常"
    };

    private static string MapBasePriority(int basePri)
    {
        if (basePri >= 24) return "实时";
        if (basePri >= 13) return "高";
        if (basePri >= 10) return "高于正常";
        if (basePri >= 8) return "正常";
        if (basePri >= 6) return "低于正常";
        return "低";
    }

    private static string MapThreadRelativePriority(int relPri)
    {
        if (relPri <= -15) return "空闲";
        if (relPri == -2) return "最低";
        if (relPri == -1) return "低于正常";
        if (relPri == 0) return "正常";
        if (relPri == 1) return "高于正常";
        if (relPri == 2) return "最高";
        if (relPri >= 15) return "关键";
        return relPri > 0 ? $"+{relPri}" : relPri.ToString();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _prevProcesses.Clear();
            _exitedGhosts.Clear();
            _prevThreads.Clear();
            _moduleCache.Clear();
        }
    }
}
