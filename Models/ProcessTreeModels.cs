namespace SideScreenHUD.Models;

public sealed class ProcessTreeSnapshot
{
    public long TimestampMs { get; set; }
    public int TotalProcesses { get; set; }
    public int TotalThreads { get; set; }
    public int TotalHandles { get; set; }
    public string ViewMode { get; set; } = "tree"; // "tree" | "flat"
    public string SortBy { get; set; } = "cpu"; // "cpu" | "mem" | "io" | "threads" | "handles" | "pid" | "name"
    public bool AutoFollowTop { get; set; } = true;
    public int PinnedPid { get; set; }
    public bool PinnedTargetActive { get; set; }
    public int ActivePid { get; set; }
    public string Filter { get; set; } = "";
    public List<ProcessTreeNode> Processes { get; set; } = new();
    public ProcessDetailInfo? SelectedProcess { get; set; }
}

public sealed class ProcessTreeNode
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public int Depth { get; set; }
    public bool HasChildren { get; set; }
    public bool IsLastSibling { get; set; }
    public string TreeGuides { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public double PrivateMb { get; set; }
    public double IoReadBytesPerSec { get; set; }
    public double IoWriteBytesPerSec { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public string Priority { get; set; } = "";
    public string Category { get; set; } = "normal"; // "system" | "service" | "elevated" | "self" | "normal"
    public string Lifecycle { get; set; } = "normal"; // "new" | "exited" | "normal"
}

public sealed class ProcessDetailInfo
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public string ParentName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Category { get; set; } = "normal";
    public string StartTimeText { get; set; } = "";
    public long UptimeSeconds { get; set; }
    public double CpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public double PrivateMb { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public double IoReadBytesPerSec { get; set; }
    public double IoWriteBytesPerSec { get; set; }
    public double TotalIoReadMb { get; set; }
    public double TotalIoWriteMb { get; set; }
    public bool AccessDenied { get; set; }
    public bool CanTerminate { get; set; }
    public List<ThreadTreeItem> Threads { get; set; } = new();
}

public sealed class ThreadTreeItem
{
    public int Tid { get; set; }
    public double CpuPercent { get; set; }
    public double KernelCpuPercent { get; set; }
    public double UserCpuPercent { get; set; }
    public int BasePriority { get; set; }
    public int DynamicPriority { get; set; }
    public string PriorityLabel { get; set; } = "";
    public string StartAddress { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public double TotalCpuSeconds { get; set; }
    public string State { get; set; } = "Wait"; // "Running" | "Active" | "Wait"
}
