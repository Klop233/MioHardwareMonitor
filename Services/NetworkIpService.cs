using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Detects public IPv4 addresses and IP purity / fraud risk scores for domestic (Chinese mainland)
/// and overseas endpoints.
/// Strictly enforces IPv4 (AddressFamily.InterNetwork) and ignores IPv6 completely.
/// Uses both a system-proxy client (matches real browser / split-tunnel routing) and a direct
/// bypass-proxy client (for matching domestic IP purity directly against IPPure when available),
/// backed by a 30-minute per-IP thread-safe risk cache.
/// </summary>
public sealed class NetworkIpService : IDisposable
{
    private static readonly Regex IPv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly TimeSpan RiskCacheTtl = TimeSpan.FromMinutes(30);

    private static readonly string[] ResidentialBroadbandKeywords =
    {
        "telecom", "unicom", "mobile", "chinanet", "cmnet", "cncgroup", "china169", "tietong", "cbn",
        "broadband", "cable", "fiber", "fibre", "ftth", "fttb", "dsl", "residential",
        "comcast", "xfinity", "verizon", "charter", "spectrum", "at&t", "cox communications",
        "ntt", "kddi", "softbank", "chunghwa", "hinet", "taiwan fixed", "far eastone",
        "bt ", "british telecom", "virgin media", "sky broadband", "deutsche telekom", "vodafone",
        "orange", "telefonica", "bell canada", "rogers", "telus", "telstra", "optus",
        "kt corp", "sk broadband", "lg uplus", "singtel", "starhub", "pccw", "hkbn", "hkt",
        "dr.peng", "great wall"
    };

    private sealed record IpRiskInfo(
        int FraudScore,
        int PurityScore,
        string RiskLevel,
        string RiskLabel,
        string IpType,
        bool? IsResidential,
        bool? IsBroadcast,
        string Asn,
        string Isp,
        string RiskSource,
        string FallbackLocation);

    private readonly HttpClient _http;
    private readonly HttpClient _directHttp;
    private readonly ConcurrentDictionary<string, (DateTime Utc, IpRiskInfo Info)> _riskCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public NetworkIpService()
    {
        var proxyHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = true
        };
        _http = new HttpClient(proxyHandler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SideScreenHUD/1.6");

        var directHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = false
        };
        _directHttp = new HttpClient(directHandler)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        _directHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SideScreenHUD/1.6");
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
        IpEndpointInfo? detected = null;

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

                    detected = new IpEndpointInfo
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
        if (detected == null)
        {
            try
            {
                var raw = await _http.GetStringAsync("https://vv.video.qq.com/checktime?otype=ojson", ct);
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("ip", out var ipProp) && TryValidateIPv4(ipProp.GetString(), out var validIp))
                {
                    detected = new IpEndpointInfo
                    {
                        Ip = validIp,
                        Location = "中国大陆",
                        Source = "qq.com",
                        Ok = true
                    };
                }
            }
            catch { }
        }

        // 3. Oray domestic IPv4 checkip fallback
        if (detected == null)
        {
            try
            {
                var raw = await _http.GetStringAsync("https://ddns.oray.com/checkip", ct);
                if (TryExtractFirstIPv4(raw, out var validIp))
                {
                    detected = new IpEndpointInfo
                    {
                        Ip = validIp,
                        Location = "中国大陆",
                        Source = "oray.com",
                        Ok = true
                    };
                }
            }
            catch { }
        }

        if (detected == null)
        {
            return new IpEndpointInfo { Ok = false };
        }

        await EnrichIpRiskAsync(detected, isDomestic: true, ct);
        return detected;
    }

    private async Task<IpEndpointInfo> DetectOverseasIPv4Async(CancellationToken ct)
    {
        // 1. Primary: my.ippure.com/v1/info (single call gives IPv4 + geo + native fraudScore/purity/type/ASN)
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            var json = await _http.GetStringAsync("https://my.ippure.com/v1/info", timeoutCts.Token);
            if (TryParseIpPureResponse(json, out var validIp, out var riskInfo))
            {
                CacheRiskInfo(validIp, riskInfo);
                var ep = new IpEndpointInfo
                {
                    Ip = validIp,
                    Location = riskInfo.FallbackLocation,
                    Source = "ippure.com",
                    Ok = true
                };
                ApplyRiskInfo(ep, riskInfo, preserveLocation: true);
                return ep;
            }
        }
        catch { }

        // 2. Fallback: ipinfo.io directly (single call gives IP + country/city). Strictly reject IPv6!
        IpEndpointInfo? detected = null;
        try
        {
            var json = await _http.GetStringAsync("https://ipinfo.io/json", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? ip = root.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : null;
            if (TryValidateIPv4(ip, out var validIp))
            {
                detected = new IpEndpointInfo
                {
                    Ip = validIp,
                    Location = BuildOverseasLocation(root),
                    Source = "ipinfo.io",
                    Ok = true
                };
            }
        }
        catch { }

        // 3. Guaranteed IPv4-only hostname api4.ipify.org (A record only, ignores IPv6 completely)
        if (detected == null)
        {
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

            // 4. Guaranteed IPv4-only fallback ipv4.icanhazip.com
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

            // 5. Fallback ifconfig.me/ip (IPv4 validated)
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

            detected = new IpEndpointInfo
            {
                Ip = ipv4,
                Location = loc,
                Source = src,
                Ok = true
            };
        }

        await EnrichIpRiskAsync(detected, isDomestic: false, ct);
        return detected;
    }

    private async Task EnrichIpRiskAsync(IpEndpointInfo endpoint, bool isDomestic, CancellationToken ct)
    {
        if (!endpoint.Ok || string.IsNullOrWhiteSpace(endpoint.Ip)) return;

        // 1. Check 30-minute per-IP thread-safe cache first
        if (TryGetCachedRiskInfo(endpoint.Ip, out var cached))
        {
            ApplyRiskInfo(endpoint, cached, preserveLocation: isDomestic || !string.IsNullOrWhiteSpace(endpoint.Location));
            return;
        }

        // 2. For domestic IP, try direct (non-proxied) IPPure first; accept ONLY on exact IPv4 match
        if (isDomestic)
        {
            try
            {
                using var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                directCts.CancelAfter(TimeSpan.FromMilliseconds(3500));
                var json = await _directHttp.GetStringAsync("https://my.ippure.com/v1/info", directCts.Token);
                if (TryParseIpPureResponse(json, out var ippureIp, out var riskInfo))
                {
                    // Cache whatever IP IPPure returned (useful even if it matches overseas later)
                    CacheRiskInfo(ippureIp, riskInfo);
                    if (string.Equals(ippureIp, endpoint.Ip, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyRiskInfo(endpoint, riskInfo, preserveLocation: true);
                        return;
                    }
                }
            }
            catch { }
        }

        // 3. Query ip-api.com by explicit IPv4 (works for both CN domestic IPs and global IPs)
        string url = $"http://ip-api.com/json/{endpoint.Ip}?fields=status,countryCode,regionName,city,isp,org,as,mobile,proxy,hosting";
        string? ipApiJson = null;

        try
        {
            using var apiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            apiCts.CancelAfter(TimeSpan.FromSeconds(4));
            ipApiJson = await _http.GetStringAsync(url, apiCts.Token);
        }
        catch
        {
            try
            {
                using var directApiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                directApiCts.CancelAfter(TimeSpan.FromMilliseconds(3500));
                ipApiJson = await _directHttp.GetStringAsync(url, directApiCts.Token);
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(ipApiJson) && TryParseIpApiRisk(ipApiJson, out var heuristicRisk))
        {
            CacheRiskInfo(endpoint.Ip, heuristicRisk);
            ApplyRiskInfo(endpoint, heuristicRisk, preserveLocation: isDomestic || !string.IsNullOrWhiteSpace(endpoint.Location));
        }
    }

    private static bool TryParseIpPureResponse(string json, out string validIp, out IpRiskInfo riskInfo)
    {
        validIp = "";
        riskInfo = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            string? rawIp = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() : null;
            if (!TryValidateIPv4(rawIp, out validIp)) return false;

            int fraudScore = 0;
            bool hasFraud = false;
            if (root.TryGetProperty("fraudScore", out var fsEl))
            {
                if (fsEl.ValueKind == JsonValueKind.Number && fsEl.TryGetInt32(out int fsInt))
                {
                    fraudScore = Math.Clamp(fsInt, 0, 100);
                    hasFraud = true;
                }
                else if (fsEl.ValueKind == JsonValueKind.Number && fsEl.TryGetDouble(out double fsDbl))
                {
                    fraudScore = Math.Clamp((int)Math.Round(fsDbl), 0, 100);
                    hasFraud = true;
                }
            }
            if (!hasFraud) return false;

            bool? isResidential = ReadOptionalBool(root, "isResidential");
            bool? isBroadcast = ReadOptionalBool(root, "isBroadcast");

            string asn = "";
            if (root.TryGetProperty("asn", out var asnEl))
            {
                if (asnEl.ValueKind == JsonValueKind.Number && asnEl.TryGetInt64(out long asnNum) && asnNum > 0)
                {
                    asn = "AS" + asnNum;
                }
                else if (asnEl.ValueKind == JsonValueKind.String)
                {
                    asn = NormalizeAsnToken(asnEl.GetString());
                }
            }

            string isp = ReadOptionalString(root, "asOrganization");
            string countryCode = ReadOptionalString(root, "countryCode");
            string country = ReadOptionalString(root, "country");
            string city = ReadOptionalString(root, "city");
            string fallbackLocation = JoinLocation(string.IsNullOrWhiteSpace(countryCode) ? country : countryCode, city);

            string ipType = (isResidential ?? false)
                ? ((isBroadcast ?? false) ? "广播家宽" : "家宽")
                : ((isBroadcast ?? false) ? "广播机房" : "机房");

            int purityScore = 100 - fraudScore;
            var (riskLevel, riskLabel) = ClassifyRisk(fraudScore);

            riskInfo = new IpRiskInfo(
                FraudScore: fraudScore,
                PurityScore: purityScore,
                RiskLevel: riskLevel,
                RiskLabel: riskLabel,
                IpType: ipType,
                IsResidential: isResidential,
                IsBroadcast: isBroadcast,
                Asn: asn,
                Isp: isp,
                RiskSource: "ippure.com",
                FallbackLocation: fallbackLocation);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseIpApiRisk(string json, out IpRiskInfo riskInfo)
    {
        riskInfo = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            string status = ReadOptionalString(root, "status");
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return false;

            string countryCode = ReadOptionalString(root, "countryCode");
            string city = ReadOptionalString(root, "city");
            string isp = ReadOptionalString(root, "isp");
            string org = ReadOptionalString(root, "org");
            string asRaw = ReadOptionalString(root, "as");
            bool isMobile = ReadOptionalBool(root, "mobile") ?? false;
            bool isProxy = ReadOptionalBool(root, "proxy") ?? false;
            bool isHosting = ReadOptionalBool(root, "hosting") ?? false;

            string asn = ExtractAsnPrefix(asRaw);
            string displayIsp = !string.IsNullOrWhiteSpace(isp)
                ? isp
                : (!string.IsNullOrWhiteSpace(org) ? org : StripAsnPrefix(asRaw));

            int fraudScore;
            string ipType;
            bool isResidential;

            if (isProxy && isHosting)
            {
                fraudScore = 78;
                ipType = "代理/机房";
                isResidential = false;
            }
            else if (isProxy)
            {
                fraudScore = 72;
                ipType = "代理/VPN";
                isResidential = false;
            }
            else if (isHosting)
            {
                fraudScore = 54;
                ipType = "机房";
                isResidential = false;
            }
            else if (isMobile)
            {
                fraudScore = 14;
                ipType = "蜂窝";
                isResidential = true;
            }
            else
            {
                string combined = $"{isp} {org} {asRaw}".ToLowerInvariant();
                bool looksResidential = false;
                foreach (var kw in ResidentialBroadbandKeywords)
                {
                    if (combined.Contains(kw, StringComparison.Ordinal))
                    {
                        looksResidential = true;
                        break;
                    }
                }

                if (looksResidential)
                {
                    fraudScore = 8;
                    ipType = "家宽";
                    isResidential = true;
                }
                else
                {
                    fraudScore = 18;
                    ipType = "商宽";
                    isResidential = false;
                }
            }

            int purityScore = 100 - fraudScore;
            var (riskLevel, riskLabel) = ClassifyRisk(fraudScore);

            riskInfo = new IpRiskInfo(
                FraudScore: fraudScore,
                PurityScore: purityScore,
                RiskLevel: riskLevel,
                RiskLabel: riskLabel,
                IpType: ipType,
                IsResidential: isResidential,
                IsBroadcast: null,
                Asn: asn,
                Isp: displayIsp,
                RiskSource: "ip-api.com",
                FallbackLocation: JoinLocation(countryCode, city));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string RiskLevel, string RiskLabel) ClassifyRisk(int fraudScore)
    {
        int clamped = Math.Clamp(fraudScore, 0, 100);
        if (clamped <= 15) return ("pure", "极纯净");
        if (clamped <= 35) return ("low", "低风险");
        if (clamped <= 65) return ("medium", "中等风险");
        return ("high", "高风险");
    }

    private static void ApplyRiskInfo(IpEndpointInfo endpoint, IpRiskInfo info, bool preserveLocation)
    {
        endpoint.FraudScore = info.FraudScore;
        endpoint.PurityScore = info.PurityScore;
        endpoint.RiskLevel = info.RiskLevel;
        endpoint.RiskLabel = info.RiskLabel;
        endpoint.IpType = info.IpType;
        endpoint.IsResidential = info.IsResidential;
        endpoint.IsBroadcast = info.IsBroadcast;
        endpoint.Asn = info.Asn;
        endpoint.Isp = info.Isp;
        endpoint.RiskSource = info.RiskSource;

        if (!preserveLocation && string.IsNullOrWhiteSpace(endpoint.Location) && !string.IsNullOrWhiteSpace(info.FallbackLocation))
        {
            endpoint.Location = info.FallbackLocation;
        }
    }

    private bool TryGetCachedRiskInfo(string ip, out IpRiskInfo info)
    {
        info = null!;
        if (_riskCache.TryGetValue(ip, out var entry))
        {
            if (DateTime.UtcNow - entry.Utc < RiskCacheTtl)
            {
                info = entry.Info;
                return true;
            }
            _riskCache.TryRemove(ip, out _);
        }
        return false;
    }

    private void CacheRiskInfo(string ip, IpRiskInfo info)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var now = DateTime.UtcNow;
        if (_riskCache.Count > 128)
        {
            foreach (var kv in _riskCache)
            {
                if (now - kv.Value.Utc >= RiskCacheTtl)
                    _riskCache.TryRemove(kv.Key, out _);
            }
        }
        _riskCache[ip] = (now, info);
    }

    private static string ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? (prop.GetString() ?? "").Trim()
            : "";

    private static bool? ReadOptionalBool(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return prop.GetBoolean();
        return null;
    }

    private static string NormalizeAsnToken(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return "";
        if (s.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            return "AS" + s[2..].Trim();
        if (long.TryParse(s, out long n) && n > 0)
            return "AS" + n;
        return s;
    }

    private static string ExtractAsnPrefix(string? asRaw)
    {
        if (string.IsNullOrWhiteSpace(asRaw)) return "";
        string trimmed = asRaw.Trim();
        int sp = trimmed.IndexOf(' ');
        string first = sp > 0 ? trimmed[..sp] : trimmed;
        if (first.StartsWith("AS", StringComparison.OrdinalIgnoreCase) && first.Length > 2 && char.IsAsciiDigit(first[2]))
        {
            return first.ToUpperInvariant();
        }
        return "";
    }

    private static string StripAsnPrefix(string? asRaw)
    {
        if (string.IsNullOrWhiteSpace(asRaw)) return "";
        string trimmed = asRaw.Trim();
        int sp = trimmed.IndexOf(' ');
        if (sp > 0 && trimmed.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            return trimmed[(sp + 1)..].Trim();
        return trimmed;
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
        try { _http.Dispose(); } catch { }
        try { _directHttp.Dispose(); } catch { }
        _riskCache.Clear();
    }
}
