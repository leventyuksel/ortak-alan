using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OrtakAlan.Views;

public partial class ChatWindow : ChromeWindow
{
    ObservableCollection<ChatLine>? _lines;
    bool _busy;
    UpdateOffer? _offer;
    public string? SelectedId { get; private set; }
    readonly List<SwatchVm> _swatches = new();

    public ChatWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        DockRight();
        SelfName.Text = App.Me.Settings.ComputerName;
        VersionText.Text = AppInfo.Display;
        Loaded += async (_, _) => await LookForUpdate();
        PeerList.ItemsSource = App.Me.Directory.Peers;
        Title = "Ortak Alan — " + App.Me.Settings.ComputerName;
        App.Me.Directory.Changed += RefreshPeers;
        foreach (var theme in ThemeService.All)
        {
            _swatches.Add(new SwatchVm
            {
                Name = theme.Name,
                Color = new SolidColorBrush(theme.Color),
                Selected = theme.Name == App.Me.Settings.Theme
            });
        }
        TemaSwatches.ItemsSource = _swatches;
        MarkAppearance();
        RefreshPeers();
    }

    void DockRight()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1000, Math.Max(760, area.Width - 48));
        Height = Math.Max(MinHeight, area.Height - 16);
        Left = area.Right - Width - 8;
        Top = area.Top + 8;
    }

    public void SelectPeer(string id)
    {
        Show();
        var peer = App.Me.Directory.Peers.FirstOrDefault(p => p.Id == id);
        if (peer != null) PeerList.SelectedItem = peer;
        else Bind(id, "Sohbet");
        Activate();
    }

    void RefreshPeers()
    {
        OnlineText.Text = App.Me.Directory.OnlineCount + " çevrimiçi";
        EmptyPeers.Visibility = App.Me.Directory.Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedId == null) return;
        var peer = App.Me.Directory.Peers.FirstOrDefault(p => p.Id == SelectedId);
        if (peer != null) PeerTitle.Text = peer.Name;
    }

    async void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeerList.SelectedItem is not PeerVm peer) return;
        Bind(peer.Id, peer.Name);
        await RequestApproval(peer);
    }

    async Task RequestApproval(PeerVm peer)
    {
        if (!peer.Online)
        {
            StatusText.Text = "Çevrimdışı. Onay isteği gönderilemedi.";
            return;
        }

        var already = App.Me.Store.IsApproved(peer.Id);
        StatusText.Text = already
            ? "Bağlanılıyor…"
            : "Onay isteği gönderildi. Karşı bilgisayar cevaplıyor…";
        var error = await App.Me.Node.ConnectAsync(peer.Id);
        if (SelectedId != peer.Id) return;
        StatusText.Text = error ?? (already ? "Bağlandı." : "Bağlantı onaylandı.");
    }

    void PeerList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is not ListBoxItem item || item.DataContext is not PeerVm peer) return;

        PeerList.SelectedItem = peer;
        var menu = new ContextMenu { PlacementTarget = item, Placement = PlacementMode.MousePoint };
        menu.Items.Add(ShareMenu("Paylaşılan klasöre bağlan", peer, "folder"));
        menu.Items.Add(ShareMenu("Paylaşılan yazıcıya bağlan", peer, "printer"));
        menu.IsOpen = true;
        e.Handled = true;
    }

    MenuItem ShareMenu(string title, PeerVm peer, string kind)
    {
        var shares = peer.Shares.Where(s => s.Kind == kind).ToList();
        var item = new MenuItem { Header = title, IsEnabled = shares.Count > 0 };
        if (shares.Count == 1)
        {
            var only = shares[0];
            item.Click += async (_, _) => await ConnectShare(peer, only);
        }
        else
        {
            foreach (var share in shares)
            {
                var captured = share;
                var child = new MenuItem { Header = share.Name };
                child.Click += async (_, _) => await ConnectShare(peer, captured);
                item.Items.Add(child);
            }
        }
        return item;
    }

    async Task ConnectShare(PeerVm peer, RemoteShareVm share)
    {
        if (!peer.Online)
        {
            StatusText.Text = "Çevrimdışı";
            return;
        }

        if (share.Kind == "folder")
            StatusText.Text = ShareConnect.OpenFolder(peer, share, true);
        else
        {
            StatusText.Text = "Yazıcıya bağlanılıyor…";
            StatusText.Text = await ShareConnect.ConnectPrinter(peer, share);
        }
    }

    void Tema_Click(object sender, RoutedEventArgs e)
    {
        MarkAppearance();
        TemaPopup.IsOpen = true;
    }

    void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string name) return;
        App.Me.Settings.Appearance = name;
        App.Me.Store.Save();
        ThemeService.ApplyAppearance(name);
        ThemeService.ApplyText(App.Me.Settings.TextColor);
        ThemeService.Apply(App.Me.Settings.Theme);
        MarkAppearance();
    }

    void MarkAppearance()
    {
        var dark = App.Me.Settings.Appearance == "Karanlık";
        var soft = (Brush)FindResource("AccentSoft");
        LightButton.Background = dark ? Brushes.Transparent : soft;
        DarkButton.Background = dark ? soft : Brushes.Transparent;
        LightButton.FontWeight = dark ? FontWeights.Normal : FontWeights.SemiBold;
        DarkButton.FontWeight = dark ? FontWeights.SemiBold : FontWeights.Normal;
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Paylaşılacak klasör" };
        if (dialog.ShowDialog(this) != true) return;
        StatusText.Text = App.Me.AddFolder(dialog.FolderName);
    }

    void Printer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PickPrinterWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        StatusText.Text = App.Me.AddPrinter(dialog.SelectedPrinter);
    }

    void Shares_Click(object sender, RoutedEventArgs e) => App.Me.ShowShare();

    void Settings_Click(object sender, RoutedEventArgs e) => App.Me.ShowSettings();

    public void RefreshIdentity()
    {
        SelfName.Text = App.Me.Settings.ComputerName;
        Title = "Ortak Alan — " + App.Me.Settings.ComputerName;
    }

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SwatchVm swatch) return;
        foreach (var item in _swatches)
            item.Selected = item == swatch;
        ThemeService.Apply(swatch.Name);
        App.Me.Settings.Theme = swatch.Name;
        App.Me.Store.Save();
        TemaPopup.IsOpen = false;
    }

    void Bind(string id, string name)
    {
        if (_lines != null) _lines.CollectionChanged -= LinesChanged;
        SelectedId = id;
        PeerTitle.Text = name;
        EmptyChat.Visibility = Visibility.Collapsed;
        _lines = App.Me.Chats.Get(id);
        _lines.CollectionChanged += LinesChanged;
        Messages.ItemsSource = _lines;
        Input.IsEnabled = true;
        SendButton.IsEnabled = true;
        FileButton.IsEnabled = true;
        StatusText.Text = App.Me.Store.IsApproved(id)
            ? ""
            : "İlk mesajda karşı bilgisayardan bir kez izin istenir.";
        ScrollEnd();
    }

    void LinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollEnd();

    void ScrollEnd() =>
        Dispatcher.BeginInvoke(() => Transcript.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

    async void Send_Click(object sender, RoutedEventArgs e) => await SendText();

    async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendText();
    }

    void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async Task SendText()
    {
        if (_busy || SelectedId == null) return;
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        _busy = true;
        Input.Clear();
        var line = new ChatLine { Mine = true, Text = text, Utc = DateTime.Now };
        App.Me.Chats.Add(SelectedId, line);
        StatusText.Text = "Gönderiliyor…";
        try
        {
            var error = await App.Me.Node.SendChatAsync(SelectedId, text);
            StatusText.Text = error ?? "";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Gönderilemedi.";
        }
        finally
        {
            _busy = false;
        }
    }

    async Task LookForUpdate()
    {
        _offer = await UpdateService.CheckAsync();
        if (_offer == null) return;
        UpdateButton.Content = "Güncelle " + _offer.Version.ToString(3);
        UpdateButton.Visibility = Visibility.Visible;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_offer == null || _busy) return;
        _busy = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "İndiriliyor…";
        try
        {
            await UpdateService.ApplyAsync(_offer);
            App.Me.ExitApp();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _busy = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Güncelle " + _offer.Version.ToString(3);
            StatusText.Text = "Güncelleme indirilemedi.";
        }
    }

    void Screen_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        SnipWindow snip;
        try
        {
            snip = new SnipWindow();
            if (snip.ShowDialog() != true || snip.Image == null)
                return;
        }
        finally
        {
            Show();
            Activate();
        }

        var editor = new ShotEditorWindow(snip.Image) { Owner = this };
        if (editor.ShowDialog() != true || string.IsNullOrEmpty(editor.OutputPath)) return;
        if (editor.SendToChat)
            _ = SendFile(editor.OutputPath);
        else
            StatusText.Text = "Kaydedildi.";
    }

    async void File_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Gönderilecek dosya" };
        if (dialog.ShowDialog(this) != true) return;
        await SendFile(dialog.FileName);
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await SendFile(files[0]);
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async Task SendFile(string path)
    {
        if (SelectedId == null)
        {
            StatusText.Text = "Bir bilgisayar seçin.";
            return;
        }
        if (_busy) return;
        _busy = true;
        var name = System.IO.Path.GetFileName(path);
        var line = new ChatLine
        {
            Mine = true,
            FileName = name,
            FilePath = path,
            Text = name,
            Status = "Gönderiliyor",
            Utc = DateTime.Now
        };
        App.Me.Chats.Add(SelectedId, line);
        StatusText.Text = "Gönderiliyor…";
        try
        {
            var error = await App.Me.Node.SendFileAsync(SelectedId, path);
            line.Status = error ?? "Gönderildi";
            StatusText.Text = error ?? "";
            App.Me.Chats.Save(SelectedId);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            line.Status = "Gönderilemedi";
            StatusText.Text = "Gönderilemedi.";
            App.Me.Chats.Save(SelectedId);
        }
        finally
        {
            _busy = false;
        }
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ChatLine line) return;
        var path = line.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusText.Text = "Dosya bulunamadı.";
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
