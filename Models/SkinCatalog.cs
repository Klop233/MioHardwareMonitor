namespace SideScreenHUD.Models;

/// <summary>One resolved color token set for a given skin + light/dark theme.</summary>
public sealed class SkinPalette
{
    public string Key { get; init; } = "miolab";
    public bool IsDark { get; init; }

    public (byte R, byte G, byte B) BgPage { get; init; }
    public (byte R, byte G, byte B) BgSurface { get; init; }
    public (byte R, byte G, byte B) BgHover { get; init; }
    public (byte R, byte G, byte B) Border { get; init; }
    public (byte R, byte G, byte B) BorderStrong { get; init; }
    public (byte R, byte G, byte B) TextMuted { get; init; }
    public (byte R, byte G, byte B) TextMain { get; init; }
    public (byte R, byte G, byte B) Accent { get; init; }
    public (byte R, byte G, byte B) AccentHover { get; init; }
    public (byte R, byte G, byte B) AccentText { get; init; }
    public (byte R, byte G, byte B) AccentSubtle { get; init; }
    public (byte R, byte G, byte B) Success { get; init; }
    public (byte R, byte G, byte B) SuccessSubtle { get; init; }
    public (byte R, byte G, byte B) Warning { get; init; }
    public (byte R, byte G, byte B) WarningSubtle { get; init; }
    public (byte R, byte G, byte B) Danger { get; init; }
    public (byte R, byte G, byte B) DangerSubtle { get; init; }
}

/// <summary>
/// Color skin catalog. Each skin defines a light and a dark token set (accent family + tinted neutral
/// surfaces). The same table drives the WPF management/update windows, the monitor window chrome and
/// the WebView dashboard (shipped to JS as inline CSS variables).
/// </summary>
public static class SkinCatalog
{
    public sealed record SkinInfo(string Key, string Name, string Hint, string AccentHex);

    public static readonly SkinInfo[] All =
    {
        new("miolab",   "星澪标准蓝", "默认 · 克制专业", "#2B5FD9"),
        new("midnight", "午夜靛蓝",   "深邃冷调 · 夜间首选", "#6366F1"),
        new("graphite", "石墨橙",     "中性硬核 · 工业风", "#C2410C"),
        new("aurora",   "极光青",     "冷静青绿 · 低刺激", "#0F766E"),
        new("sakura",   "樱粉",       "柔和粉调 · 轻盈", "#BE185D"),
        new("amber",    "琥珀金",     "暖金复古 · 护眼", "#A16207")
    };

    public static string Normalize(string? skin)
    {
        string key = (skin ?? "").Trim().ToLowerInvariant();
        foreach (var s in All)
        {
            if (s.Key == key) return s.Key;
        }
        return "miolab";
    }

    public static string GetDisplayName(string? skin)
    {
        string key = Normalize(skin);
        foreach (var s in All)
        {
            if (s.Key == key) return s.Name;
        }
        return All[0].Name;
    }

    private static (byte R, byte G, byte B) C(int hex) =>
        ((byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));

    public static SkinPalette Get(string? skin, bool dark, bool oledTrueBlack = false)
    {
        string key = Normalize(skin);
        if (!dark) return Light(key);
        var d = Dark(key);
        if (!oledTrueBlack) return d;

        return new SkinPalette
        {
            Key = d.Key,
            IsDark = true,
            BgPage = (0, 0, 0),
            BgSurface = (5, 6, 8),
            BgHover = (14, 17, 23),
            Border = (28, 33, 43),
            BorderStrong = (75, 85, 104),
            TextMain = (220, 226, 236),
            TextMuted = (143, 153, 170),
            Accent = d.Accent,
            AccentHover = d.AccentHover,
            AccentText = d.AccentText,
            AccentSubtle = d.AccentSubtle,
            Success = d.Success,
            SuccessSubtle = d.SuccessSubtle,
            Warning = d.Warning,
            WarningSubtle = d.WarningSubtle,
            Danger = d.Danger,
            DangerSubtle = d.DangerSubtle
        };
    }

    // Semantic status colors are shared across skins so warnings/errors stay unambiguous.
    private static SkinPalette Light(string key)
    {
        var (accent, hover, accentText, subtle, page, surface, hoverBg, border, borderStrong, muted, text) = key switch
        {
            "midnight" => (0x4F46E5, 0x4338CA, 0x4338CA, 0xEEF0FE, 0xF5F7FC, 0xFFFFFF, 0xEFF3FA, 0xE1E6F0, 0x8590A6, 0x59647A, 0x1B2233),
            "graphite" => (0xC2410C, 0x9A3412, 0xC2410C, 0xFFF1E9, 0xF8F8F7, 0xFFFFFF, 0xF2F2F0, 0xE5E4E0, 0x8C8A84, 0x5D5B55, 0x23221F),
            "aurora"   => (0x0F766E, 0x115E59, 0x0F766E, 0xE3F4F1, 0xF5FAF9, 0xFFFFFF, 0xEDF5F3, 0xDEEAE7, 0x7F9791, 0x526864, 0x172522),
            "sakura"   => (0xBE185D, 0xA11350, 0xBE185D, 0xFCEEF4, 0xFBF7F9, 0xFFFFFF, 0xF6EEF2, 0xEFE0E7, 0x9A8590, 0x6A5962, 0x251B20),
            "amber"    => (0xA16207, 0x854D0E, 0xA16207, 0xFDF3DD, 0xFBFAF6, 0xFFFFFF, 0xF5F3EC, 0xEAE6DA, 0x968F7B, 0x6A6455, 0x25221A),
            _          => (0x2B5FD9, 0x234DB5, 0x2B5FD9, 0xEAF1FB, 0xF7F8FA, 0xFFFFFF, 0xF1F3F5, 0xE4E7EC, 0x8A94A3, 0x5B6472, 0x1F2937)
        };

        return new SkinPalette
        {
            Key = key,
            IsDark = false,
            BgPage = C(page),
            BgSurface = C(surface),
            BgHover = C(hoverBg),
            Border = C(border),
            BorderStrong = C(borderStrong),
            TextMuted = C(muted),
            TextMain = C(text),
            Accent = C(accent),
            AccentHover = C(hover),
            AccentText = C(accentText),
            AccentSubtle = C(subtle),
            Success = C(0x1F7A4A),
            SuccessSubtle = C(0xE7F4EC),
            Warning = C(0x8A5300),
            WarningSubtle = C(0xFFF3DC),
            Danger = C(0xB42318),
            DangerSubtle = C(0xFDECEA)
        };
    }

    private static SkinPalette Dark(string key)
    {
        var (accent, hover, accentText, subtle, page, surface, hoverBg, border, borderStrong, muted, text) = key switch
        {
            "midnight" => (0x6366F1, 0x4F46E5, 0x8E9BFF, 0x1C2244, 0x0B0E17, 0x121728, 0x1A2136, 0x262E45, 0x5A6484, 0x9AA4BE, 0xE7EAF3),
            "graphite" => (0xF97316, 0xEA6A12, 0xFBA76A, 0x35210F, 0x121212, 0x1A1A19, 0x232321, 0x322F2B, 0x6B665E, 0xA6A29A, 0xECEAE6),
            "aurora"   => (0x14B8A6, 0x0D9488, 0x5EEAD4, 0x10312E, 0x0A1312, 0x121C1B, 0x1A2826, 0x24352F, 0x557067, 0x96ACA6, 0xE5F0EC),
            "sakura"   => (0xEC4899, 0xDB2777, 0xF9A8D4, 0x3A1B29, 0x150E12, 0x1F1519, 0x291B21, 0x3A2A32, 0x7A5C68, 0xB6A0A9, 0xF2E7EC),
            "amber"    => (0xD97706, 0xB45309, 0xFBBF24, 0x3A2A0E, 0x12100B, 0x1B1812, 0x252117, 0x37301F, 0x7C7257, 0xB3A88B, 0xF1EBDC),
            _          => (0x3D6FE0, 0x3360CC, 0x7BA3F2, 0x1B2A45, 0x0F1115, 0x171A21, 0x1F232B, 0x2A2F3A, 0x5E6878, 0xA1A9B6, 0xE6E8EC)
        };

        return new SkinPalette
        {
            Key = key,
            IsDark = true,
            BgPage = C(page),
            BgSurface = C(surface),
            BgHover = C(hoverBg),
            Border = C(border),
            BorderStrong = C(borderStrong),
            TextMuted = C(muted),
            TextMain = C(text),
            Accent = C(accent),
            AccentHover = C(hover),
            AccentText = C(accentText),
            AccentSubtle = C(subtle),
            Success = C(0x4CC38A),
            SuccessSubtle = C(0x15291F),
            Warning = C(0xE8A33D),
            WarningSubtle = C(0x2E2412),
            Danger = C(0xF0705F),
            DangerSubtle = C(0x33191A)
        };
    }

    public static string Hex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Palette for the given settings (resolves theme + skin + OLED true black in one step).</summary>
    public static SkinPalette ForSettings(AppSettings settings) =>
        Get(settings.Skin, string.Equals(settings.Theme, "dark", StringComparison.OrdinalIgnoreCase), settings.OledTrueBlack);

    /// <summary>CSS variables to push to the WebView dashboard for the given settings.</summary>
    public static Dictionary<string, string> CssVariablesFor(AppSettings settings) =>
        ToCssVariables(ForSettings(settings), settings.ColorScheme);

    /// <summary>CSS custom properties consumed by web/styles.css and the canvas charts.</summary>
    public static Dictionary<string, string> ToCssVariables(SkinPalette p, string? colorScheme)
    {
        bool global = string.Equals(colorScheme, "global", StringComparison.OrdinalIgnoreCase);
        string up = global ? Hex(p.Success) : Hex(p.Danger);
        string down = global ? Hex(p.Danger) : Hex(p.Success);
        string upBg = global ? Hex(p.SuccessSubtle) : Hex(p.DangerSubtle);
        string downBg = global ? Hex(p.DangerSubtle) : Hex(p.SuccessSubtle);

        return new Dictionary<string, string>
        {
            ["--bg-page"] = Hex(p.BgPage),
            ["--bg-surface"] = Hex(p.BgSurface),
            ["--bg-hover"] = Hex(p.BgHover),
            ["--border"] = Hex(p.Border),
            ["--border-strong"] = Hex(p.BorderStrong),
            ["--text-muted"] = Hex(p.TextMuted),
            ["--text"] = Hex(p.TextMain),
            ["--accent"] = Hex(p.Accent),
            ["--accent-hover"] = Hex(p.AccentHover),
            ["--accent-text"] = Hex(p.AccentText),
            ["--accent-subtle"] = Hex(p.AccentSubtle),
            ["--success"] = Hex(p.Success),
            ["--success-subtle"] = Hex(p.SuccessSubtle),
            ["--warning"] = Hex(p.Warning),
            ["--warning-subtle"] = Hex(p.WarningSubtle),
            ["--danger"] = Hex(p.Danger),
            ["--danger-solid"] = p.IsDark ? "#C2392B" : Hex(p.Danger),
            ["--danger-subtle"] = Hex(p.DangerSubtle),
            ["--info"] = Hex(p.AccentText),
            ["--info-subtle"] = Hex(p.AccentSubtle),
            ["--up-color"] = up,
            ["--down-color"] = down,
            ["--up-bg"] = upBg,
            ["--down-bg"] = downBg
        };
    }
}
