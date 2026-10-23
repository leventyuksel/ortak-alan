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

        _open.ShowUpdate();
        _open.Show();
        _open.Activate();
    }

    public KunyeWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage, openKunye: false);
        VersionText.Text = AppInfo.Display;
        UpdateService.PendingChanged += ShowUpdate;
        Closed += (_, _) => UpdateService.PendingChanged -= ShowUpdate;
        ShowUpdate();
    }

    void ShowUpdate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowUpdate);
            return;
        }

        var offer = UpdateService.Pending;
        UpdateButton.Visibility = offer == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.IsEnabled = offer != null && !UpdateService.Applying;
        if (offer == null) return;
        UpdateCaption.Text = offer.Version.ToString(3);
        UpdateButton.ToolTip = "Sürüm " + offer.Version.ToString(3) + " — kurmak için tıklayın";
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Pending == null || UpdateService.Applying) return;
        UpdateButton.IsEnabled = false;
        UpdateCaption.Text = "İndiriliyor…";
        var error = await UpdateService.ApplyPendingAsync();
        if (error == null)
        {
            App.Me.ExitApp();
            return;
        }

        if (UpdateService.Applying)
        {
            UpdateCaption.Text = "İndiriliyor…";
            return;
        }

        UpdateButton.IsEnabled = true;
        UpdateCaption.Text = error;
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
