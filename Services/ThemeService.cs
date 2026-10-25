using System.Windows.Media;

namespace OrtakAlan;

public sealed record ThemeSet(
    string Name,
    bool Dark,
    Color Paper,
    Color Ink,
    Color Muted,
    Color Line,
    Color Wash,
    Color Bubble,
    Color Field,
    Color Accent,
    Color OnAccent,
    Color Danger);

public static class ThemeService
{
    public static IReadOnlyList<ThemeSet> Light { get; } =
    [
        Set(false, "Kağıt", "FFFFFF", "111827", "4B5563", "E5E7EB", "F3F4F6", "EEEFF2", "D1D5DB", "0F766E", "FFFFFF", "9F1239"),
        Set(false, "Keten", "FBF7F2", "1C1917", "57534E", "E7E0D6", "F4EFE8", "EFE6DC", "D6CDC2", "9A3412", "FFFFFF", "9F1239"),
        Set(false, "Sis", "F4F7FB", "0F172A", "475569", "E2E8F0", "EEF2F6", "E7EDF4", "CBD5E1", "1D4ED8", "FFFFFF", "9F1239"),
        Set(false, "Nane", "F3F7F4", "13201B", "3E5349", "D7E3DC", "E8F0EB", "E1EBE5", "C3D5CB", "065F46", "FFFFFF", "9F1239"),
        Set(false, "Lavanta", "F7F6FB", "1E1B2E", "5C5672", "E4E1EF", "F0EEF7", "E9E6F3", "D3CEE3", "6D28D9", "FFFFFF", "9F1239")
    ];

    public static IReadOnlyList<ThemeSet> DarkThemes { get; } =
    [
        Set(true, "Gece", "121418", "F4F6F8", "C1C7D0", "2C323B", "1B1F26", "232830", "3C4450", "2DD4BF", "042F2E", "FDA4AF"),
        Set(true, "Kömür", "161412", "F6F3EF", "CDC4B8", "342F2A", "211C18", "2B2622", "4A433C", "F59E0B", "1C1403", "FECACA"),
        Set(true, "Okyanus", "0E151C", "E8F0F7", "B7C5D4", "243140", "16202A", "1C2936", "334155", "38BDF8", "062033", "FECACA"),
        Set(true, "Orman", "101614", "E7F3EC", "B7CABC", "24312B", "18211C", "1E2B24", "3A4B42", "34D399", "022C22", "FECACA"),
        Set(true, "Mürekkep", "13111A", "F1EDF8", "C9C1D8", "2E2A3A", "1C1926", "262233", "413C52", "C4B5FD", "1E1033", "FECACA")
    ];

    public static IReadOnlyList<ThemeSet> All { get; } = [..Light, ..DarkThemes];

    public static bool Dark { get; private set; }
    public static event Action? Changed;

    public static void Register() => Apply("Kağıt");

    public static ThemeSet Find(string? name) =>
        All.FirstOrDefault(theme => theme.Name == name) ?? Light[0];

    public static string Canonical(string? theme, string? appearance)
    {
        if (All.Any(item => item.Name == theme)) return theme!;
        var dark = string.Equals(appearance, "Karanlık", StringComparison.OrdinalIgnoreCase);
        return theme switch
        {
            "Mavi" or "Lacivert" => dark ? "Okyanus" : "Sis",
            "Yeşil" => dark ? "Orman" : "Nane",
            "Mor" => dark ? "Mürekkep" : "Lavanta",
            "Bordo" => dark ? "Kömür" : "Keten",
            "Grafit" or "Tiffany" => dark ? "Gece" : "Kağıt",
            _ => dark ? "Gece" : "Kağıt"
        };
    }

    public static void Apply(string? name)
    {
        var theme = Find(name);
        Dark = theme.Dark;
        var resources = System.Windows.Application.Current.Resources;
        var soft = theme.Dark ? (byte)0x55 : (byte)0x24;
        Replace(resources, "Paper", theme.Paper);
        Replace(resources, "Ink", theme.Ink);
        Replace(resources, "Muted", theme.Muted);
        Replace(resources, "Line", theme.Line);
        Replace(resources, "Wash", theme.Wash);
        Replace(resources, "Bubble", theme.Bubble);
        Replace(resources, "Field", theme.Field);
        Replace(resources, "Accent", theme.Accent);
        Replace(resources, "AccentSoft", Color.FromArgb(soft, theme.Accent.R, theme.Accent.G, theme.Accent.B));
        Replace(resources, "OnAccent", theme.OnAccent);
        Replace(resources, "Danger", theme.Danger);
        Changed?.Invoke();
    }

    static ThemeSet Set(bool dark, string name, string paper, string ink, string muted, string line, string wash, string bubble, string field, string accent, string onAccent, string danger) =>
        new(name, dark, Hex(paper), Hex(ink), Hex(muted), Hex(line), Hex(wash), Hex(bubble), Hex(field), Hex(accent), Hex(onAccent), Hex(danger));

    static Color Hex(string value)
    {
        var packed = Convert.ToUInt32(value, 16);
        return Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    static void Replace(System.Windows.ResourceDictionary resources, string key, Color color)
    {
        if (resources.Contains(key))
            resources.Remove(key);
        resources.Add(key, new SolidColorBrush(color));
    }
}
