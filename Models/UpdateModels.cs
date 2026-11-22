namespace SideScreenHUD.Models;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Verifying,
    ReadyToInstall,
    Error
}

public sealed class UpdateReleaseInfo
{
    public string TagName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }

    public string AssetName { get; set; } = "";
    public string AssetDownloadUrl { get; set; } = "";
    public long AssetSizeBytes { get; set; }
    public bool IsInstallerAsset { get; set; }
    public string ChecksumAssetUrl { get; set; } = "";
}

public sealed class UpdateStateSnapshot
{
    public UpdateStatus Status { get; set; } = UpdateStatus.Idle;
    public string CurrentVersion { get; set; } = "1.0.0";
    public string CurrentArch { get; set; } = "x64";
    public bool IsInstalledMode { get; set; }
    public UpdateReleaseInfo? LatestRelease { get; set; }
    public bool HasUpdate { get; set; }
    public bool IsIgnoredVersion { get; set; }

    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double ProgressPercent { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public string DownloadedFilePath { get; set; } = "";
    public bool Sha256Verified { get; set; }

    public string StatusMessage { get; set; } = "尚未检查更新";
    public string ErrorMessage { get; set; } = "";
    public DateTimeOffset? LastCheckedAt { get; set; }
}
