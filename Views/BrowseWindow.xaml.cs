using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace OrtakAlan.Views;

public partial class BrowseWindow : ChromeWindow
{
    readonly string _peerId;
    readonly string _shareId;
    readonly string? _windowsShare;
    readonly string? _ip;
    string _rel = "";

    public BrowseWindow(string peerId, string peerName, string shareId, string shareName, string? windowsShare, string? ip)
    {
        InitializeComponent();
        _peerId = peerId;
        _shareId = shareId;
        _windowsShare = windowsShare;
        _ip = ip;
        TitleText.Text = shareName;
        SubText.Text = peerName;
        Title = shareName;
        if (!string.IsNullOrEmpty(windowsShare) && !string.IsNullOrEmpty(ip))
            ExplorerButton.Visibility = Visibility.Visible;
        Loaded += async (_, _) => await LoadDir();
    }

    async Task LoadDir()
    {
        StatusText.Text = "İzin bekleniyor…";
        var (error, entries) = await App.Me.Node!.ListAsync(_peerId, _shareId, _rel);
        if (error != null)
        {
            FileList.ItemsSource = null;
            StatusText.Text = error;
            return;
        }

        FileList.ItemsSource = entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name).ToList();
        StatusText.Text = string.IsNullOrEmpty(_rel) ? "Kök klasör" : _rel;
    }

    async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not DirEntry entry) return;
        if (entry.IsDir)
        {
            _rel = Join(_rel, entry.Name);
            await LoadDir();
        }
        else await DownloadAndOpen(entry);
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not DirEntry entry || entry.IsDir) return;
        await DownloadAndOpen(entry);
    }

    async Task DownloadAndOpen(DirEntry entry)
    {
        StatusText.Text = "İndiriliyor…";
        var rel = Join(_rel, entry.Name);
        var (error, path) = await App.Me.Node!.DownloadAsync(_peerId, _shareId, rel, entry.Name);
        if (error != null || string.IsNullOrEmpty(path))
        {
            StatusText.Text = error ?? "Dosya alınamadı";
            return;
        }

        StatusText.Text = "İndirildi";
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_rel.Length == 0) return;
        var slash = _rel.LastIndexOf('/');
        _rel = slash < 0 ? "" : _rel[..slash];
        await LoadDir();
    }

    void Explorer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_windowsShare) || string.IsNullOrEmpty(_ip)) return;
        Process.Start("explorer.exe", $@"\\{_ip}\{_windowsShare}");
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    static string Join(string rel, string name) => string.IsNullOrEmpty(rel) ? name : rel + "/" + name;
}
