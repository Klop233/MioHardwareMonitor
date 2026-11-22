using System.Runtime.InteropServices;
using SideScreenHUD.Models;
using SideScreenHUD.Native;

namespace SideScreenHUD.Services;

public sealed class DisplayManager
{
    public List<MonitorDescriptor> GetMonitors()
    {
        var adapterMap = QueryDisplayAdapters();
        var list = new List<MonitorDescriptor>();
        int idx = 0;

        Win32Interop.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref Win32Interop.RECT _, IntPtr _) =>
        {
            var info = new Win32Interop.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<Win32Interop.MONITORINFOEX>()
            };

            if (Win32Interop.GetMonitorInfo(hMonitor, ref info))
            {
                idx++;
                bool isPrimary = (info.dwFlags & 1) != 0;
                double scale = 1.0;
                try
                {
                    if (Win32Interop.GetDpiForMonitor(hMonitor, Win32Interop.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    {
                        scale = Math.Round(dpiX / 96.0, 2);
                    }
                }
                catch { }

                int hz = 60;
                try
                {
                    var devMode = new Win32Interop.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32Interop.DEVMODE>() };
                    if (Win32Interop.EnumDisplaySettings(info.szDevice, Win32Interop.ENUM_CURRENT_SETTINGS, ref devMode) && devMode.dmDisplayFrequency > 0)
                    {
                        hz = devMode.dmDisplayFrequency;
                    }
                }
                catch { }

                adapterMap.TryGetValue(info.szDevice, out var adapterName);
                adapterName = CleanAdapterName(adapterName);

                list.Add(new MonitorDescriptor
                {
                    Index = idx,
                    DeviceName = info.szDevice,
                    AdapterName = adapterName,
                    IsPrimary = isPrimary,
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Width,
                    Height = info.rcMonitor.Height,
                    WorkLeft = info.rcWork.Left,
                    WorkTop = info.rcWork.Top,
                    WorkWidth = info.rcWork.Width,
                    WorkHeight = info.rcWork.Height,
                    RefreshRateHz = hz,
                    ScaleFactor = scale
                });
            }
            return true;
        };

        Win32Interop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        var sorted = list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Left)
            .ThenBy(m => m.Top)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].Index = i + 1;
            var m = sorted[i];
            var tag = m.IsPrimary ? "主屏" : "副屏";
            var gpuPart = string.IsNullOrWhiteSpace(m.AdapterName) ? "" : $" · {m.AdapterName}";
            m.FriendlyName = $"屏幕 {m.Index} ({tag} {m.Width}×{m.Height} @{m.RefreshRateHz}Hz{gpuPart})";
        }

        return sorted;
    }

    public MonitorDescriptor? ResolveTargetMonitor(string? preferredDevice, bool preferSecondary = true)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredDevice))
        {
            var matched = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, preferredDevice, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }

        if (preferSecondary)
        {
            var secondary = monitors.FirstOrDefault(m => !m.IsPrimary);
            if (secondary != null) return secondary;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public MonitorDescriptor? GetMonitorForWindow(IntPtr hwnd)
    {
        var hMon = Win32Interop.MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (hMon == IntPtr.Zero) return null;

        var info = new Win32Interop.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32Interop.MONITORINFOEX>() };
        if (!Win32Interop.GetMonitorInfo(hMon, ref info)) return null;

        return GetMonitors().FirstOrDefault(m => string.Equals(m.DeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase));
    }

    internal static Win32Interop.RECT CalculateFullScreenRect(MonitorDescriptor mon)
    {
        return new Win32Interop.RECT
        {
            Left = mon.Left,
            Top = mon.Top,
            Right = mon.Left + mon.Width,
            Bottom = mon.Top + mon.Height
        };
    }

    internal static Win32Interop.RECT CalculateFixedWindowRect(MonitorDescriptor mon, AppSettings s)
    {
        const int minW = 420;
        const int minH = 300;
        int margin = Math.Clamp(s.WindowEdgeMargin, 0, Math.Min(200, Math.Min(mon.WorkWidth / 4, mon.WorkHeight / 4)));
        int availW = Math.Max(minW, mon.WorkWidth - margin * 2);
        int availH = Math.Max(minH, mon.WorkHeight - margin * 2);

        string preset = SettingsService.NormalizePositionPreset(s.WindowPositionPreset);
        int w;
        int h;
        int x;
        int y;

        switch (preset)
        {
            case "left-half":
                w = Math.Max(minW, availW / 2);
                h = availH;
                x = mon.WorkLeft + margin;
                y = mon.WorkTop + margin;
                break;

            case "right-half":
                w = Math.Max(minW, availW / 2);
                h = availH;
                x = mon.WorkLeft + mon.WorkWidth - margin - w;
                y = mon.WorkTop + margin;
                break;

            case "top-half":
                w = availW;
                h = Math.Max(minH, availH / 2);
                x = mon.WorkLeft + margin;
                y = mon.WorkTop + margin;
                break;

            case "bottom-half":
                w = availW;
                h = Math.Max(minH, availH / 2);
                x = mon.WorkLeft + margin;
                y = mon.WorkTop + mon.WorkHeight - margin - h;
                break;

            case "top-left":
                w = (int)Math.Clamp(s.WindowWidth, minW, availW);
                h = (int)Math.Clamp(s.WindowHeight, minH, availH);
                x = mon.WorkLeft + margin;
                y = mon.WorkTop + margin;
                break;

            case "top-right":
                w = (int)Math.Clamp(s.WindowWidth, minW, availW);
                h = (int)Math.Clamp(s.WindowHeight, minH, availH);
                x = mon.WorkLeft + mon.WorkWidth - margin - w;
                y = mon.WorkTop + margin;
                break;

            case "bottom-left":
                w = (int)Math.Clamp(s.WindowWidth, minW, availW);
                h = (int)Math.Clamp(s.WindowHeight, minH, availH);
                x = mon.WorkLeft + margin;
                y = mon.WorkTop + mon.WorkHeight - margin - h;
                break;

            case "bottom-right":
                w = (int)Math.Clamp(s.WindowWidth, minW, availW);
                h = (int)Math.Clamp(s.WindowHeight, minH, availH);
                x = mon.WorkLeft + mon.WorkWidth - margin - w;
                y = mon.WorkTop + mon.WorkHeight - margin - h;
                break;

            case "center":
                w = (int)Math.Clamp(s.WindowWidth, minW, availW);
                h = (int)Math.Clamp(s.WindowHeight, minH, availH);
                x = mon.WorkLeft + (mon.WorkWidth - w) / 2;
                y = mon.WorkTop + (mon.WorkHeight - h) / 2;
                break;

            default: // "custom"
                w = (int)Math.Clamp(s.WindowWidth, minW, Math.Max(minW, mon.Width));
                h = (int)Math.Clamp(s.WindowHeight, minH, Math.Max(minH, mon.Height));
                if (s.HasWindowOffset)
                {
                    x = mon.WorkLeft + s.WindowOffsetX;
                    y = mon.WorkTop + s.WindowOffsetY;
                }
                else
                {
                    bool savedInsideTarget =
                        s.WindowLeft >= mon.Left - 20 &&
                        s.WindowLeft + 200 <= mon.Left + mon.Width &&
                        s.WindowTop >= mon.Top - 20 &&
                        s.WindowTop + 120 <= mon.Top + mon.Height;

                    x = savedInsideTarget ? (int)Math.Round(s.WindowLeft) : mon.WorkLeft + (mon.WorkWidth - w) / 2;
                    y = savedInsideTarget ? (int)Math.Round(s.WindowTop) : mon.WorkTop + (mon.WorkHeight - h) / 2;
                }

                // Clamp strictly inside the target monitor so fixed windows never bleed onto neighboring displays
                int maxX = Math.Max(mon.Left, mon.Left + mon.Width - w);
                int maxY = Math.Max(mon.Top, mon.Top + mon.Height - h);
                x = Math.Clamp(x, mon.Left, maxX);
                y = Math.Clamp(y, mon.Top, maxY);
                break;
        }

        return new Win32Interop.RECT
        {
            Left = x,
            Top = y,
            Right = x + w,
            Bottom = y + h
        };
    }

    public MonitorDescriptor? ResolveControlWindowMonitor(string? displayMonitorDevice)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;

        // Prefer primary monitor if it's different from the monitor window's screen
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        if (primary != null && !string.Equals(primary.DeviceName, displayMonitorDevice, StringComparison.OrdinalIgnoreCase))
            return primary;

        // Otherwise pick any other monitor so Settings and Monitor stay physically separated
        var other = monitors.FirstOrDefault(m => !string.Equals(m.DeviceName, displayMonitorDevice, StringComparison.OrdinalIgnoreCase));
        if (other != null) return other;

        return primary ?? monitors[0];
    }

    public bool IsPointVisibleOnAnyMonitor(double physicalX, double physicalY, double minVisibleWidth = 160, double minVisibleHeight = 100)
    {
        foreach (var m in GetMonitors())
        {
            if (physicalX + minVisibleWidth >= m.WorkLeft &&
                physicalX <= m.WorkLeft + m.WorkWidth - 80 &&
                physicalY + minVisibleHeight >= m.WorkTop &&
                physicalY <= m.WorkTop + m.WorkHeight - 60)
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, string> QueryDisplayAdapters()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (uint i = 0; i < 32; i++)
            {
                var dd = new Win32Interop.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32Interop.DISPLAY_DEVICE>() };
                if (!Win32Interop.EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & 1) != 0 && !string.IsNullOrWhiteSpace(dd.DeviceName))
                {
                    map[dd.DeviceName] = dd.DeviceString?.Trim() ?? "";
                }
            }
        }
        catch { }
        return map;
    }

    private static string CleanAdapterName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();
    }
}
