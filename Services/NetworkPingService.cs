using System.Diagnostics;
using System.Net.NetworkInformation;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Lightweight, disposable ICMP latency monitor that maintains a rolling 20-sample window
/// per target host to compute live RTT (ms), jitter (ms), and packet loss (%).
/// </summary>
public sealed class NetworkPingService : IDisposable
{
    private const int WindowSize = 20;

    private readonly object _sync = new();
    private readonly Queue<(bool Ok, double? RttMs)> _samples = new(WindowSize + 1);
    private string _currentTarget = "";
    private bool _disposed;

    public async Task<NetworkLatencyMetrics> ProbeAsync(string? targetHost, CancellationToken ct)
    {
        string target = SettingsService.NormalizePingTarget(targetHost);
        if (_disposed || ct.IsCancellationRequested)
        {
            return new NetworkLatencyMetrics { Target = target, Ok = false };
        }

        bool ok = false;
        double? rttMs = null;

        try
        {
            using var ping = new Ping();
            var sw = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(2000), cancellationToken: ct);
            sw.Stop();

            if (reply != null && reply.Status == IPStatus.Success)
            {
                ok = true;
                double rawMs = reply.RoundtripTime > 0
                    ? reply.RoundtripTime
                    : Math.Min(0.9, Math.Max(0.1, sw.Elapsed.TotalMilliseconds));
                rttMs = Math.Round(rawMs, 1);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return GetSnapshot(target);
        }
        catch
        {
            ok = false;
            rttMs = null;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return new NetworkLatencyMetrics { Target = target, Ok = false };
            }

            if (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                _currentTarget = target;
                _samples.Clear();
            }

            _samples.Enqueue((ok, rttMs));
            while (_samples.Count > WindowSize)
            {
                _samples.Dequeue();
            }

            return BuildSnapshotLocked(target, ok, rttMs);
        }
    }

    public NetworkLatencyMetrics GetSnapshot(string? targetHost)
    {
        string target = SettingsService.NormalizePingTarget(targetHost);
        lock (_sync)
        {
            if (!string.Equals(_currentTarget, target, StringComparison.OrdinalIgnoreCase) || _samples.Count == 0)
            {
                return new NetworkLatencyMetrics { Target = target, Ok = false };
            }

            var last = _samples.Last();
            return BuildSnapshotLocked(target, last.Ok, last.RttMs);
        }
    }

    private NetworkLatencyMetrics BuildSnapshotLocked(string target, bool latestOk, double? latestRtt)
    {
        int total = _samples.Count;
        int failed = 0;
        double jitterSum = 0;
        int jitterPairs = 0;
        double? prevOkRtt = null;

        foreach (var sample in _samples)
        {
            if (!sample.Ok || !sample.RttMs.HasValue)
            {
                failed++;
                prevOkRtt = null;
                continue;
            }

            if (prevOkRtt.HasValue)
            {
                jitterSum += Math.Abs(sample.RttMs.Value - prevOkRtt.Value);
                jitterPairs++;
            }
            prevOkRtt = sample.RttMs.Value;
        }

        double lossPct = total > 0 ? Math.Round(failed * 100.0 / total, 1) : 0;
        double? jitterMs = jitterPairs > 0 ? Math.Round(jitterSum / jitterPairs, 1) : null;

        return new NetworkLatencyMetrics
        {
            Target = target,
            PingMs = latestOk ? latestRtt : null,
            JitterMs = jitterMs,
            PacketLossPercent = lossPct,
            Ok = latestOk
        };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _samples.Clear();
        }
    }
}
