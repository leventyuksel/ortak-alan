using System.Windows.Media;

namespace OrtakAlan;

public sealed record ThemeOption(string Name, Color Color);

public sealed record TextColorOption(string Name, Color? Color);

public static class ThemeService
{
    public static readonly IReadOnlyList<ThemeOption> All =
    [
        new("Lacivert", Color.FromRgb(0x0B, 0x1F, 0x4B)),
        new("Mavi", Color.FromRgb(0x25, 0x63, 0xEB)),
        new("Yeşil", Color.FromRgb(0x0F, 0x76, 0x6E)),
        new("Mor", Color.FromRgb(0x6D, 0x28, 0xD9)),
        new("Bordo", Color.FromRgb(0x9F, 0x12, 0x39)),
        new("Grafit", Color.FromRgb(0x33, 0x41, 0x55))
    ];

    public static readonly IReadOnlyList<TextColorOption> TextColors =
    [
        new("Otomatik", null),
        new("Beyaz", Color.FromRgb(0xF9, 0xFA, 0xFB)),
        new("Açık gri", Color.FromRgb(0xD1, 0xD5, 0xDB)),
        new("Koyu", Color.FromRgb(0x11, 0x18, 0x27)),
        new("Lacivert", Color.FromRgb(0x0B, 0x1F, 0x4B)),
        new("Mavi", Color.FromRgb(0x25, 0x63, 0xEB))
    ];

    public static bool Dark { get; private set; }
    public static event Action? Changed;

    public static void Register()
    {
        Apply("Lacivert");
        ApplyAppearance("Aydınlık");
    }

    public static void Apply(string? name)
    {
        var theme = All.FirstOrDefault(t => t.Name == name) ?? All[0];
        var resources = System.Windows.Application.Current.Resources;
        var soft = Dark ? (byte)0x38 : (byte)0x1F;
        Replace(resources, "Accent", theme.Color);
        Replace(resources, "AccentSoft", Color.FromArgb(soft, theme.Color.R, theme.Color.G, theme.Color.B));
        Changed?.Invoke();
    }

    public static void ApplyAppearance(string? name)
    {
        Dark = string.Equals(name, "Karanlık", StringComparison.OrdinalIgnoreCase);
        var resources = System.Windows.Application.Current.Resources;
        if (!Dark)
        {
            Replace(resources, "Paper", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Replace(resources, "Ink", Color.FromRgb(0x11, 0x18, 0x27));
            Replace(resources, "Muted", Color.FromRgb(0x6B, 0x72, 0x80));
            Replace(resources, "Line", Color.FromRgb(0xE6, 0xE8, 0xEE));
            Replace(resources, "Wash", Color.FromRgb(0xF7, 0xF8, 0xFA));
            Replace(resources, "Bubble", Color.FromRgb(0xF3, 0xF4, 0xF6));
            Replace(resources, "Field", Color.FromRgb(0xD1, 0xD5, 0xDB));
        }
        else
        {
            Replace(resources, "Paper", Color.FromRgb(0x14, 0x16, 0x1C));
            Replace(resources, "Ink", Color.FromRgb(0xF3, 0xF4, 0xF6));
            Replace(resources, "Muted", Color.FromRgb(0x9C, 0xA3, 0xAF));
            Replace(resources, "Line", Color.FromRgb(0x2C, 0x31, 0x3C));
            Replace(resources, "Wash", Color.FromRgb(0x1C, 0x1F, 0x27));
            Replace(resources, "Bubble", Color.FromRgb(0x26, 0x2A, 0x34));
            Replace(resources, "Field", Color.FromRgb(0x3A, 0x40, 0x4C));
        }

        ApplyText(null);
        Changed?.Invoke();
    }

    public static void ApplyText(string? name)
    {
        var choice = TextColors.FirstOrDefault(t => t.Name == name);
        if (choice?.Color == null) return;
        Replace(System.Windows.Application.Current.Resources, "Ink", choice.Color.Value);
    }

    public static void ApplySaved(string? theme, string? appearance, string? textColor)
    {
        ApplyAppearance(appearance);
        ApplyText(textColor);
        Apply(theme);
    }

    static void Replace(System.Windows.ResourceDictionary resources, string key, Color color)
    {
        if (resources.Contains(key))
            resources.Remove(key);
        resources.Add(key, new SolidColorBrush(color));
    }
}
