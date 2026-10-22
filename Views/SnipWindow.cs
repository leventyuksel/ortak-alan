using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OrtakAlan.Views;

public class SnipWindow : Window
{
    readonly Rectangle _selection = new()
    {
        Stroke = Brushes.White,
        StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    Point _start;
    bool _dragging;
    public BitmapSource? Image { get; private set; }

    public SnipWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var canvas = new Canvas();
        canvas.Children.Add(_selection);
        Content = canvas;
        MouseLeftButtonDown += Begin;
        MouseMove += Move;
        MouseLeftButtonUp += End;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                DialogResult = false;
        };
    }

    void Begin(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        _selection.Visibility = Visibility.Visible;
        Place(_start);
        CaptureMouse();
    }

    void Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Place(e.GetPosition(this));
    }

    void End(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        var rect = CurrentRect(e.GetPosition(this));
        if (rect.Width < 8 || rect.Height < 8)
        {
            DialogResult = false;
            return;
        }

        Hide();
        System.Threading.Thread.Sleep(60);
        Image = Grab(rect);
        DialogResult = Image != null;
    }

    void Place(Point current)
    {
        var rect = CurrentRect(current);
        Canvas.SetLeft(_selection, rect.X);
        Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width;
        _selection.Height = rect.Height;
    }

    Rect CurrentRect(Point current)
    {
        var x = Math.Min(_start.X, current.X);
        var y = Math.Min(_start.Y, current.Y);
        return new Rect(x, y, Math.Abs(current.X - _start.X), Math.Abs(current.Y - _start.Y));
    }

    BitmapSource? Grab(Rect dipRect)
    {
        try
        {
            var topLeft = PointToScreen(new Point(dipRect.X, dipRect.Y));
            var bottomRight = PointToScreen(new Point(dipRect.Right, dipRect.Bottom));
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var matrix = source.CompositionTarget.TransformToDevice;
                topLeft = matrix.Transform(topLeft);
                bottomRight = matrix.Transform(bottomRight);
            }

            var x = (int)Math.Floor(topLeft.X);
            var y = (int)Math.Floor(topLeft.Y);
            var width = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X));
            var height = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            return ToSource(bitmap);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
    }

    static BitmapSource ToSource(System.Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);
}
