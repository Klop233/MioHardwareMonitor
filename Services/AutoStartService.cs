using System.IO;
using Microsoft.Win32;

namespace SideScreenHUD.Services;

/// <summary>
/// Manages Windows user-level auto-start registration via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Uses the same value name ("SideScreenHUD") as installer/SideScreenHUD.iss so installer and in-app toggle stay in sync.
/// </summary>
public static class AutoStartService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RegistryValueName = "SideScreenHUD";
    public const string AutoStartArg = "--autostart";

    public static string? ResolveExecutablePath()
    {
        var procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath) &&
            !string.Equals(Path.GetFileName(procPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(procPath))
        {
            return procPath;
        }

        var localExe = Path.Combine(AppContext.BaseDirectory, "SideScreenHUD.exe");
        if (File.Exists(localExe))
        {
            return localExe;
        }

        return !string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath) ? procPath : null;
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
            var raw = key?.GetValue(RegistryValueName) as string;
            return !string.IsNullOrWhiteSpace(raw);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Success, string? ErrorMessage) SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true);
            if (key == null)
                return (false, "无法打开 Windows 启动项注册表键。");

            if (enabled)
            {
                var exePath = ResolveExecutablePath();
                if (string.IsNullOrWhiteSpace(exePath))
                    return (false, "无法解析当前程序路径。");

                string cmd = BuildRunCommand(exePath);
                key.SetValue(RegistryValueName, cmd, RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(RegistryValueName) != null)
                {
                    key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// If auto-start is already enabled (e.g. from installer or before moving the portable folder),
    /// keeps the registered executable path and --autostart flag up to date.
    /// </summary>
    public static void SyncPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
            var existing = key?.GetValue(RegistryValueName) as string;
            if (string.IsNullOrWhiteSpace(existing)) return;

            var exePath = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath)) return;

            // Only rewrite when running from the actual SideScreenHUD.exe (not dotnet.exe)
            if (!string.Equals(Path.GetFileName(exePath), "SideScreenHUD.exe", StringComparison.OrdinalIgnoreCase))
                return;

            string expected = BuildRunCommand(exePath);
            if (!string.Equals(existing.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                key!.SetValue(RegistryValueName, expected, RegistryValueKind.String);
            }
        }
        catch
        {
            // Best-effort sync; ignore permission issues
        }
    }

    public static string BuildRunCommand(string exePath) => $"\"{exePath}\" {AutoStartArg}";
}
