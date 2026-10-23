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

        if (UpdateService.Applying)
        {
            UpdateButton.IsEnabled = false;
            UpdateCaption.Text = "İndiriliyor…";
            Note("");
            return;
        }

        var offer = UpdateService.Pending;
        UpdateButton.IsEnabled = true;
        if (offer != null)
        {
            UpdateCaption.Text = "Sürüm " + offer.Version.ToString(3) + " kur";
            UpdateButton.ToolTip = "Kurulumu başlat";
            Note("");
            return;
        }

        UpdateCaption.Text = "Güncellemeleri kontrol et";
        UpdateButton.ToolTip = "GitHub sürümüne bak";
        if (UpdateService.LastCheckFailed) Note("Kontrol edilemedi. Yeniden deneyin.");
        else if (UpdateService.Checked) Note("Bu sürüm güncel.");
        else Note("");
    }

    void Note(string text)
    {
        UpdateNote.Text = text;
        UpdateNote.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Applying) return;
        if (UpdateService.Pending == null)
        {
            UpdateButton.IsEnabled = false;
            UpdateCaption.Text = "Kontrol ediliyor…";
            Note("");
            await UpdateService.LookAsync();
            ShowUpdate();
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateCaption.Text = "İndiriliyor…";
        Note("");
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
        UpdateCaption.Text = "Sürüm " + UpdateService.Pending?.Version.ToString(3) + " kur";
        Note(error);
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
