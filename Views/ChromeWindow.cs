using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrtakAlan.Views;

public class ChromeWindow : Window
{
    public ChromeWindow()
    {
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "Paper");
        SetResourceReference(ForegroundProperty, "Ink");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Loaded += (_, _) =>
        {
            if (BrandAssets.Icon != null) Icon = BrandAssets.Icon;
        };
    }

    protected void UseLogo(Image image, bool openKunye = true)
    {
        if (BrandAssets.Logo != null)
        {
            image.Source = BrandAssets.Logo;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        }

        if (!openKunye) return;
        image.Cursor = Cursors.Hand;
        image.ToolTip = "Künye";
        image.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            KunyeWindow.ShowOnce();
        };
    }

    bool _dragging;
    double _dragLeft;
    double _dragTop;
    int _dragX;
    int _dragY;

    protected void BeginWindowDrag(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, (area.Width - ActualWidth) / 2);
            Top = Math.Max(area.Top, (area.Height - ActualHeight) / 2);
        }

        if (!GetCursorPos(out var point)) return;
        _dragLeft = Left;
        _dragTop = Top;
        _dragX = point.X;
        _dragY = point.Y;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        if (!GetCursorPos(out var point)) return;
        var (scaleX, scaleY) = DeviceScale();
        Left = _dragLeft + (point.X - _dragX) / scaleX;
        Top = _dragTop + (point.Y - _dragY) / scaleY;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button or Image) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    (double X, double Y) DeviceScale()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return (1, 1);
        var matrix = source.CompositionTarget.TransformToDevice;
        var x = matrix.M11 <= 0 ? 1 : matrix.M11;
        var y = matrix.M22 <= 0 ? 1 : matrix.M22;
        return (x, y);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (WindowStyle != WindowStyle.None) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = 2;
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch
        {
            // Köşe yuvarlama bu sistemde yoksa pencere düz kalır.
        }
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }
}
