using System.Windows;
using System.Windows.Media;
using SideScreenHUD.Models;

namespace SideScreenHUD.Services;

/// <summary>
/// Writes a <see cref="SkinPalette"/> into a window's resource dictionary so every
/// <c>{DynamicResource ...}</c> reference updates live when the skin or theme changes.
/// </summary>
public static class SkinApplier
{
    public static void Apply(ResourceDictionary resources, string? skin, string? theme, bool oledTrueBlack = false)
    {
        bool dark = SettingsService.NormalizeTheme(theme) == "dark";
        var p = SkinCatalog.Get(skin, dark, oledTrueBlack);

        Set(resources, "BgPage", p.BgPage);
        Set(resources, "BgSurface", p.BgSurface);
        Set(resources, "BgHover", p.BgHover);
        Set(resources, "BorderToken", p.Border);
        Set(resources, "BorderStrong", p.BorderStrong);
        Set(resources, "TextMuted", p.TextMuted);
        Set(resources, "TextMain", p.TextMain);
        Set(resources, "Accent", p.Accent);
        Set(resources, "AccentHover", p.AccentHover);
        Set(resources, "AccentText", p.AccentText);
        Set(resources, "AccentSubtle", p.AccentSubtle);
        Set(resources, "Success", p.Success);
        Set(resources, "SuccessSubtle", p.SuccessSubtle);
        Set(resources, "Warning", p.Warning);
        Set(resources, "WarningSubtle", p.WarningSubtle);
        Set(resources, "Danger", p.Danger);
        Set(resources, "DangerSubtle", p.DangerSubtle);
    }

    /// <summary>Token set used by the WPF shader-less window frame (title bar / border colors).</summary>
    public static SkinPalette Resolve(string? skin, string? theme, bool oledTrueBlack = false) =>
        SkinCatalog.Get(skin, SettingsService.NormalizeTheme(theme) == "dark", oledTrueBlack);

    private static void Set(ResourceDictionary resources, string key, (byte R, byte G, byte B) c)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        resources[key] = brush;
    }
}
