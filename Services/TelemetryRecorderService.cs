using System.Globalization;
using System.IO;
using System.Text;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Thread-safe, low-overhead buffered UTF-8 (with BOM) CSV telemetry logger.
/// Writes to %USERPROFILE%\Documents\MioHardwareMonitor\Logs\MioTelemetry_yyyyMMdd_HHmmss.csv
/// and flushes every 5 samples to keep disk I/O negligible while protecting data on crash.
/// </summary>
public sealed class TelemetryRecorderService : IDisposable
{
    private const int FlushIntervalSamples = 5;
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private bool _isRecording;
    private string _filePath = "";
    private string _fileName = "";
    private string _logsDirectory = GetDefaultLogsDirectory();
    private long _startedMs;
    private int _finalDurationSeconds;
    private int _sampleCount;
    private long _bytesWritten;
    private string _lastError = "";
    private bool _disposed;

    public bool IsRecording
    {
        get { lock (_sync) return _isRecording; }
    }

    public static string GetDefaultLogsDirectory()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(docs))
                return Path.Combine(docs, "MioHardwareMonitor", "Logs");
        }
        catch { }

        return Path.Combine(AppContext.BaseDirectory, "Logs");
    }

    public (bool Success, string? ErrorMessage) Start()
    {
        lock (_sync)
        {
            if (_disposed) return (false, "录制服务已释放。");
            if (_isRecording) return (true, null);

            try
            {
                _logsDirectory = GetDefaultLogsDirectory();
                Directory.CreateDirectory(_logsDirectory);

                _fileName = $"MioTelemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                _filePath = Path.Combine(_logsDirectory, _fileName);

                var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 16384);
                _writer = new StreamWriter(stream, Utf8Bom, 16384);
                _writer.WriteLine(
                    "Timestamp,UptimeSec,CpuUsagePct,CpuClockGhz,CpuTempC,CpuPowerW," +
                    "GpuUsagePct,GpuTempC,GpuHotspotC,GpuMemTempC,GpuClockMhz,GpuPowerW,GpuVramUsedGb,GpuVramTotalGb," +
                    "RamUsagePct,RamUsedGb,RamTotalGb,RamCommittedGb," +
                    "NetDownKbps,NetUpKbps,PingMs,JitterMs,PacketLossPct," +
                    "DiskReadMBps,DiskWriteMBps,SysPowerW,TopFanRpm,AlertCount," +
                    "HostCpuPct,WebViewCpuPct,AppMemMb");
                _writer.Flush();

                _startedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _finalDurationSeconds = 0;
                _sampleCount = 0;
                _bytesWritten = stream.Length;
                _lastError = "";
                _isRecording = true;

                return (true, null);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                CloseWriterLocked();
                _isRecording = false;
                return (false, $"无法创建日志 CSV 文件：{ex.Message}");
            }
        }
    }

    public void Record(TelemetrySnapshot snap)
    {
        if (snap == null) return;
        lock (_sync)
        {
            if (!_isRecording || _writer == null || _disposed) return;

            try
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(snap.TimestampMs).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

                var cpu = snap.Cpu ?? new CpuMetrics();
                var gpu = snap.Gpu ?? new GpuMetrics();
                var mem = snap.Memory ?? new MemoryMetrics();
                var net = snap.Network ?? new NetworkMetrics();
                var disk = snap.Disk ?? new DiskMetrics();

                double? topFan = snap.Sensors?.Fans is { Count: > 0 } fans
                    ? fans.Max(f => f.Rpm)
                    : gpu.FanRpm;

                var line = string.Join(",",
                    ts,
                    snap.UptimeSeconds.ToString(CultureInfo.InvariantCulture),
                    F(cpu.TotalUsagePercent, 1),
                    F(cpu.CurrentClockGhz, 2),
                    Fn(cpu.TemperatureC, 1),
                    Fn(cpu.PowerWatts, 1),
                    F(gpu.UsagePercent, 1),
                    Fn(gpu.TemperatureC, 1),
                    Fn(gpu.HotspotTempC, 1),
                    Fn(gpu.MemoryTempC, 1),
                    Fn(gpu.CoreClockMhz, 0),
                    Fn(gpu.PowerWatts, 1),
                    F(gpu.VramUsedGb, 2),
                    F(gpu.VramTotalGb, 1),
                    F(mem.UsagePercent, 1),
                    F(mem.UsedGb, 1),
                    F(mem.TotalGb, 1),
                    F(mem.CommittedGb, 1),
                    F(net.DownloadBytesPerSec / 1024.0, 1),
                    F(net.UploadBytesPerSec / 1024.0, 1),
                    Fn(net.Latency?.PingMs, 1),
                    Fn(net.Latency?.JitterMs, 1),
                    F(net.Latency?.PacketLossPercent ?? 0, 1),
                    F(disk.ReadBytesPerSec / (1024.0 * 1024.0), 2),
                    F(disk.WriteBytesPerSec / (1024.0 * 1024.0), 2),
                    Fn(snap.Sensors?.EstimatedSystemPowerWatts, 1),
                    Fn(topFan, 0),
                    (snap.Alerts?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                    F(snap.SelfFootprint?.HostCpuPercent ?? 0, 2),
                    F(snap.SelfFootprint?.WebViewCpuPercent ?? 0, 2),
                    F(snap.SelfFootprint?.TotalMemoryMb ?? 0, 1));

                _writer.WriteLine(line);
                _sampleCount++;

                if (_sampleCount % FlushIntervalSamples == 0)
                {
                    _writer.Flush();
                    _bytesWritten = _writer.BaseStream.Length;
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _finalDurationSeconds = ComputeActiveDurationLocked();
                CloseWriterLocked();
                _isRecording = false;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_isRecording && _writer == null) return;
            _finalDurationSeconds = ComputeActiveDurationLocked();
            CloseWriterLocked();
            _isRecording = false;
        }
    }

    public TelemetryRecordingStatus GetStatus()
    {
        lock (_sync)
        {
            int dur = _isRecording ? ComputeActiveDurationLocked() : _finalDurationSeconds;
            return new TelemetryRecordingStatus
            {
                IsRecording = _isRecording,
                FilePath = _filePath,
                FileName = _fileName,
                LogsDirectory = _logsDirectory,
                StartedMs = _startedMs,
                DurationSeconds = dur,
                SampleCount = _sampleCount,
                BytesWritten = _bytesWritten,
                LastError = _lastError
            };
        }
    }

    private int ComputeActiveDurationLocked()
    {
        if (_startedMs <= 0) return 0;
        long elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedMs;
        return (int)Math.Max(0, elapsedMs / 1000);
    }

    private void CloseWriterLocked()
    {
        if (_writer == null) return;
        try
        {
            _writer.Flush();
            _bytesWritten = _writer.BaseStream.Length;
        }
        catch { }

        try { _writer.Dispose(); } catch { }
        _writer = null;
    }

    private static string F(double value, int decimals) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? ""
            : Math.Round(value, decimals).ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);

    private static string Fn(double? value, int decimals) =>
        value.HasValue ? F(value.Value, decimals) : "";

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _finalDurationSeconds = ComputeActiveDurationLocked();
            CloseWriterLocked();
            _isRecording = false;
        }
    }
}
