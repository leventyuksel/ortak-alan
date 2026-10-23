using System.Windows.Media;

namespace OrtakAlan;

public sealed record ThemeOption(string Name, Color Color);

public sealed record TextColorOption(string Name, Color? Color);

public static class ThemeService
{
    public static readonly Color Tiffany = Color.FromRgb(0x0A, 0xBA, 0xB5);
    static readonly Color DarkPaper = Color.FromRgb(0x14, 0x16, 0x1C);
    static readonly Color OnLight = Color.FromRgb(0x08, 0x20, 0x1E);

    public static readonly IReadOnlyList<ThemeOption> All =
    [
        new("Tiffany", Tiffany),
        new("Mavi", Color.FromRgb(0x3B, 0x82, 0xF6)),
        new("Lacivert", Color.FromRgb(0x60, 0x7A, 0xF0)),
        new("Yeşil", Color.FromRgb(0x10, 0xB9, 0x81)),
        new("Mor", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        new("Bordo", Color.FromRgb(0xFB, 0x71, 0x85)),
        new("Grafit", Color.FromRgb(0xCB, 0xD5, 0xE1))
    ];

    public static readonly IReadOnlyList<TextColorOption> TextColors =
    [
        new("Otomatik", null),
        new("Beyaz", Color.FromRgb(0xF9, 0xFA, 0xFB)),
        new("Açık gri", Color.FromRgb(0xD1, 0xD5, 0xDB)),
        new("Koyu", Color.FromRgb(0x11, 0x18, 0x27)),
        new("Lacivert", Color.FromRgb(0x1E, 0x3A, 0x8A)),
        new("Mavi", Color.FromRgb(0x25, 0x63, 0xEB))
    ];

    public static bool Dark { get; private set; }
    public static event Action? Changed;

    public static void Register()
    {
        Apply("Tiffany");
        ApplyAppearance("Aydınlık");
    }

    public static void Apply(string? name)
    {
        var theme = All.FirstOrDefault(t => t.Name == name) ?? All[0];
        var accent = Tune(theme.Color, Dark);
        var resources = System.Windows.Application.Current.Resources;
        var soft = Dark ? (byte)0x66 : (byte)0x28;
        Replace(resources, "Accent", accent);
        Replace(resources, "AccentSoft", Color.FromArgb(soft, accent.R, accent.G, accent.B));
        Replace(resources, "OnAccent", On(accent));
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
            Replace(resources, "Paper", DarkPaper);
            Replace(resources, "Ink", Color.FromRgb(0xF3, 0xF4, 0xF6));
            Replace(resources, "Muted", Color.FromRgb(0xD1, 0xD5, 0xDB));
            Replace(resources, "Line", Color.FromRgb(0x3A, 0x41, 0x4E));
            Replace(resources, "Wash", Color.FromRgb(0x1C, 0x24, 0x2C));
            Replace(resources, "Bubble", Color.FromRgb(0x26, 0x32, 0x38));
            Replace(resources, "Field", Color.FromRgb(0x4B, 0x55, 0x63));
        }

        ApplyText(null);
        Changed?.Invoke();
    }

    public static void ApplyText(string? name)
    {
        var choice = TextColors.FirstOrDefault(t => t.Name == name);
        if (choice?.Color == null) return;
        var color = choice.Color.Value;
        var paper = Dark ? DarkPaper : Colors.White;
        if (Contrast(color, paper) < 4.5)
            color = Dark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x11, 0x18, 0x27);
        Replace(System.Windows.Application.Current.Resources, "Ink", color);
    }

    public static void ApplySaved(string? theme, string? appearance, string? textColor)
    {
        ApplyAppearance(appearance);
        ApplyText(textColor);
        Apply(theme);
    }

    public static Color On(Color background) =>
        Contrast(Colors.White, background) >= 4.5 ? Colors.White : OnLight;

    public static Color Tune(Color color, bool onDark)
    {
        var backdrop = onDark ? DarkPaper : Colors.White;
        if (Contrast(color, backdrop) >= 4.5) return color;
        ToHsl(color, out var h, out var s, out var l);
        if (s < 0.2) s = 0.2;
        var lo = onDark ? l : 0.12;
        var hi = onDark ? 0.86 : l;
        for (var i = 0; i < 16; i++)
        {
            var mid = (lo + hi) / 2;
            var ok = Contrast(FromHsl(h, s, mid), backdrop) >= 4.5;
            if (onDark)
            {
                if (ok) hi = mid;
                else lo = mid;
            }
            else if (ok) lo = mid;
            else hi = mid;
        }
        return FromHsl(h, s, onDark ? hi : lo);
    }

    static void Replace(System.Windows.ResourceDictionary resources, string key, Color color)
    {
        if (resources.Contains(key))
            resources.Remove(key);
        resources.Add(key, new SolidColorBrush(color));
    }

    static double Contrast(Color a, Color b)
    {
        var hi = Math.Max(Lum(a), Lum(b));
        var lo = Math.Min(Lum(a), Lum(b));
        return (hi + 0.05) / (lo + 0.05);
    }

    static double Lum(Color color) =>
        0.2126 * Lin(color.R) + 0.7152 * Lin(color.G) + 0.0722 * Lin(color.B);

    static double Lin(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    static void ToHsl(Color color, out double h, out double s, out double l)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        var d = max - min;
        if (d < 0.0001)
        {
            h = 0;
            s = 0;
            return;
        }
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (Math.Abs(max - r) < 0.0001) h = (g - b) / d + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 0.0001) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    static Color FromHsl(double h, double s, double l)
    {
        double r, g, b;
        if (s <= 0)
            r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return Color.FromRgb(Byte(r), Byte(g), Byte(b));
    }

    static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
