namespace SideScreenHUD.Models;

public sealed class TelemetrySnapshot
{
    public long TimestampMs { get; set; }
    public string HostName { get; set; } = Environment.MachineName;
    public long UptimeSeconds { get; set; }
    public bool IsElevated { get; set; }
    public CpuMetrics Cpu { get; set; } = new();
    public List<CpuSocketMetrics> Cpus { get; set; } = new();
    public MemoryMetrics Memory { get; set; } = new();
    public GpuMetrics Gpu { get; set; } = new();
    public List<GpuSummary> AllGpus { get; set; } = new();
    public NetworkMetrics Network { get; set; } = new();
    public DiskMetrics Disk { get; set; } = new();
    public List<ProcessItem> TopProcesses { get; set; } = new();
}

public sealed class CpuMetrics
{
    public string Name { get; set; } = "CPU";
    public int SocketCount { get; set; } = 1;
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public double TotalUsagePercent { get; set; }
    public double CurrentClockGhz { get; set; }
    public double BaseClockGhz { get; set; }
    public double? TemperatureC { get; set; }
    public double? PowerWatts { get; set; }
    public double[] CoreUsages { get; set; } = Array.Empty<double>();
}

public sealed class CpuSocketMetrics
{
    public int Index { get; set; }
    public string Label { get; set; } = "CPU 1";
    public string SocketDesignation { get; set; } = "";
    public string Name { get; set; } = "CPU";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public int StartCoreIndex { get; set; }
    public double TotalUsagePercent { get; set; }
    public double CurrentClockGhz { get; set; }
    public double BaseClockGhz { get; set; }
    public double? TemperatureC { get; set; }
    public double? PowerWatts { get; set; }
    public double[] CoreUsages { get; set; } = Array.Empty<double>();
}

public sealed class MemoryMetrics
{
    public double TotalGb { get; set; }
    public double UsedGb { get; set; }
    public double AvailableGb { get; set; }
    public double UsagePercent { get; set; }
    public int? SpeedMhz { get; set; }
}

public sealed class GpuMetrics
{
    public string Name { get; set; } = "GPU";
    public double UsagePercent { get; set; }
    public double VramUsedGb { get; set; }
    public double VramTotalGb { get; set; }
    public double VramUsagePercent { get; set; }
    public double? TemperatureC { get; set; }
    public double? HotspotTempC { get; set; }
    public double? FanRpm { get; set; }
    public double? FanPercent { get; set; }
    public double? CoreClockMhz { get; set; }
    public double? MemoryClockMhz { get; set; }
    public double? PowerWatts { get; set; }
}

public sealed class GpuSummary
{
    public string Name { get; set; } = "";
    public string LuidKey { get; set; } = "";
    public double UsagePercent { get; set; }
    public double VramUsedGb { get; set; }
    public double VramTotalGb { get; set; }
    public double? TemperatureC { get; set; }
    public bool IsDiscrete { get; set; }
}

public sealed class NetworkMetrics
{
    public string AdapterName { get; set; } = "Network";
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public double TotalDownloadedMb { get; set; }
    public double TotalUploadedMb { get; set; }
}

public sealed class IpEndpointInfo
{
    public string Ip { get; set; } = "";
    public string Location { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class NetworkIpSnapshot
{
    public IpEndpointInfo Domestic { get; set; } = new();
    public IpEndpointInfo Overseas { get; set; } = new();
    public long UpdatedMs { get; set; }
    public bool IsChecking { get; set; }
}

public sealed class DiskMetrics
{
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
    public List<DriveItem> Drives { get; set; } = new();
}

public sealed class DriveItem
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public double TotalGb { get; set; }
    public double UsedGb { get; set; }
    public double FreeGb { get; set; }
    public double UsagePercent { get; set; }
}

public sealed class ProcessItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
}
