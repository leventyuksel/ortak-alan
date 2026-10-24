using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrtakAlan;

static class PrintNotice
{
    static readonly List<NoticeWindow> Open = new();

    public static void Show(string title, string text, bool error)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Show(title, text, error));
            return;
        }

        ExplorerChrome.Read();

        var window = new NoticeWindow(title, text, error);
        Open.Add(window);
        window.ContentRendered += (_, _) => Place();
        window.Closed += (_, _) =>
        {
            Open.Remove(window);
            Place();
        };
        window.Show();
    }

    static void Place()
    {
        var area = SystemParameters.WorkArea;
        var y = area.Bottom - 12;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var window = Open[i];
            var height = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
            y -= height + 8;
            window.Left = area.Right - window.Width - 12;
            window.Top = Math.Max(area.Top + 12, y);
        }
    }

    sealed class NoticeWindow : Window
    {
        readonly DispatcherTimer? _timer;

        public NoticeWindow(string title, string text, bool error)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = 340;
            SizeToContent = SizeToContent.Height;
            Cursor = Cursors.Hand;
            Title = title;

            var mark = error ? Color.FromRgb(220, 38, 38) : Color.FromRgb(10, 186, 181);
            var card = new Border
            {
                Background = new SolidColorBrush(ExplorerChrome.Light ? Color.FromRgb(255, 255, 255) : Color.FromRgb(28, 28, 28)),
                BorderBrush = new SolidColorBrush(ExplorerChrome.Light ? Color.FromRgb(229, 229, 229) : Color.FromRgb(48, 48, 48)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.28,
                    Color = Colors.Black
                }
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                Background = new SolidColorBrush(mark),
                CornerRadius = new CornerRadius(4),
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(bar, 0);
            var body = new StackPanel();
            Grid.SetColumn(body, 2);
            body.Children.Add(new TextBlock
            {
                Text = title.Length == 0 ? "Yazıcı" : title,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ExplorerChrome.Fg),
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 3, 0, 0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = new SolidColorBrush(ExplorerChrome.Fg),
                TextWrapping = TextWrapping.Wrap
            });
            if (error)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Kapatmak için tıklayın",
                    Margin = new Thickness(0, 6, 0, 0),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(ExplorerChrome.Muted)
                });
            }
            row.Children.Add(bar);
            row.Children.Add(body);
            card.Child = row;
            Content = card;
            MouseLeftButtonUp += (_, _) => Close();

            if (!error)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (_, _) => Close();
                _timer.Start();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20);
            var updated = new IntPtr(style.ToInt64() | 0x08000000);
            SetWindowLongPtr(handle, -20, updated);
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            base.OnClosed(e);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    }
}
