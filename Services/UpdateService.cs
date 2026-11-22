using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Handles GitHub Releases update discovery, SemVer/build comparison, streaming download
/// with progress + cancellation, SHA-256 integrity verification, and seamless update application
/// for both Inno Setup installations and portable ZIP deployments.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepoOwner = "Klop233";
    public const string RepoName = "MioHardwareMonitor";
    public const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    private const string LatestApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string LatestWebUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    private readonly HttpClient _http;
    private readonly HttpClient _noRedirectHttp;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _sync = new();

    private CancellationTokenSource? _downloadCts;
    private UpdateStateSnapshot _state;
    private bool _disposed;

    public string CurrentVersion { get; }
    public string CurrentArch { get; }
    public bool IsInstalledMode { get; }
    public string UpdatesDirectory { get; }

    public event Action<UpdateStateSnapshot>? StateChanged;

    public UpdateService()
    {
        CurrentVersion = ReadCurrentAppVersion();
        CurrentArch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        IsInstalledMode = DetectInstalledMode();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        UpdatesDirectory = Path.Combine(localAppData, "SideScreenHUD", "Updates");
        Directory.CreateDirectory(UpdatesDirectory);

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MioHardwareMonitor", SanitizeUserAgentVersion(CurrentVersion)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var noRedirHandler = new HttpClientHandler { AllowAutoRedirect = false };
        _noRedirectHttp = new HttpClient(noRedirHandler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _noRedirectHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MioHardwareMonitor", SanitizeUserAgentVersion(CurrentVersion)));

        _state = new UpdateStateSnapshot
        {
            Status = UpdateStatus.Idle,
            CurrentVersion = CurrentVersion,
            CurrentArch = CurrentArch,
            IsInstalledMode = IsInstalledMode,
            StatusMessage = $"当前版本 v{CurrentVersion}"
        };
    }

    public UpdateStateSnapshot GetState()
    {
        lock (_sync)
        {
            return CloneState(_state);
        }
    }

    public void UpdateIgnoredFlag(string? ignoredVersion)
    {
        UpdateStateSnapshot snapshot;
        lock (_sync)
        {
            _state.IsIgnoredVersion = IsVersionIgnored(_state.LatestRelease?.TagName, ignoredVersion);
            snapshot = CloneState(_state);
        }
        RaiseStateChanged(snapshot);
    }

    public async Task<UpdateStateSnapshot> CheckForUpdatesAsync(string? ignoredVersion, CancellationToken ct = default)
    {
        if (!await _checkLock.WaitAsync(0, ct))
        {
            return GetState();
        }

        try
        {
            SetState(s =>
            {
                s.Status = UpdateStatus.Checking;
                s.ErrorMessage = "";
                s.StatusMessage = "正在连接 GitHub 检查最新版本...";
            });

            UpdateReleaseInfo? release = null;
            string? apiError = null;

            try
            {
                release = await FetchLatestFromGitHubApiAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                apiError = ex.Message;
            }

            // Fallback to web redirect probe when GitHub API hits anonymous rate-limit (403/429) or transient error
            if (release == null)
            {
                try
                {
                    release = await FetchLatestViaWebRedirectAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    apiError ??= ex.Message;
                }
            }

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return SetState(s =>
                {
                    s.Status = UpdateStatus.Error;
                    s.LastCheckedAt = DateTimeOffset.UtcNow;
                    s.ErrorMessage = string.IsNullOrWhiteSpace(apiError) ? "无法获取最新 Release 信息" : apiError;
                    s.StatusMessage = "检查更新失败，请检查网络或稍后重试";
                });
            }

            bool hasUpdate = CompareVersions(release.TagName, CurrentVersion) > 0;
            bool isIgnored = hasUpdate && IsVersionIgnored(release.TagName, ignoredVersion);

            // Reuse already-downloaded & intact file if user previously downloaded this exact release
            string? existingFile = null;
            if (hasUpdate && !string.IsNullOrWhiteSpace(release.AssetName))
            {
                string candidate = Path.Combine(UpdatesDirectory, SanitizePathSegment(release.TagName), release.AssetName);
                if (File.Exists(candidate) && (release.AssetSizeBytes <= 0 || new FileInfo(candidate).Length == release.AssetSizeBytes))
                {
                    existingFile = candidate;
                }
            }

            return SetState(s =>
            {
                s.LatestRelease = release;
                s.HasUpdate = hasUpdate;
                s.IsIgnoredVersion = isIgnored;
                s.LastCheckedAt = DateTimeOffset.UtcNow;
                s.ErrorMessage = "";
                s.DownloadedBytes = existingFile != null ? new FileInfo(existingFile).Length : 0;
                s.TotalBytes = release.AssetSizeBytes;
                s.ProgressPercent = existingFile != null ? 100 : 0;
                s.SpeedBytesPerSec = 0;
                s.DownloadedFilePath = existingFile ?? "";

                if (!hasUpdate)
                {
                    s.Status = UpdateStatus.UpToDate;
                    s.StatusMessage = $"当前已是最新版本 (v{CurrentVersion})";
                }
                else if (existingFile != null)
                {
                    s.Status = UpdateStatus.ReadyToInstall;
                    s.StatusMessage = $"新版本 {release.TagName} 已下载就绪，可立即安装";
                }
                else
                {
                    s.Status = UpdateStatus.UpdateAvailable;
                    s.StatusMessage = isIgnored
                        ? $"发现新版本 {release.TagName}（已设为忽略）"
                        : $"发现新版本 {release.TagName}";
                }
            });
        }
        catch (OperationCanceledException)
        {
            return GetState();
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public async Task<UpdateStateSnapshot> DownloadLatestAsync()
    {
        UpdateReleaseInfo? release;
        lock (_sync)
        {
            if (_state.Status is UpdateStatus.Downloading or UpdateStatus.Verifying)
                return CloneState(_state);
            release = _state.LatestRelease;
        }

        if (release == null || string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
        {
            return SetState(s =>
            {
                s.Status = UpdateStatus.Error;
                s.ErrorMessage = "当前版本未匹配到可下载的更新包，请前往 GitHub Releases 页面手动下载。";
                s.StatusMessage = "未找到匹配的更新包";
            });
        }

        CancelDownload();
        var cts = new CancellationTokenSource();
        lock (_sync) { _downloadCts = cts; }
        var ct = cts.Token;

        string tagDir = Path.Combine(UpdatesDirectory, SanitizePathSegment(release.TagName));
        Directory.CreateDirectory(tagDir);
        string destPath = Path.Combine(tagDir, release.AssetName);
        string partPath = destPath + ".part";

        try
        {
            SetState(s =>
            {
                s.Status = UpdateStatus.Downloading;
                s.DownloadedBytes = 0;
                s.TotalBytes = release.AssetSizeBytes;
                s.ProgressPercent = 0;
                s.SpeedBytesPerSec = 0;
                s.Sha256Verified = false;
                s.DownloadedFilePath = "";
                s.ErrorMessage = "";
                s.StatusMessage = $"正在下载 {release.AssetName}...";
            });

            using var req = new HttpRequestMessage(HttpMethod.Get, release.AssetDownloadUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? release.AssetSizeBytes;
            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536];
                long downloaded = 0;
                var sw = Stopwatch.StartNew();
                long lastReportMs = 0;
                long lastBytes = 0;

                int read;
                while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    long elapsedMs = sw.ElapsedMilliseconds;
                    if (elapsedMs - lastReportMs >= 150 || (totalBytes > 0 && downloaded >= totalBytes))
                    {
                        double dtSec = Math.Max(0.05, (elapsedMs - lastReportMs) / 1000.0);
                        double speed = (downloaded - lastBytes) / dtSec;
                        lastReportMs = elapsedMs;
                        lastBytes = downloaded;

                        double pct = totalBytes > 0 ? Math.Clamp(downloaded * 100.0 / totalBytes, 0, 100) : 0;
                        long snapDl = downloaded;
                        long snapTot = totalBytes;
                        double snapPct = pct;
                        double snapSpd = speed;

                        SetState(s =>
                        {
                            s.Status = UpdateStatus.Downloading;
                            s.DownloadedBytes = snapDl;
                            s.TotalBytes = snapTot;
                            s.ProgressPercent = Math.Round(snapPct, 1);
                            s.SpeedBytesPerSec = snapSpd;
                            s.StatusMessage = totalBytes > 0
                                ? $"正在下载 {release.TagName} ({snapPct:F1}%)"
                                : $"正在下载 {release.TagName} ({FormatBytes(snapDl)})";
                        });
                    }
                }
            }

            // SHA-256 verification against SHA256SUMS.txt if available
            bool verified = false;
            if (!string.IsNullOrWhiteSpace(release.ChecksumAssetUrl))
            {
                SetState(s =>
                {
                    s.Status = UpdateStatus.Verifying;
                    s.ProgressPercent = 100;
                    s.SpeedBytesPerSec = 0;
                    s.StatusMessage = "正在校验 SHA-256 文件完整性...";
                });

                verified = await VerifySha256Async(partPath, release.AssetName, release.ChecksumAssetUrl, ct);
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(partPath, destPath);

            long finalSize = new FileInfo(destPath).Length;
            return SetState(s =>
            {
                s.Status = UpdateStatus.ReadyToInstall;
                s.DownloadedBytes = finalSize;
                s.TotalBytes = finalSize;
                s.ProgressPercent = 100;
                s.SpeedBytesPerSec = 0;
                s.DownloadedFilePath = destPath;
                s.Sha256Verified = verified;
                s.ErrorMessage = "";
                s.StatusMessage = verified
                    ? $"下载完成并通过 SHA-256 校验，可立即安装 {release.TagName}"
                    : $"下载完成，可立即安装 {release.TagName}";
            });
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            return SetState(s =>
            {
                s.Status = s.HasUpdate ? UpdateStatus.UpdateAvailable : UpdateStatus.Idle;
                s.DownloadedBytes = 0;
                s.ProgressPercent = 0;
                s.SpeedBytesPerSec = 0;
                s.StatusMessage = "已取消下载";
            });
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            return SetState(s =>
            {
                s.Status = UpdateStatus.Error;
                s.SpeedBytesPerSec = 0;
                s.ErrorMessage = ex.Message;
                s.StatusMessage = "下载更新失败，请重试或前往 GitHub 手动下载";
            });
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_downloadCts, cts))
                    _downloadCts = null;
            }
            cts.Dispose();
        }
    }

    public void CancelDownload()
    {
        lock (_sync)
        {
            try { _downloadCts?.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Launches either the downloaded Inno Setup installer or a clean portable ZIP swapper script.
    /// Returns (Success, ErrorMessage). Caller should exit the app when Success is true.
    /// </summary>
    public (bool Success, string? ErrorMessage) LaunchDownloadedUpdate()
    {
        var snap = GetState();
        var release = snap.LatestRelease;
        var filePath = snap.DownloadedFilePath;

        if (release == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return (false, "未找到已下载的更新文件，请重新下载。");
        }

        try
        {
            if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = "/SP- /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true
                });
                return (true, null);
            }

            if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string exeName = Path.GetFileName(Environment.ProcessPath ?? Path.Combine(appDir, "SideScreenHUD.exe"));
                if (string.IsNullOrWhiteSpace(exeName)) exeName = "SideScreenHUD.exe";

                if (!CanWriteToDirectory(appDir))
                {
                    // Reveal downloaded zip so user can extract manually if located in read-only directory
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    });
                    return (false, "当前程序目录无写入权限（如 Program Files），已为你定位下载的更新包，建议改用安装包升级。");
                }

                string stageDir = Path.Combine(UpdatesDirectory, "staging_" + SanitizePathSegment(release.TagName));
                if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
                Directory.CreateDirectory(stageDir);
                ZipFile.ExtractToDirectory(filePath, stageDir, overwriteFiles: true);

                // Validate extracted package contains executable
                string stagedExe = Path.Combine(stageDir, exeName);
                if (!File.Exists(stagedExe))
                {
                    stagedExe = Directory.GetFiles(stageDir, "SideScreenHUD.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                    if (string.IsNullOrWhiteSpace(stagedExe))
                    {
                        return (false, "更新压缩包中缺少 SideScreenHUD.exe，已中止更新。");
                    }
                    stageDir = Path.GetDirectoryName(stagedExe) ?? stageDir;
                }

                string scriptPath = Path.Combine(UpdatesDirectory, "apply-portable-update.ps1");
                int pid = Environment.ProcessId;
                string relaunchExe = Path.Combine(appDir, Path.GetFileName(stagedExe));

                string psScript = BuildPortableSwapScript(pid, stageDir, appDir, relaunchExe);
                File.WriteAllText(scriptPath, psScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return (true, null);
            }

            return (false, "不支持的更新文件格式。");
        }
        catch (Exception ex)
        {
            return (false, $"启动更新失败：{ex.Message}");
        }
    }

    public static void OpenUrlInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private async Task<UpdateReleaseInfo?> FetchLatestFromGitHubApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

        using var resp = await _http.SendAsync(req, timeoutCts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API 返回 HTTP {(int)resp.StatusCode}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "").Trim() : "";
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string title = root.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "").Trim() : tag;
        string body = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "").Trim() : "";
        string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? (urlEl.GetString() ?? ReleasesPageUrl) : ReleasesPageUrl;

        DateTimeOffset? pubAt = null;
        if (root.TryGetProperty("published_at", out var pubEl) && DateTimeOffset.TryParse(pubEl.GetString(), out var parsedDate))
            pubAt = parsedDate;

        var assets = new List<(string Name, string Url, long Size)>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                string aName = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                string aUrl = a.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "") : "";
                long aSize = a.TryGetProperty("size", out var asz) && asz.TryGetInt64(out var sz) ? sz : 0;
                if (!string.IsNullOrWhiteSpace(aName) && !string.IsNullOrWhiteSpace(aUrl))
                {
                    assets.Add((aName, aUrl, aSize));
                }
            }
        }

        return BuildReleaseInfo(tag, title, body, htmlUrl, pubAt, assets);
    }

    private async Task<UpdateReleaseInfo?> FetchLatestViaWebRedirectAsync(CancellationToken ct)
    {
        using var resp = await _noRedirectHttp.GetAsync(LatestWebUrl, ct);
        if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
        {
            string loc = resp.Headers.Location.IsAbsoluteUri
                ? resp.Headers.Location.AbsoluteUri
                : new Uri(new Uri("https://github.com"), resp.Headers.Location).AbsoluteUri;

            int idx = loc.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string tag = loc[(idx + "/releases/tag/".Length)..].Trim('/').Split('?', '#')[0];
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    string rid = $"win-{CurrentArch}";
                    string baseDl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}";
                    var synthesized = new List<(string Name, string Url, long Size)>
                    {
                        ($"SideScreenHUD-{tag}-{rid}-Setup.exe", $"{baseDl}/SideScreenHUD-{tag}-{rid}-Setup.exe", 0),
                        ($"SideScreenHUD-{tag}-{rid}.zip", $"{baseDl}/SideScreenHUD-{tag}-{rid}.zip", 0),
                        ("SHA256SUMS.txt", $"{baseDl}/SHA256SUMS.txt", 0)
                    };
                    return BuildReleaseInfo(tag, $"星澪硬件监控 {tag}", "已检测到新版本发布，点击下方链接可查看完整更新日志。", loc, null, synthesized);
                }
            }
        }
        return null;
    }

    private UpdateReleaseInfo BuildReleaseInfo(
        string tag,
        string title,
        string body,
        string htmlUrl,
        DateTimeOffset? publishedAt,
        List<(string Name, string Url, long Size)> assets)
    {
        string rid = $"win-{CurrentArch}";
        bool preferSetup = IsInstalledMode || !CanWriteToDirectory(AppContext.BaseDirectory);

        var archSetup = assets.FirstOrDefault(a =>
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        var archZip = assets.FirstOrDefault(a =>
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        var chosen = preferSetup
            ? (!string.IsNullOrEmpty(archSetup.Url) ? archSetup : archZip)
            : (!string.IsNullOrEmpty(archZip.Url) ? archZip : archSetup);

        var checksum = assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        return new UpdateReleaseInfo
        {
            TagName = tag,
            Version = NormalizeVersionDisplay(tag),
            Title = string.IsNullOrWhiteSpace(title) ? $"星澪硬件监控 {tag}" : title,
            Body = body,
            HtmlUrl = htmlUrl,
            PublishedAt = publishedAt,
            AssetName = chosen.Name ?? "",
            AssetDownloadUrl = chosen.Url ?? "",
            AssetSizeBytes = chosen.Size,
            IsInstallerAsset = (chosen.Name ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            ChecksumAssetUrl = checksum.Url ?? ""
        };
    }

    private async Task<bool> VerifySha256Async(string filePath, string assetName, string checksumUrl, CancellationToken ct)
    {
        string expectedHash = "";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            string content = await _http.GetStringAsync(checksumUrl, timeoutCts.Token);

            foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = Regex.Split(line, @"\s+");
                if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
                {
                    expectedHash = parts[0].Trim().ToLowerInvariant();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Checksum file unavailable; continue gracefully without claiming verified
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64)
            return false;

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] hashBytes = await SHA256.HashDataAsync(fs, ct);
        string actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 校验失败（期望 {expectedHash[..12]}...，实际 {actualHash[..12]}...），文件可能损坏。");
        }
        return true;
    }

    private static string BuildPortableSwapScript(int parentPid, string stageDir, string appDir, string relaunchExe)
    {
        static string Esc(string s) => s.Replace("'", "''");
        return $@"
$ErrorActionPreference = 'SilentlyContinue'
$parentPid = {parentPid}
$stageDir = '{Esc(stageDir)}'
$appDir = '{Esc(appDir)}'
$relaunchExe = '{Esc(relaunchExe)}'

# Wait for old process to exit cleanly (up to 15s)
for ($i = 0; $i -lt 75; $i++) {{
    if (-not (Get-Process -Id $parentPid -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 200
}}
Start-Sleep -Milliseconds 400

# Copy updated files over the portable directory (user settings live safely in %AppData%\SideScreenHUD)
for ($attempt = 0; $attempt -lt 8; $attempt++) {{
    try {{
        Copy-Item -Path (Join-Path $stageDir '*') -Destination $appDir -Recurse -Force -ErrorAction Stop
        break
    }} catch {{
        Start-Sleep -Milliseconds 500
    }}
}}

Remove-Item -Path $stageDir -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $relaunchExe) {{
    Start-Process -FilePath $relaunchExe -WorkingDirectory $appDir
}}
";
    }

    public static bool IsVersionIgnored(string? remoteTag, string? ignoredVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteTag) || string.IsNullOrWhiteSpace(ignoredVersion))
            return false;
        return string.Equals(NormalizeVersionDisplay(remoteTag), NormalizeVersionDisplay(ignoredVersion), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeVersionDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "1.0.0";
        string cleaned = raw.Trim();
        int plus = cleaned.IndexOf('+');
        if (plus > 0) cleaned = cleaned[..plus];
        return cleaned.TrimStart('v', 'V');
    }

    /// <summary>
    /// Compares two version tags such as "v1.2.0", "1.1.0", "v1.1.0-b2".
    /// Returns &gt; 0 if remote &gt; current, 0 if equal, &lt; 0 if remote &lt; current.
    /// </summary>
    public static int CompareVersions(string? remoteTag, string? currentVersion)
    {
        var (rVer, rBuild) = ParseVersionParts(remoteTag);
        var (cVer, cBuild) = ParseVersionParts(currentVersion);

        int cmp = rVer.CompareTo(cVer);
        if (cmp != 0) return cmp;
        return rBuild.CompareTo(cBuild);
    }

    private static (Version Numeric, int BuildSuffix) ParseVersionParts(string? raw)
    {
        string s = NormalizeVersionDisplay(raw);
        int buildNum = 0;

        int dash = s.IndexOf('-');
        if (dash > 0)
        {
            string suffix = s[(dash + 1)..];
            s = s[..dash];
            var m = Regex.Match(suffix, @"(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int parsedB))
                buildNum = parsedB;
        }

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int major = parts.Length > 0 && int.TryParse(parts[0], out int p0) ? p0 : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int p1) ? p1 : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int p2) ? p2 : 0;
        int rev = parts.Length > 3 && int.TryParse(parts[3], out int p3) ? p3 : 0;

        return (new Version(major, minor, patch, rev), buildNum);
    }

    private static string ReadCurrentAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoAttr))
        {
            return NormalizeVersionDisplay(infoAttr);
        }
        var ver = asm.GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}" : "1.0.0";
    }

    private static bool DetectInstalledMode()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "unins000.exe")) || File.Exists(Path.Combine(baseDir, "unins000.dat")))
                return true;

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

            return (!string.IsNullOrEmpty(pf) && baseDir.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(pf86) && baseDir.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(localPrograms) && baseDir.StartsWith(localPrograms, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteToDirectory(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeUserAgentVersion(string ver)
    {
        var m = Regex.Match(ver, @"^\d+(\.\d+){0,3}");
        return m.Success ? m.Value : "1.0.0";
    }

    private static string SanitizePathSegment(string seg)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(seg.Length);
        foreach (char c in seg)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.Length > 0 ? sb.ToString() : "latest";
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    private UpdateStateSnapshot SetState(Action<UpdateStateSnapshot> mutator)
    {
        UpdateStateSnapshot snap;
        lock (_sync)
        {
            mutator(_state);
            snap = CloneState(_state);
        }
        RaiseStateChanged(snap);
        return snap;
    }

    private void RaiseStateChanged(UpdateStateSnapshot snap)
    {
        try { StateChanged?.Invoke(snap); } catch { }
    }

    private static UpdateStateSnapshot CloneState(UpdateStateSnapshot s) => new()
    {
        Status = s.Status,
        CurrentVersion = s.CurrentVersion,
        CurrentArch = s.CurrentArch,
        IsInstalledMode = s.IsInstalledMode,
        LatestRelease = s.LatestRelease,
        HasUpdate = s.HasUpdate,
        IsIgnoredVersion = s.IsIgnoredVersion,
        DownloadedBytes = s.DownloadedBytes,
        TotalBytes = s.TotalBytes,
        ProgressPercent = s.ProgressPercent,
        SpeedBytesPerSec = s.SpeedBytesPerSec,
        DownloadedFilePath = s.DownloadedFilePath,
        Sha256Verified = s.Sha256Verified,
        StatusMessage = s.StatusMessage,
        ErrorMessage = s.ErrorMessage,
        LastCheckedAt = s.LastCheckedAt
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelDownload();
        try { _http.Dispose(); } catch { }
        try { _noRedirectHttp.Dispose(); } catch { }
        try { _checkLock.Dispose(); } catch { }
    }
}
