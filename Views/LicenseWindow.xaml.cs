using System.Windows;
using System.Windows.Input;

namespace OrtakAlan.Views;

public partial class LicenseWindow : ChromeWindow
{
    static int _gate;
    static LicenseWindow? _open;

    public static void ShowTrial() => Show(LicenseService.TrialText);

    public static void ShowSeat() => Show(LicenseService.BlockText);

    public static void Show(string text)
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Show(text));
            return;
        }

        if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
        {
            _open?.Activate();
            return;
        }

        var window = new LicenseWindow(text);
        _open = window;
        window.Closed += (_, _) =>
        {
            _open = null;
            _gate = 0;
        };
        window.Show();
        window.Activate();
    }

    LicenseWindow(string text)
    {
        InitializeComponent();
        UseLogo(LogoImage, openKunye: false);
        BodyText.Text = text;
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        KunyeWindow.ShowOnce();
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
