using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingImage = System.Drawing.Image;

namespace OrtakAlan;

static class BrandAssets
{
    public static ImageSource? Logo { get; private set; }
    public static ImageSource? Icon { get; private set; }
    public static DrawingImage? TrayImage { get; private set; }

    public static void Init()
    {
        try { Logo = LoadLogo(); }
        catch (Exception ex) { Log.Write(ex); }
        try { LoadAppIcon(); }
        catch (Exception ex) { Log.Write(ex); }
        Icon ??= MakeIcon();
    }

    static void LoadAppIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ikon.png", UriKind.Absolute));
        if (info == null) return;
        using var copy = new MemoryStream();
        info.Stream.CopyTo(copy);
        var bytes = copy.ToArray();

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        Icon = image;

        using var raw = new System.Drawing.Bitmap(new MemoryStream(bytes));
        var sized = new System.Drawing.Bitmap(256, 256);
        using (var graphics = System.Drawing.Graphics.FromImage(sized))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.DrawImage(raw, 0, 0, 256, 256);
        }
        TrayImage = sized;
    }

    public static Stream PngIcon()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)Icon!));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    static BitmapSource LoadLogo()
    {
        var uri = new Uri("pack://application:,,,/Assets/logo.png", UriKind.Absolute);
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var minX = width;
        var minY = height;
        var maxX = 0;
        var maxY = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i + 2] > 245 && pixels[i + 1] > 245 && pixels[i] > 245)
                    pixels[i + 3] = 0;
                if (pixels[i + 3] > 10)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        bitmap.Freeze();
        if (maxX <= minX || maxY <= minY) return bitmap;

        var cropped = new CroppedBitmap(bitmap, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        cropped.Freeze();
        return cropped;
    }

    static BitmapSource MakeIcon()
    {
        const int size = 64;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x4B)),
                null,
                new Rect(1, 1, 62, 62),
                16,
                16);
            dc.DrawEllipse(Brushes.White, null, new Point(23, 32), 6.5, 6.5);
            dc.DrawEllipse(Brushes.White, null, new Point(43, 32), 6.5, 6.5);
            var pen = new Pen(Brushes.White, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(29, 32), new Point(37, 32));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
