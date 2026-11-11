using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace OrtakAlan.Mac;

sealed class ShotWindow : Window
{
    readonly Bitmap _image;
    readonly Page _page;
    readonly Button _pen;
    readonly Button _mark;
    readonly Button _erase;
    readonly List<Mark> _marks = new();
    Mark? _current;
    Tool _tool = Tool.Pen;

    public string? OutputPath { get; private set; }
    public bool SendToChat { get; private set; }

    public ShotWindow(string path)
    {
        Title = "Ekran alıntısı";
        Width = 960;
        Height = 680;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0E1A2B"));
        _image = new Bitmap(path);
        _page = new Page(_image, _marks)
        {
            Width = _image.PixelSize.Width,
            Height = _image.PixelSize.Height,
            Cursor = new Cursor(StandardCursorType.Cross)
        };
        _page.PointerPressed += Pressed;
        _page.PointerMoved += Moved;
        _page.PointerReleased += Released;

        _pen = ToolButton("Kalem");
        _mark = ToolButton("Fosforlu");
        _erase = ToolButton("Silgi");
        _pen.Click += (_, _) => SetTool(Tool.Pen);
        _mark.Click += (_, _) => SetTool(Tool.Mark);
        _erase.Click += (_, _) => SetTool(Tool.Erase);
        var undo = ToolButton("Geri al");
        undo.Click += (_, _) =>
        {
            if (_marks.Count == 0) return;
            _marks.RemoveAt(_marks.Count - 1);
            _page.InvalidateVisual();
        };
        var save = ToolButton("Kaydet");
        save.Click += async (_, _) => await Save();
        var send = new Button
        {
            Content = "Sohbete gönder",
            Background = new SolidColorBrush(Color.Parse("#0F766E")),
            Foreground = Brushes.White,
            Padding = new Thickness(12, 8)
        };
        send.Click += (_, _) => Send();

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 10)
        };
        tools.Children.Add(_pen);
        tools.Children.Add(_mark);
        tools.Children.Add(_erase);
        tools.Children.Add(undo);
        tools.Children.Add(save);
        tools.Children.Add(send);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.Parse("#09101A")),
            Content = _page
        };
        var dock = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);
        dock.Children.Add(scroll);
        Content = dock;
        SetTool(Tool.Pen);
        Closed += (_, _) => _image.Dispose();
    }

    void SetTool(Tool tool)
    {
        _tool = tool;
        Paint(_pen, tool == Tool.Pen);
        Paint(_mark, tool == Tool.Mark);
        Paint(_erase, tool == Tool.Erase);
        _page.Cursor = new Cursor(tool == Tool.Erase ? StandardCursorType.Hand : StandardCursorType.Cross);
    }

    static void Paint(Button button, bool on)
    {
        button.Background = new SolidColorBrush(Color.Parse(on ? "#0F766E" : "#16324A"));
    }

    static Button ToolButton(string text) => new()
    {
        Content = text,
        Foreground = Brushes.White,
        Padding = new Thickness(12, 8)
    };

    void Pressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(_page);
        if (_tool == Tool.Erase)
        {
            Erase(point);
            return;
        }
        _current = new Mark
        {
            Color = _tool == Tool.Mark ? Color.FromArgb(110, 250, 204, 21) : Colors.Red,
            Thickness = _tool == Tool.Mark ? 18 : 3
        };
        _current.Points.Add(point);
        _marks.Add(_current);
        e.Pointer.Capture(_page);
        _page.InvalidateVisual();
    }

    void Moved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(_page);
        if (_tool == Tool.Erase && e.GetCurrentPoint(_page).Properties.IsLeftButtonPressed)
        {
            Erase(point);
            return;
        }
        if (_current == null) return;
        _current.Points.Add(point);
        _page.InvalidateVisual();
    }

    void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_current != null && _current.Points.Count == 1)
            _current.Points.Add(_current.Points[0]);
        _current = null;
        e.Pointer.Capture(null);
    }

    void Erase(Point point)
    {
        for (var i = _marks.Count - 1; i >= 0; i--)
        {
            if (!Hits(_marks[i], point)) continue;
            _marks.RemoveAt(i);
            _page.InvalidateVisual();
            return;
        }
    }

    static bool Hits(Mark mark, Point point)
    {
        var reach = Math.Max(10, mark.Thickness / 2 + 4);
        for (var i = 0; i < mark.Points.Count; i++)
        {
            if (Distance(mark.Points[i], point) <= reach) return true;
            if (i == 0) continue;
            if (SegmentDistance(mark.Points[i - 1], mark.Points[i], point) <= reach) return true;
        }
        return false;
    }

    static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double SegmentDistance(Point a, Point b, Point p)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        if (length <= 0.01) return Distance(a, p);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        return Distance(new Point(a.X + t * dx, a.Y + t * dy), p);
    }

    async Task Save()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Ekran alıntısını kaydet",
            SuggestedFileName = "ekran-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG görseli") { Patterns = new[] { "*.png" } }
            }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        Write(path);
        OutputPath = path;
        SendToChat = false;
        Close();
    }

    void Send()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OrtakAlan");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ekran-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
        Write(path);
        OutputPath = path;
        SendToChat = true;
        Close();
    }

    void Write(string path)
    {
        var pixels = _image.PixelSize;
        var size = new Size(pixels.Width, pixels.Height);
        _page.Measure(size);
        _page.Arrange(new Rect(size));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
        bitmap.Render(_page);
        bitmap.Save(path);
    }

    enum Tool { Pen, Mark, Erase }

    sealed class Mark
    {
        public List<Point> Points { get; } = new();
        public Color Color { get; set; }
        public double Thickness { get; set; }
    }

    sealed class Page : Control
    {
        readonly Bitmap _image;
        readonly List<Mark> _marks;

        public Page(Bitmap image, List<Mark> marks)
        {
            _image = image;
            _marks = marks;
        }

        public override void Render(DrawingContext context)
        {
            context.DrawImage(_image, new Rect(0, 0, _image.PixelSize.Width, _image.PixelSize.Height));
            foreach (var mark in _marks)
            {
                if (mark.Points.Count == 0) continue;
                var brush = new SolidColorBrush(mark.Color);
                var pen = new Pen(brush, mark.Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                if (mark.Points.Count == 1)
                {
                    context.DrawEllipse(brush, null, mark.Points[0], mark.Thickness / 2, mark.Thickness / 2);
                    continue;
                }
                var geometry = new StreamGeometry();
                using (var draw = geometry.Open())
                {
                    draw.BeginFigure(mark.Points[0], false);
                    for (var i = 1; i < mark.Points.Count; i++)
                        draw.LineTo(mark.Points[i]);
                    draw.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }
}
