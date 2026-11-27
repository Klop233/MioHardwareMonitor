using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SideScreenHUD.Models;
using SideScreenHUD.Native;
using SideScreenHUD.Services;

namespace SideScreenHUD;

public partial class UpdateWindow : Window
{
    private readonly AppController _controller;
    private string? _appliedTheme;

    public UpdateWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        ApplyThemeResources(_controller.Settings.Theme, _controller.Settings.Skin, _controller.Settings.OledTrueBlack);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                bool isDark = SettingsService.NormalizeTheme(_controller.Settings.Theme) == "dark";
                Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
            }
        };

        Loaded += (_, _) =>
        {
            SyncTopMost();
            RefreshFromState(_controller.GetUpdateState());
        };

        SizeChanged += (_, _) => UpdateProgressBarWidth(_controller.GetUpdateState().ProgressPercent);
        _controller.UpdateStateChanged += Controller_UpdateStateChanged;
        _controller.StateChanged += Controller_StateChanged;
        Closed += (_, _) =>
        {
            _controller.UpdateStateChanged -= Controller_UpdateStateChanged;
            _controller.StateChanged -= Controller_StateChanged;
        };
    }

    private void Controller_StateChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyThemeResources(_controller.Settings.Theme, _controller.Settings.Skin, _controller.Settings.OledTrueBlack);
            SyncTopMost();
        });
    }

    private void Controller_UpdateStateChanged(UpdateStateSnapshot snap)
    {
        Dispatcher.BeginInvoke(() => RefreshFromState(snap));
    }

    private void SyncTopMost()
    {
        Topmost = _controller.IsDisplayOpen && _controller.Settings.AlwaysOnTop;
    }

    private void ApplyThemeResources(string? theme, string? skin = null, bool oledTrueBlack = false)
    {
        string norm = SettingsService.NormalizeTheme(theme);
        string skinKey = SkinCatalog.Normalize(skin);
        string cacheKey = $"{norm}|{skinKey}|{(oledTrueBlack ? 1 : 0)}";
        bool isDark = norm == "dark";
        if (_appliedTheme == cacheKey) return;
        _appliedTheme = cacheKey;

        SkinApplier.Apply(Resources, skinKey, norm, oledTrueBlack);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Interop.ApplyWindowFrame(hwnd, isDark, rounded: true);
        }
    }

    public void RefreshFromState(UpdateStateSnapshot snap)
    {
        var rel = snap.LatestRelease;
        string pkgKind = rel?.IsInstallerAsset == true ? "安装包" : (snap.IsInstalledMode ? "安装版" : "便携版");
        PkgBadgeText.Text = $"win-{snap.CurrentArch} · {pkgKind}";

        if (rel != null)
        {
            TxtHeadline.Text = snap.HasUpdate
                ? $"发现新版本 {rel.TagName}"
                : $"当前已是最新版本 (v{snap.CurrentVersion})";
            TxtVersionTransition.Text = $"v{snap.CurrentVersion} → {rel.TagName}";

            string dateStr = rel.PublishedAt.HasValue
                ? rel.PublishedAt.Value.ToLocalTime().ToString("yyyy.MM.dd HH:mm")
                : "—";
            string sizeStr = rel.AssetSizeBytes > 0
                ? UpdateService.FormatBytes(rel.AssetSizeBytes)
                : "未知大小";
            TxtMeta.Text = $"发布时间：{dateStr} · 更新包：{sizeStr}";
            TxtReleaseNotes.Text = FormatReleaseNotes(rel.Body);
        }
        else
        {
            TxtHeadline.Text = snap.Status == UpdateStatus.Checking ? "正在检查更新..." : "软件更新";
            TxtVersionTransition.Text = $"v{snap.CurrentVersion}";
            TxtMeta.Text = snap.StatusMessage;
            TxtReleaseNotes.Text = string.IsNullOrWhiteSpace(snap.ErrorMessage)
                ? snap.StatusMessage
                : snap.ErrorMessage;
        }

        // Update progress & buttons per state
        bool showProgress = snap.Status is UpdateStatus.Downloading or UpdateStatus.Verifying or UpdateStatus.ReadyToInstall or UpdateStatus.Error;
        ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        TxtErrorDetail.Visibility = !string.IsNullOrWhiteSpace(snap.ErrorMessage) ? Visibility.Visible : Visibility.Collapsed;
        TxtErrorDetail.Text = snap.ErrorMessage;
        BtnIgnoreVersion.Visibility = snap.HasUpdate ? Visibility.Visible : Visibility.Collapsed;

        switch (snap.Status)
        {
            case UpdateStatus.Downloading:
                TxtProgressStatus.Text = snap.StatusMessage;
                TxtProgressStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
                string speedStr = snap.SpeedBytesPerSec > 0 ? $" · {UpdateService.FormatBytes((long)snap.SpeedBytesPerSec)}/s" : "";
                TxtProgressDetail.Text = snap.TotalBytes > 0
                    ? $"{UpdateService.FormatBytes(snap.DownloadedBytes)} / {UpdateService.FormatBytes(snap.TotalBytes)}{speedStr}"
                    : $"{UpdateService.FormatBytes(snap.DownloadedBytes)}{speedStr}";
                ProgressFill.SetResourceReference(Border.BackgroundProperty, "Accent");
                UpdateProgressBarWidth(snap.ProgressPercent);

                BtnIgnoreVersion.IsEnabled = false;
                BtnRemindLater.Content = "后台下载";
                BtnRemindLater.IsEnabled = true;
                BtnPrimaryAction.Content = "取消下载";
                BtnPrimaryAction.Style = (Style)FindResource("SecondaryButton");
                BtnPrimaryAction.IsEnabled = true;
                break;

            case UpdateStatus.Verifying:
                TxtProgressStatus.Text = "正在校验 SHA-256 文件完整性...";
                TxtProgressStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                TxtProgressDetail.Text = "100%";
                ProgressFill.SetResourceReference(Border.BackgroundProperty, "Accent");
                UpdateProgressBarWidth(100);

                BtnIgnoreVersion.IsEnabled = false;
                BtnRemindLater.IsEnabled = false;
                BtnPrimaryAction.Content = "校验中...";
                BtnPrimaryAction.Style = (Style)FindResource("PrimaryButton");
                BtnPrimaryAction.IsEnabled = false;
                break;

            case UpdateStatus.ReadyToInstall:
                TxtProgressStatus.Text = snap.Sha256Verified
                    ? "更新包已下载完成并通过 SHA-256 校验"
                    : "更新包已下载完成，准备安装";
                TxtProgressStatus.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                TxtProgressDetail.Text = snap.TotalBytes > 0 ? UpdateService.FormatBytes(snap.TotalBytes) : "100%";
                ProgressFill.SetResourceReference(Border.BackgroundProperty, "Success");
                UpdateProgressBarWidth(100);

                BtnIgnoreVersion.IsEnabled = snap.HasUpdate;
                BtnRemindLater.Content = "下次再说";
                BtnRemindLater.IsEnabled = true;
                BtnPrimaryAction.Content = rel?.IsInstallerAsset == true ? "立即运行安装包" : "立即更新并重启";
                BtnPrimaryAction.Style = (Style)FindResource("PrimaryButton");
                BtnPrimaryAction.IsEnabled = true;
                break;

            case UpdateStatus.Error:
                TxtProgressStatus.Text = snap.StatusMessage;
                TxtProgressStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                TxtProgressDetail.Text = "失败";
                ProgressFill.SetResourceReference(Border.BackgroundProperty, "Danger");
                UpdateProgressBarWidth(100);

                BtnIgnoreVersion.IsEnabled = snap.HasUpdate && rel != null;
                BtnRemindLater.Content = "下次再说";
                BtnRemindLater.IsEnabled = true;
                BtnPrimaryAction.Content = rel != null && snap.HasUpdate ? "重试下载" : "重新检查";
                BtnPrimaryAction.Style = (Style)FindResource("PrimaryButton");
                BtnPrimaryAction.IsEnabled = true;
                break;

            case UpdateStatus.UpToDate:
                BtnIgnoreVersion.Visibility = Visibility.Collapsed;
                BtnRemindLater.Content = "关闭";
                BtnRemindLater.IsEnabled = true;
                BtnPrimaryAction.Content = "重新检查";
                BtnPrimaryAction.Style = (Style)FindResource("SecondaryButton");
                BtnPrimaryAction.IsEnabled = true;
                break;

            case UpdateStatus.Checking:
                BtnIgnoreVersion.IsEnabled = false;
                BtnRemindLater.Content = "关闭";
                BtnRemindLater.IsEnabled = true;
                BtnPrimaryAction.Content = "检查中...";
                BtnPrimaryAction.Style = (Style)FindResource("PrimaryButton");
                BtnPrimaryAction.IsEnabled = false;
                break;

            default: // UpdateAvailable / Idle
                BtnIgnoreVersion.Visibility = snap.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
                BtnIgnoreVersion.IsEnabled = snap.HasUpdate;
                BtnRemindLater.Content = "下次再说";
                BtnRemindLater.IsEnabled = true;
                BtnPrimaryAction.Content = "立即下载更新";
                BtnPrimaryAction.Style = (Style)FindResource("PrimaryButton");
                BtnPrimaryAction.IsEnabled = snap.HasUpdate && rel != null && !string.IsNullOrWhiteSpace(rel.AssetDownloadUrl);
                break;
        }
    }

    private void UpdateProgressBarWidth(double pct)
    {
        if (ProgressFill.Parent is FrameworkElement track && track.ActualWidth > 0)
        {
            ProgressFill.Width = Math.Max(0, Math.Min(track.ActualWidth, track.ActualWidth * Math.Clamp(pct, 0, 100) / 100.0));
        }
    }

    private static string FormatReleaseNotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "该版本未填写详细更新日志，可点击右上角前往 GitHub 发布页查看完整变更记录。";

        // Lightly clean raw markdown headings/bold markers so plain-text viewer looks neat
        string text = raw.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = text.Replace("**", "");
        return text.Trim();
    }

    private void BtnOpenRelease_Click(object sender, RoutedEventArgs e)
    {
        var url = _controller.GetUpdateState().LatestRelease?.HtmlUrl;
        UpdateService.OpenUrlInBrowser(string.IsNullOrWhiteSpace(url) ? UpdateService.ReleasesPageUrl : url);
    }

    private void BtnIgnoreVersion_Click(object sender, RoutedEventArgs e)
    {
        var tag = _controller.GetUpdateState().LatestRelease?.TagName;
        if (!string.IsNullOrWhiteSpace(tag))
        {
            _controller.IgnoreUpdateVersion(tag);
        }
        Close();
    }

    private void BtnRemindLater_Click(object sender, RoutedEventArgs e)
    {
        var tag = _controller.GetUpdateState().LatestRelease?.TagName;
        if (!string.IsNullOrWhiteSpace(tag))
        {
            _controller.RemindUpdateLater(tag);
        }
        Close();
    }

    private void BtnPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        var snap = _controller.GetUpdateState();
        switch (snap.Status)
        {
            case UpdateStatus.Downloading:
                _controller.CancelUpdateDownload();
                break;
            case UpdateStatus.ReadyToInstall:
                var (ok, err) = _controller.ApplyDownloadedUpdate();
                if (!ok && !string.IsNullOrWhiteSpace(err))
                {
                    TxtErrorDetail.Text = err;
                    TxtErrorDetail.Visibility = Visibility.Visible;
                    ProgressPanel.Visibility = Visibility.Visible;
                }
                break;
            case UpdateStatus.UpdateAvailable:
            case UpdateStatus.Error when snap.HasUpdate && snap.LatestRelease != null:
                _ = _controller.DownloadLatestUpdateAsync();
                break;
            default:
                _ = _controller.CheckForUpdatesAsync(manual: true, showDialogIfAvailable: true);
                break;
        }
    }
}
