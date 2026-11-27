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
    public SystemSensorsMetrics Sensors { get; set; } = new();
    public SessionStatsSnapshot SessionStats { get; set; } = new();
    public List<TelemetryAlertItem> Alerts { get; set; } = new();
    public SelfFootprintMetrics SelfFootprint { get; set; } = new();
    public bool IsRecording { get; set; }
    public int RecordingDurationSeconds { get; set; }
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
    public double CommittedGb { get; set; }
    public double CommitLimitGb { get; set; }
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
    public double? MemoryTempC { get; set; }
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
    public NetworkLatencyMetrics Latency { get; set; } = new();
}

public sealed class NetworkLatencyMetrics
{
    public string Target { get; set; } = "";
    public double? PingMs { get; set; }
    public double? JitterMs { get; set; }
    public double PacketLossPercent { get; set; }
    public bool Ok { get; set; }
}

public sealed class IpEndpointInfo
{
    public string Ip { get; set; } = "";
    public string Location { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Ok { get; set; }
    public int? FraudScore { get; set; }
    public int? PurityScore { get; set; }
    public string RiskLevel { get; set; } = "";
    public string RiskLabel { get; set; } = "";
    public string IpType { get; set; } = "";
    public bool? IsResidential { get; set; }
    public bool? IsBroadcast { get; set; }
    public string Asn { get; set; } = "";
    public string Isp { get; set; } = "";
    public string RiskSource { get; set; } = "";
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
    public double? TemperatureC { get; set; }
}

public sealed class ProcessItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
}

public sealed class SystemSensorsMetrics
{
    public string MotherboardName { get; set; } = "";
    public List<FanSensorItem> Fans { get; set; } = new();
    public List<TempSensorItem> MotherboardTemps { get; set; } = new();
    public List<StorageTempItem> StorageTemps { get; set; } = new();
    public double? EstimatedSystemPowerWatts { get; set; }
    public BatteryMetrics? Battery { get; set; }
}

public sealed class FanSensorItem
{
    public string Name { get; set; } = "";
    public double Rpm { get; set; }
    public double? Percent { get; set; }
    public string Source { get; set; } = "";
}

public sealed class TempSensorItem
{
    public string Name { get; set; } = "";
    public double TempC { get; set; }
}

public sealed class StorageTempItem
{
    public string Name { get; set; } = "";
    public double? TempC { get; set; }
    public double? LifeRemainingPercent { get; set; }
}

public sealed class BatteryMetrics
{
    public double ChargePercent { get; set; }
    public bool IsCharging { get; set; }
    public bool OnAcPower { get; set; }
    public double? PowerWatts { get; set; }
    public int? RemainingMinutes { get; set; }
    public double? WearPercent { get; set; }
}

public sealed class MetricRange
{
    public double? Min { get; set; }
    public double? Avg { get; set; }
    public double? Max { get; set; }
}

public sealed class SessionStatsSnapshot
{
    public long SessionStartedMs { get; set; }
    public int SampleCount { get; set; }
    public MetricRange CpuUsage { get; set; } = new();
    public MetricRange CpuTemp { get; set; } = new();
    public MetricRange CpuPower { get; set; } = new();
    public MetricRange CpuClock { get; set; } = new();
    public MetricRange GpuUsage { get; set; } = new();
    public MetricRange GpuTemp { get; set; } = new();
    public MetricRange GpuHotspot { get; set; } = new();
    public MetricRange GpuPower { get; set; } = new();
    public MetricRange RamUsage { get; set; } = new();
    public double NetDownPeakBytesPerSec { get; set; }
    public double NetUpPeakBytesPerSec { get; set; }
}

public sealed class TelemetryAlertItem
{
    public string Id { get; set; } = "";
    public string WidgetId { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public double Value { get; set; }
    public double Threshold { get; set; }
}

public sealed class SelfFootprintMetrics
{
    public double HostCpuPercent { get; set; }
    public double WebViewCpuPercent { get; set; }
    public double TotalCpuPercent { get; set; }
    public double TotalMemoryMb { get; set; }
    public int ProcessCount { get; set; }
    public double CollectDurationMs { get; set; }
}

public sealed class DirectDisplayDiagnostics
{
    public bool IsActive { get; set; }
    public int TargetFps { get; set; }
    public double ActualFps { get; set; }
    public double AvgUploadMs { get; set; }
    public double LastUploadMs { get; set; }
    public long PresentedFrames { get; set; }
    public long SkippedFrames { get; set; }
    public long FailedFrames { get; set; }
    public string LastStatus { get; set; } = "";
    public int PanelWidth { get; set; }
    public int PanelHeight { get; set; }
    public int ContentWidth { get; set; }
    public int ContentHeight { get; set; }
    public int Rotation { get; set; }
    public int RefreshRateHz { get; set; }
}

public sealed class TelemetryRecordingStatus
{
    public bool IsRecording { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string LogsDirectory { get; set; } = "";
    public long StartedMs { get; set; }
    public int DurationSeconds { get; set; }
    public int SampleCount { get; set; }
    public long BytesWritten { get; set; }
    public string LastError { get; set; } = "";
}
