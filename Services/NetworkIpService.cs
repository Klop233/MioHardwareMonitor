using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Detects public IPv4 addresses for domestic (Chinese mainland) and overseas endpoints.
/// Strictly enforces IPv4 (AddressFamily.InterNetwork) and ignores IPv6 completely.
/// Uses the system proxy settings so split-tunneling / proxy rules match real browser routing.
/// </summary>
public sealed class NetworkIpService : IDisposable
{
    private static readonly Regex IPv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private readonly HttpClient _http;
    private bool _disposed;

    public NetworkIpService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SideScreenHUD/1.0");
    }

    public async Task<NetworkIpSnapshot> DetectAsync(CancellationToken ct = default)
    {
        var domesticTask = DetectDomesticIPv4Async(ct);
        var overseasTask = DetectOverseasIPv4Async(ct);

        await Task.WhenAll(domesticTask, overseasTask);

        return new NetworkIpSnapshot
        {
            Domestic = await domesticTask,
            Overseas = await overseasTask,
            UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsChecking = false
        };
    }

    private async Task<IpEndpointInfo> DetectDomesticIPv4Async(CancellationToken ct)
    {
        // 1. myip.ipip.net (Chinese domestic endpoint with province/city/ISP in UTF-8 JSON)
        try
        {
            var json = await _http.GetStringAsync("https://myip.ipip.net/json", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                string? ip = data.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : null;
                if (TryValidateIPv4(ip, out var validIp))
                {
                    string loc = "";
                    if (data.TryGetProperty("location", out var locArr) && locArr.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<string>();
                        foreach (var item in locArr.EnumerateArray())
                        {
                            var s = item.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(s) && s != "中国")
                                parts.Add(s);
                        }
                        if (parts.Count > 0)
                        {
                            // Format e.g. "广东 广州 · 移动"
                            if (parts.Count >= 3)
                                loc = $"{parts[0]} {parts[1]} · {parts[^1]}";
                            else
                                loc = string.Join(" · ", parts.Distinct());
                        }
                    }

                    return new IpEndpointInfo
                    {
                        Ip = validIp,
                        Location = loc,
                        Source = "ipip.net",
                        Ok = true
                    };
                }
            }
        }
        catch { }

        // 2. Tencent Video domestic CDN checktime (fast & reliable domestic IPv4)
        try
        {
            var raw = await _http.GetStringAsync("https://vv.video.qq.com/checktime?otype=ojson", ct);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("ip", out var ipProp) && TryValidateIPv4(ipProp.GetString(), out var validIp))
            {
                return new IpEndpointInfo
                {
                    Ip = validIp,
                    Location = "中国大陆",
                    Source = "qq.com",
                    Ok = true
                };
            }
        }
        catch { }

        // 3. Oray domestic IPv4 checkip fallback
        try
        {
            var raw = await _http.GetStringAsync("https://ddns.oray.com/checkip", ct);
            if (TryExtractFirstIPv4(raw, out var validIp))
            {
                return new IpEndpointInfo
                {
                    Ip = validIp,
                    Location = "中国大陆",
                    Source = "oray.com",
                    Ok = true
                };
            }
        }
        catch { }

        return new IpEndpointInfo { Ok = false };
    }

    private async Task<IpEndpointInfo> DetectOverseasIPv4Async(CancellationToken ct)
    {
        // 1. Try ipinfo.io directly (single call gives IP + country/city). Strictly reject IPv6!
        try
        {
            var json = await _http.GetStringAsync("https://ipinfo.io/json", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? ip = root.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : null;
            if (TryValidateIPv4(ip, out var validIp))
            {
                return new IpEndpointInfo
                {
                    Ip = validIp,
                    Location = BuildOverseasLocation(root),
                    Source = "ipinfo.io",
                    Ok = true
                };
            }
        }
        catch { }

        // 2. Guaranteed IPv4-only hostname api4.ipify.org (A record only, ignores IPv6 completely)
        string? ipv4 = null;
        string src = "";
        try
        {
            var json = await _http.GetStringAsync("https://api4.ipify.org?format=json", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ip", out var ipProp) && TryValidateIPv4(ipProp.GetString(), out var parsed))
            {
                ipv4 = parsed;
                src = "ipify.org";
            }
        }
        catch { }

        // 3. Guaranteed IPv4-only fallback ipv4.icanhazip.com
        if (ipv4 == null)
        {
            try
            {
                var raw = await _http.GetStringAsync("https://ipv4.icanhazip.com", ct);
                if (TryExtractFirstIPv4(raw, out var parsed))
                {
                    ipv4 = parsed;
                    src = "icanhazip.com";
                }
            }
            catch { }
        }

        // 4. Fallback ifconfig.me/ip (IPv4 validated)
        if (ipv4 == null)
        {
            try
            {
                var raw = await _http.GetStringAsync("https://ifconfig.me/ip", ct);
                if (TryExtractFirstIPv4(raw, out var parsed))
                {
                    ipv4 = parsed;
                    src = "ifconfig.me";
                }
            }
            catch { }
        }

        if (ipv4 == null)
        {
            return new IpEndpointInfo { Ok = false };
        }

        // Best-effort lightweight geo lookup for the detected IPv4
        string loc = "";
        try
        {
            var geoJson = await _http.GetStringAsync($"https://ipinfo.io/{ipv4}/json", ct);
            using var geoDoc = JsonDocument.Parse(geoJson);
            loc = BuildOverseasLocation(geoDoc.RootElement);
        }
        catch
        {
            try
            {
                var geoJson = await _http.GetStringAsync($"https://ipwho.is/{ipv4}", ct);
                using var geoDoc = JsonDocument.Parse(geoJson);
                var root = geoDoc.RootElement;
                string country = root.TryGetProperty("country_code", out var cc) ? (cc.GetString() ?? "") : "";
                string city = root.TryGetProperty("city", out var ctProp) ? (ctProp.GetString() ?? "") : "";
                loc = JoinLocation(country, city);
            }
            catch { }
        }

        return new IpEndpointInfo
        {
            Ip = ipv4,
            Location = loc,
            Source = src,
            Ok = true
        };
    }

    private static string BuildOverseasLocation(JsonElement root)
    {
        string country = root.TryGetProperty("country", out var c) ? (c.GetString() ?? "").Trim() : "";
        string city = root.TryGetProperty("city", out var ct) ? (ct.GetString() ?? "").Trim() : "";
        return JoinLocation(country, city);
    }

    private static string JoinLocation(string country, string city)
    {
        if (!string.IsNullOrWhiteSpace(country) && !string.IsNullOrWhiteSpace(city))
            return $"{country} · {city}";
        if (!string.IsNullOrWhiteSpace(country)) return country;
        if (!string.IsNullOrWhiteSpace(city)) return city;
        return "";
    }

    private static bool TryValidateIPv4(string? raw, out string ipv4)
    {
        ipv4 = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (IPAddress.TryParse(trimmed, out var addr) && addr.AddressFamily == AddressFamily.InterNetwork)
        {
            ipv4 = addr.ToString();
            return true;
        }
        return false;
    }

    private static bool TryExtractFirstIPv4(string? text, out string ipv4)
    {
        ipv4 = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (Match m in IPv4Regex.Matches(text))
        {
            if (TryValidateIPv4(m.Value, out ipv4))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
