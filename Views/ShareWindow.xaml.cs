using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace OrtakAlan.Views;

public partial class ShareWindow : ChromeWindow
{
    public ShareWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        Title = "Ortak Alan — " + App.Me.Settings.ComputerName;
        LocalNameText.Text = App.Me.Settings.ComputerName;
        NetList.ItemsSource = App.Me.Directory.Peers;
        App.Me.Directory.Peers.CollectionChanged += (_, _) => UpdateEmpty();
        ThemeService.Changed += () => SelectTab(MinePanel.Visibility == Visibility.Visible);
        SelectTab(true);
        Reload();
    }

    public void Reload()
    {
        MineList.ItemsSource = App.Me.Store.CopyShares().Select(share => new ShareVm
        {
            Id = share.Id,
            Kind = share.Kind,
            Name = share.Name,
            Path = share.Path,
            Line = share.Kind == "printer" ? "Yazıcı · Açık" : "Klasör · Açık",
            Glyph = share.Kind == "printer" ? "\uE749" : "\uE8B7",
            Warning = string.IsNullOrEmpty(share.WindowsError) ? "" : share.WindowsError,
            Tip = share.Path
        }).ToList();
        UpdateEmpty();
    }

    public void ShowAtCorner()
    {
        if (!IsVisible) Show();
        Dispatcher.BeginInvoke(() =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = Math.Max(area.Top + 16, area.Bottom - ActualHeight - 16);
            Activate();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void UpdateEmpty()
    {
        EmptyMine.Visibility = MineList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyNet.Visibility = App.Me.Directory.Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SelectTab(bool mine)
    {
        MinePanel.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        NetPanel.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
        ShareActions.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        var soft = (System.Windows.Media.Brush)FindResource("AccentSoft");
        TabMine.Background = mine ? soft : System.Windows.Media.Brushes.Transparent;
        TabNet.Background = mine ? System.Windows.Media.Brushes.Transparent : soft;
        TabMine.FontWeight = mine ? FontWeights.SemiBold : FontWeights.Normal;
        TabNet.FontWeight = mine ? FontWeights.Normal : FontWeights.SemiBold;
    }

    void TabMine_Click(object sender, RoutedEventArgs e) => SelectTab(true);
    void TabNet_Click(object sender, RoutedEventArgs e) => SelectTab(false);

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Paylaşılacak klasör" };
        if (dialog.ShowDialog(this) != true) return;
        StatusText.Text = App.Me.AddFolder(dialog.FolderName);
        Reload();
    }

    void Printer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PickPrinterWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        StatusText.Text = App.Me.AddPrinter(dialog.SelectedPrinter);
        Reload();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ShareVm share) return;
        var answer = MessageBox.Show(this, $"\"{share.Name}\" paylaşımdan kaldırılsın mı?", "Ortak Alan", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        App.Me.RemoveShare(share.Id);
        Reload();
        StatusText.Text = "Paylaşım kaldırıldı.";
    }

    void OpenLocal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ShareVm share) return;
        if (share.Kind == "folder" && Directory.Exists(share.Path))
            Process.Start("explorer.exe", share.Path);
        else if (share.Kind == "printer")
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}") { UseShellExecute = true });
    }

    void Message_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is PeerVm peer)
            App.Me.ShowChat(peer.Id);
    }

    async void RemoteShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not RemoteShareVm share) return;
        var peer = App.Me.Directory.Peers.FirstOrDefault(p => p.Id == share.PeerId);
        if (peer == null) return;
        if (!peer.Online)
        {
            StatusText.Text = "Çevrimdışı";
            return;
        }

        if (share.Kind == "folder")
        {
            StatusText.Text = ShareConnect.OpenFolder(peer, share, true);
            return;
        }

        StatusText.Text = "Yazıcıya bağlanılıyor…";
        StatusText.Text = await ShareConnect.ConnectPrinter(peer, share);
    }

    void Chat_Click(object sender, RoutedEventArgs e) => App.Me.ShowChat();
    void Theme_Click(object sender, RoutedEventArgs e) => App.Me.ShowTheme();
    void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Me.Exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
