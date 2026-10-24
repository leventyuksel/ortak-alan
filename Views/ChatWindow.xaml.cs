using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrtakAlan.Views;

public partial class ChatWindow : ChromeWindow
{
    ObservableCollection<ChatLine>? _lines;
    ListCollectionView? _messageView;
    readonly ListCollectionView _people;
    bool _busy;
    bool _holdSelection;
    Storyboard? _blink;
    public string? SelectedId { get; private set; }

    public ChatWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage, openKunye: false);
        TransferUi.Attach(this, TransferBar, StatusText);
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        DockRight();
        SelfName.Text = App.Me.Settings.ComputerName;
        UpdateService.PendingChanged += ShowUpdateIcon;
        Closed += (_, _) => UpdateService.PendingChanged -= ShowUpdateIcon;
        ShowUpdateIcon();
        _people = new ListCollectionView(App.Me.Directory.Peers);
        _people.Filter = FilterPerson;
        _people.SortDescriptions.Add(new SortDescription(nameof(PeerVm.Online), ListSortDirection.Descending));
        _people.SortDescriptions.Add(new SortDescription(nameof(PeerVm.Name), ListSortDirection.Ascending));
        _people.LiveSortingProperties.Add(nameof(PeerVm.Online));
        _people.LiveSortingProperties.Add(nameof(PeerVm.Name));
        _people.IsLiveSorting = true;
        PeerList.ItemsSource = _people;
        Title = "Ortak Alan — " + App.Me.Settings.ComputerName;
        App.Me.Directory.Changed += RefreshPeers;
        RefreshPeers();
    }

    void DockRight()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1000, Math.Max(MinWidth, area.Width - 48));
        Height = Math.Max(MinHeight, area.Height - 16);
        Left = area.Right - Width - 8;
        Top = area.Top + 8;
    }

    public void ClearPeer(string id)
    {
        if (SelectedId != id) return;
        _holdSelection = true;
        PeerList.SelectedItem = null;
        _holdSelection = false;
        if (_lines != null) _lines.CollectionChanged -= LinesChanged;
        _lines = null;
        _messageView = null;
        SelectedId = null;
        Messages.ItemsSource = null;
        PeerTitle.Text = "";
        Input.IsEnabled = false;
        SendButton.IsEnabled = false;
        FileButton.IsEnabled = false;
        MarkRoom(false);
        UpdateChatEmpty();
        StatusText.Text = "";
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
        UpdatePeopleEmpty();
        if (SelectedId == null || SelectedId == RoomChat.Id) return;
        var peer = App.Me.Directory.Peers.FirstOrDefault(p => p.Id == SelectedId);
        if (peer != null) PeerTitle.Text = peer.Name;
    }

    void UpdatePeopleEmpty()
    {
        var query = PersonSearch.Text.Trim();
        var any = _people.Cast<object>().Any();
        EmptyPeers.Text = query.Length == 0 ? "Henüz kimse yok." : "Eşleşen kişi yok.";
        EmptyPeers.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    bool FilterPerson(object item)
    {
        if (item is not PeerVm peer) return false;
        var query = PersonSearch.Text.Trim();
        return query.Length == 0 || peer.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    void PersonSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PersonHint.Visibility = PersonSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _people.Refresh();
        UpdatePeopleEmpty();
    }

    void ChatSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ChatHint.Visibility = ChatSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _messageView?.Refresh();
        UpdateChatEmpty();
    }

    bool FilterLine(object item)
    {
        if (item is not ChatLine line) return false;
        var query = ChatSearch.Text.Trim();
        if (query.Length == 0) return true;
        return line.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || line.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (line.FileName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    void UpdateChatEmpty()
    {
        if (SelectedId == null)
        {
            EmptyChat.Text = "Bir konuşma seçin.";
            EmptyChat.Visibility = Visibility.Visible;
            return;
        }
        var any = _messageView?.Cast<object>().Any() ?? false;
        var query = ChatSearch.Text.Trim();
        EmptyChat.Text = "Eşleşen mesaj yok.";
        EmptyChat.Visibility = !any && query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Room_Click(object sender, MouseButtonEventArgs e)
    {
        _holdSelection = true;
        PeerList.SelectedItem = null;
        _holdSelection = false;
        MarkRoom(true);
        Bind(RoomChat.Id, RoomChat.Title);
        StatusText.Text = "Buraya yazılanı ağdaki herkes görür. Dosyaya tıklayınca alınır.";
        e.Handled = true;
    }

    void MarkRoom(bool on)
    {
        RoomCard.BorderThickness = new Thickness(1.5);
        RoomCard.Background = new SolidColorBrush(on
            ? Color.FromArgb(235, 0x0C, 0x3E, 0x48)
            : Color.FromArgb(210, 0x08, 0x24, 0x2C));
    }

    async void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_holdSelection || PeerList.SelectedItem is not PeerVm peer) return;
        MarkRoom(false);
        Bind(peer.Id, peer.Name);
        await RequestApproval(peer);
    }

    async Task RequestApproval(PeerVm peer)
    {
        if (!peer.Online)
        {
            StatusText.Text = "Çevrimdışı. Mesaj açılınca iletilir.";
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
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Listeden kaldır" };
        remove.Click += (_, _) => App.Me.RemoveFromList(peer.Id, peer.Name);
        menu.Items.Add(remove);
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
        if (share.Kind == "folder")
        {
            if (!peer.Online)
            {
                StatusText.Text = "Çevrimdışı";
                return;
            }
            StatusText.Text = ShareConnect.OpenFolder(peer, share, true);
        }
        else
        {
            StatusText.Text = "Yazıcılar klasörüne ekleniyor…";
            var text = await ShareConnect.ConnectPrinter(peer, share, this);
            if (text.Length > 0) StatusText.Text = text;
        }
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

    void Kunye_Click(object sender, RoutedEventArgs e) => KunyeWindow.ShowOnce();

    public void RefreshIdentity()
    {
        SelfName.Text = App.Me.Settings.ComputerName;
        Title = "Ortak Alan — " + App.Me.Settings.ComputerName;
    }

    void Bind(string id, string name)
    {
        if (_lines != null) _lines.CollectionChanged -= LinesChanged;
        SelectedId = id;
        PeerTitle.Text = name;
        _lines = App.Me.Chats.Get(id);
        _lines.CollectionChanged += LinesChanged;
        _messageView = new ListCollectionView(_lines);
        _messageView.Filter = FilterLine;
        Messages.ItemsSource = _messageView;
        Input.IsEnabled = true;
        SendButton.IsEnabled = true;
        FileButton.IsEnabled = true;
        if (id == RoomChat.Id)
            StatusText.Text = "Buraya yazılanı ağdaki herkes görür. Dosyaya tıklayınca alınır.";
        else
        {
            var peer = App.Me.Directory.Peers.FirstOrDefault(p => p.Id == id);
            StatusText.Text = peer is { Online: false }
                ? "Çevrimdışı. Mesaj açılınca iletilir."
                : App.Me.Store.IsApproved(id) ? "" : "İlk mesajda karşı bilgisayardan bir kez izin istenir.";
        }
        UpdateChatEmpty();
        ScrollEnd();
        App.Me.MarkRead(id);
        _ = MarkSeen();
    }

    async Task MarkSeen()
    {
        if (SelectedId == null || _lines == null) return;
        var chatId = SelectedId;
        if (chatId == RoomChat.Id)
        {
            var groups = _lines
                .Where(line => !line.Mine && !line.Seen && line.Id.Length > 0 && line.AuthorId.Length > 0 && line.AuthorId != App.Me.Settings.ComputerId)
                .GroupBy(line => line.AuthorId)
                .ToList();
            var changed = false;
            foreach (var group in groups)
            {
                var error = await App.Me.Node.SendSeenAsync(group.Key, RoomChat.Id, group.Select(line => line.Id).Distinct().ToList());
                if (error != null || SelectedId != chatId) continue;
                foreach (var line in group) line.Seen = true;
                changed = true;
            }
            if (changed) App.Me.Chats.Save(RoomChat.Id);
            return;
        }

        var pending = _lines.Where(line => !line.Mine && !line.Seen && line.Id.Length > 0).Select(line => line.Id).Distinct().ToList();
        if (pending.Count == 0) return;
        var err = await App.Me.Node.SendSeenAsync(chatId, "", pending);
        if (err != null || SelectedId != chatId) return;
        foreach (var line in _lines.Where(line => !line.Mine && pending.Contains(line.Id)))
            line.Seen = true;
        App.Me.Chats.Save(chatId);
    }

    void LinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollEnd();
        UpdateChatEmpty();
        if (e.Action == NotifyCollectionChangedAction.Add)
            _ = MarkSeen();
    }

    void Transcript_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedId == null || _lines == null || SelectedId == RoomChat.Id) return;
        var peerId = SelectedId;
        var line = LineOf(e.OriginalSource);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (line != null)
        {
            var one = new MenuItem { Header = "Bu mesajı sil" };
            one.Click += (_, _) => App.Me.Chats.Remove(peerId, line);
            menu.Items.Add(one);
        }
        var all = new MenuItem { Header = "Tümünü sil", IsEnabled = _lines.Count > 0 };
        all.Click += (_, _) =>
        {
            if (_lines.Count == 0) return;
            if (MessageBox.Show(this, "Bu konuşmadaki tüm mesajlar silinsin mi?", "Sohbet", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            App.Me.Chats.Clear(peerId);
        };
        menu.Items.Add(all);
        menu.IsOpen = true;
        e.Handled = true;
    }

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
        var line = new ChatLine
        {
            Id = Guid.NewGuid().ToString("N"),
            Mine = true,
            AuthorId = App.Me.Settings.ComputerId,
            Text = text,
            Utc = DateTime.Now
        };
        App.Me.Chats.Add(SelectedId, line);
        StatusText.Text = "Gönderiliyor…";
        try
        {
            var error = SelectedId == RoomChat.Id
                ? await App.Me.Node.BroadcastRoomAsync(line)
                : await App.Me.Node.SendChatAsync(SelectedId, text, line.Id);
            if (error != null)
            {
                if (SelectedId != RoomChat.Id) line.Failed = true;
                line.Status = error;
                App.Me.Chats.Save(SelectedId);
                StatusText.Text = error;
            }
            else if (SelectedId != RoomChat.Id && App.Me.Node.IsQueued(SelectedId, line.Id))
            {
                line.Status = "İletilecek";
                App.Me.Chats.Save(SelectedId);
                StatusText.Text = "Mesaj iletilecek. Kişi açılınca gider.";
            }
            else StatusText.Text = "";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            line.Failed = true;
            line.Status = "Gönderilemedi";
            App.Me.Chats.Save(SelectedId);
            StatusText.Text = "Gönderilemedi.";
        }
        finally
        {
            _busy = false;
        }
    }

    void ShowUpdateIcon()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowUpdateIcon);
            return;
        }

        var offer = UpdateService.Pending;
        var pending = offer != null;
        UpdateMark.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        UpdateMark.IsEnabled = pending && !UpdateService.Applying;
        UpdateMark.ToolTip = !pending ? null : UpdateService.Applying ? "İndiriliyor…" : "Sürüm " + offer!.Version.ToString(3) + " — kurmak için tıklayın";
        if (pending && !UpdateService.Applying) StartBlink();
        else StopBlink();
    }

    void StartBlink()
    {
        if (_blink != null) return;
        var animation = new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(420))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, LogoFrame);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        _blink = new Storyboard();
        _blink.Children.Add(animation);
        _blink.Begin(this, true);
    }

    void StopBlink()
    {
        if (_blink != null)
        {
            _blink.Stop(this);
            _blink = null;
        }
        LogoFrame.Opacity = 1;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Pending == null || UpdateService.Applying) return;
        UpdateMark.IsEnabled = false;
        UpdateMark.ToolTip = "İndiriliyor…";
        StopBlink();
        var error = await UpdateService.ApplyPendingAsync();
        if (error == null)
        {
            App.Me.ExitApp();
            return;
        }

        StatusText.Text = error;
        ShowUpdateIcon();
    }

    async void Screen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId == null)
        {
            MessageBox.Show(this, "Önce soldan Ortak konuşmayı veya bir kişiyi seçin.", "Ekran alıntısı");
            StatusText.Text = "Önce bir kişi seçin.";
            return;
        }

        var peerId = SelectedId;
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
            await SendFile(editor.OutputPath, peerId);
        else
            StatusText.Text = "Kaydedildi.";
    }

    async void File_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Gönderilecek dosya" };
        if (dialog.ShowDialog(this) != true) return;
        await SendFile(dialog.FileName, SelectedId);
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
        var files = CollectFiles(dropped);
        if (files.Count == 0)
        {
            StatusText.Text = "Gönderilecek dosya yok.";
            return;
        }
        await SendFiles(files, SelectedId);
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    static List<string> CollectFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                files.Add(path);
                continue;
            }
            if (!Directory.Exists(path)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (files.Count >= 200) return files;
                    files.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
        return files;
    }

    Task SendFile(string path, string? peerId) => SendFiles([path], peerId);

    async Task SendFiles(IReadOnlyList<string> paths, string? peerId)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            MessageBox.Show(this, "Önce soldan Ortak konuşmayı veya bir kişiyi seçin.", "Dosya");
            StatusText.Text = "Önce bir konuşma seçin.";
            return;
        }
        if (peerId != SelectedId)
            Bind(peerId, App.Me.Directory.Peers.FirstOrDefault(p => p.Id == peerId)?.Name ?? "Sohbet");
        if (_busy) return;
        _busy = true;
        var sent = 0;
        var waiting = false;
        try
        {
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                var name = Path.GetFileName(path);
                var line = new ChatLine
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Mine = true,
                    AuthorId = App.Me.Settings.ComputerId,
                    FileName = name,
                    FilePath = path,
                    Text = name,
                    Status = "Gönderiliyor",
                    Utc = DateTime.Now
                };
                App.Me.Chats.Add(peerId, line);
                StatusText.Text = "Gönderiliyor… " + name;
                try
                {
                    var error = peerId == RoomChat.Id
                        ? await App.Me.Node.SendRoomFileAsync(line.Id, path)
                        : await App.Me.Node.SendFileAsync(peerId, path, line.Id);
                    var queued = error == null && peerId != RoomChat.Id && App.Me.Node.IsQueued(peerId, line.Id);
                    if (error != null && peerId != RoomChat.Id) line.Failed = true;
                    line.Status = error ?? (queued ? "İletilecek" : "Gönderildi");
                    if (queued) waiting = true;
                    if (error == null) sent++;
                    else StatusText.Text = name + ": " + error;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    line.Failed = true;
                    line.Status = "Gönderilemedi";
                    StatusText.Text = name + " gönderilemedi.";
                }
                App.Me.Chats.Save(peerId);
            }
        }
        finally
        {
            _busy = false;
        }
        if (waiting)
            StatusText.Text = "İletilecek. Kişi açılınca gider.";
        else if (sent > 0)
            StatusText.Text = sent == 1 ? "Gönderildi." : sent + " dosya gönderildi.";
    }

    async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (LineOf(sender) is not ChatLine line) return;
        var path = line.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = SharedFiles.FindIncoming(line.FileName ?? "");
        if ((string.IsNullOrEmpty(path) || !File.Exists(path)) && SelectedId == RoomChat.Id && line.Id.Length > 0)
        {
            line.Status = "Alınıyor";
            StatusText.Text = "Dosya alınıyor…";
            var (error, fetched) = await App.Me.Node.FetchRoomFileAsync(line.Id, line.FileName ?? "", line.AuthorId);
            if (error != null || string.IsNullOrEmpty(fetched) || !File.Exists(fetched))
            {
                line.Status = error ?? "Dosya alınamadı";
                StatusText.Text = line.Status;
                return;
            }
            path = fetched;
            line.FilePath = fetched;
            line.Status = "Alındı";
            App.Me.Chats.Save(RoomChat.Id);
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusText.Text = "Dosya bulunamadı.";
            return;
        }

        if (!string.Equals(line.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            line.FilePath = path;
            if (SelectedId != null) App.Me.Chats.Save(SelectedId);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Dosya açılamadı.";
        }
    }

    static ChatLine? LineOf(object sender)
    {
        var current = sender as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement element && element.DataContext is ChatLine line)
                return line;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
