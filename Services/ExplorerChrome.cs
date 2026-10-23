using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OrtakAlan;

static class ExplorerChrome
{
    public static bool Light { get; private set; } = true;

    public static void Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            Light = key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            Light = true;
        }
    }

    public static Color Bg => Light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x19, 0x19, 0x19);
    public static Color Fg => Light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static Color Muted => Light ? Color.FromRgb(0x5C, 0x5C, 0x5C) : Color.FromRgb(0xC6, 0xC6, 0xC6);
    public static Color Address => Light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x2D, 0x2D, 0x2D);
    public static Color Hover => Light ? Color.FromRgb(0xF5, 0xF5, 0xF5) : Color.FromRgb(0x2E, 0x2E, 0x2E);
    public static Color Select => Light ? Color.FromRgb(0xCC, 0xE4, 0xF7) : Color.FromRgb(0x3A, 0x3A, 0x3A);
    public static Color Line => Light ? Color.FromRgb(0xE5, 0xE5, 0xE5) : Color.FromRgb(0x2A, 0x2A, 0x2A);
}

static class FolderArt
{
    public static readonly Color Tiffany = Color.FromRgb(0x0A, 0xBA, 0xB5);
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource Create(Color color)
    {
        var key = color.ToString();
        if (Cache.TryGetValue(key, out var found)) return found;
        var lighter = Color.FromRgb(
            (byte)Math.Min(255, color.R + 32),
            (byte)Math.Min(255, color.G + 32),
            (byte)Math.Min(255, color.B + 32));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Brush(lighter),
            null,
            new RectangleGeometry(new Rect(1, 8, 18, 8), 2, 2)));
        group.Children.Add(new GeometryDrawing(
            Brush(color),
            new Pen(Brush(Color.FromArgb(50, 0, 0, 0)), 0.6),
            new RectangleGeometry(new Rect(1, 13, 38, 24), 2.5, 2.5)));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        Cache[key] = image;
        return image;
    }

    static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
