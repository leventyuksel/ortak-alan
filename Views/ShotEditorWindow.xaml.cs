using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrtakAlan.Views;

public partial class ShotEditorWindow : ChromeWindow
{
    readonly BitmapSource _image;
    public string? OutputPath { get; private set; }
    public bool SendToChat { get; private set; }

    public ShotEditorWindow(BitmapSource image)
    {
        InitializeComponent();
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        _image = image;
        Page.Width = image.PixelWidth;
        Page.Height = image.PixelHeight;
        Page.Background = new ImageBrush(image) { Stretch = Stretch.Fill };
        UsePen();
    }

    void Pen_Click(object sender, RoutedEventArgs e) => UsePen();

    void Mark_Click(object sender, RoutedEventArgs e)
    {
        Page.EditingMode = InkCanvasEditingMode.Ink;
        Page.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromArgb(110, 250, 204, 21),
            Width = 18,
            Height = 18,
            IsHighlighter = true,
            IgnorePressure = true
        };
    }

    void Erase_Click(object sender, RoutedEventArgs e) => Page.EditingMode = InkCanvasEditingMode.EraseByStroke;

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (Page.Strokes.Count > 0)
            Page.Strokes.RemoveAt(Page.Strokes.Count - 1);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Ekran alıntısını kaydet",
            Filter = "PNG görseli|*.png",
            FileName = "ekran-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png"
        };
        if (dialog.ShowDialog(this) != true) return;
        Write(dialog.FileName);
        OutputPath = dialog.FileName;
        SendToChat = false;
        DialogResult = true;
        Close();
    }

    void Send_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OrtakAlan");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ekran-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
        Write(path);
        OutputPath = path;
        SendToChat = true;
        DialogResult = true;
        Close();
    }

    void UsePen()
    {
        Page.EditingMode = InkCanvasEditingMode.Ink;
        Page.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Red,
            Width = 3,
            Height = 3,
            FitToCurve = true
        };
    }

    void Write(string path)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(_image, new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
            Page.Strokes.Draw(context);
        }

        var bitmap = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
