using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using QRCoder;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Client for the MioHardwareMonitor WeChat Pay Native v3 acquiring backend (server/wechat-pay).
/// Keeps merchant private keys strictly on the backend server while rendering crisp QR codes
/// and polling order status natively inside WPF.
/// </summary>
public sealed class DonationService : IDisposable
{
    public const string DefaultApiBaseUrl = "https://donate.miolab.ink";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private bool _disposed;

    public DonationService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MioHardwareMonitor/1.4");
    }

    public static string NormalizeBaseUrl(string? raw)
    {
        string url = (raw ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url)) return DefaultApiBaseUrl;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }

    public async Task<DonateHealthResult> CheckHealthAsync(string? baseUrl, CancellationToken ct = default)
    {
        string endpoint = NormalizeBaseUrl(baseUrl) + "/api/donate/health";
        try
        {
            using var resp = await _http.GetAsync(endpoint, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var res = new DonateHealthResult
            {
                Ok = resp.IsSuccessStatusCode && root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean(),
                Configured = root.TryGetProperty("configured", out var cfgEl) && cfgEl.GetBoolean(),
                MerchantName = root.TryGetProperty("merchantName", out var mEl) ? (mEl.GetString() ?? "星澪科技") : "星澪科技"
            };

            if (root.TryGetProperty("missing", out var missEl) && missEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in missEl.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) res.Missing.Add(s);
                }
            }

            if (!res.Ok)
            {
                res.ErrorMessage = $"收单服务返回 HTTP {(int)resp.StatusCode}";
            }
            return res;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DonateHealthResult
            {
                Ok = false,
                ErrorMessage = FriendlyNetworkError(NormalizeBaseUrl(baseUrl), ex)
            };
        }
    }

    public async Task<DonateOrderResult> CreateWeChatNativeOrderAsync(
        string? baseUrl,
        int amountFen,
        string? note,
        string clientVersion,
        CancellationToken ct = default)
    {
        string normBase = NormalizeBaseUrl(baseUrl);
        string endpoint = normBase + "/api/donate/wechat/native";
        try
        {
            var payload = new
            {
                amountFen,
                note = (note ?? "").Trim(),
                clientVersion
            };
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(endpoint, content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool ok = resp.IsSuccessStatusCode && root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                string msg = root.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "";
                return new DonateOrderResult
                {
                    Ok = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(msg) ? $"下单失败（HTTP {(int)resp.StatusCode}）" : msg
                };
            }

            DateTimeOffset? expireAt = null;
            if (root.TryGetProperty("expireAt", out var expEl) && DateTimeOffset.TryParse(expEl.GetString(), out var parsedExp))
                expireAt = parsedExp;

            return new DonateOrderResult
            {
                Ok = true,
                OrderId = root.TryGetProperty("orderId", out var oidEl) ? (oidEl.GetString() ?? "") : "",
                AmountFen = root.TryGetProperty("amountFen", out var amtEl) ? amtEl.GetInt32() : amountFen,
                CodeUrl = root.TryGetProperty("codeUrl", out var cuEl) ? (cuEl.GetString() ?? "") : "",
                ExpireAt = expireAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
                MerchantName = root.TryGetProperty("merchantName", out var mEl) ? (mEl.GetString() ?? "星澪科技") : "星澪科技"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DonateOrderResult
            {
                Ok = false,
                ErrorMessage = FriendlyNetworkError(normBase, ex)
            };
        }
    }

    public async Task<DonateStatusResult> QueryWeChatOrderStatusAsync(
        string? baseUrl,
        string orderId,
        CancellationToken ct = default)
    {
        string normBase = NormalizeBaseUrl(baseUrl);
        string endpoint = $"{normBase}/api/donate/wechat/status?orderId={Uri.EscapeDataString(orderId)}";
        try
        {
            using var resp = await _http.GetAsync(endpoint, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool ok = resp.IsSuccessStatusCode && root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                string msg = root.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "";
                return new DonateStatusResult
                {
                    Ok = false,
                    OrderId = orderId,
                    ErrorMessage = string.IsNullOrWhiteSpace(msg) ? $"查单失败（HTTP {(int)resp.StatusCode}）" : msg
                };
            }

            return new DonateStatusResult
            {
                Ok = true,
                OrderId = orderId,
                TradeState = root.TryGetProperty("tradeState", out var tsEl) ? (tsEl.GetString() ?? "NOTPAY") : "NOTPAY",
                TradeStateDesc = root.TryGetProperty("tradeStateDesc", out var descEl) ? (descEl.GetString() ?? "") : "",
                TransactionId = root.TryGetProperty("transactionId", out var tidEl) ? (tidEl.GetString() ?? "") : "",
                SuccessTime = root.TryGetProperty("successTime", out var stEl) ? (stEl.GetString() ?? "") : "",
                AmountFen = root.TryGetProperty("amountFen", out var amtEl) ? amtEl.GetInt32() : 0
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DonateStatusResult
            {
                Ok = false,
                OrderId = orderId,
                ErrorMessage = FriendlyNetworkError(normBase, ex)
            };
        }
    }

    public async Task CloseWeChatOrderAsync(string? baseUrl, string orderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return;
        try
        {
            string endpoint = NormalizeBaseUrl(baseUrl) + "/api/donate/wechat/close";
            string json = JsonSerializer.Serialize(new { orderId }, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(endpoint, content, ct);
        }
        catch { }
    }

    public static BitmapImage GenerateQrBitmapImage(string codeUrl, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(codeUrl, QRCodeGenerator.ECCLevel.M);
        using var pngQr = new PngByteQRCode(qrData);
        byte[] pngBytes = pngQr.GetGraphic(pixelsPerModule, new byte[] { 0x1F, 0x29, 0x37 }, new byte[] { 0xFF, 0xFF, 0xFF }, drawQuietZones: false);

        using var ms = new MemoryStream(pngBytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static string FriendlyNetworkError(string baseUrl, Exception ex)
    {
        if (ex is TaskCanceledException)
            return $"连接收单服务超时（{baseUrl}），请稍后重试。";
        if (ex is JsonException)
            return $"收单服务（{baseUrl}）返回的不是有效 JSON，请确认已正确部署 server/wechat-pay。";
        return $"无法连接到收单服务（{baseUrl}）：{ex.Message}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { }
    }
}
