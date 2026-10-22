using System.Diagnostics;
using System.Windows;

namespace OrtakAlan.Views;

public partial class KunyeWindow : ChromeWindow
{
    static KunyeWindow? _open;

    public static void ShowOnce()
    {
        if (_open == null)
        {
            _open = new KunyeWindow();
            _open.Closed += (_, _) => _open = null;
        }

        _open.Show();
        _open.Activate();
    }

    public KunyeWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage, openKunye: false);
        VersionText.Text = AppInfo.Display;
    }

    void Phone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("tel:05552578072") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
