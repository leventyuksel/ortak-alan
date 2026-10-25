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
    readonly ObservableCollection<string> _quick = new();
    readonly ListCollectionView _people;
    bool _busy;
    bool _holdSelection;
    bool _favoritesOnly;
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
        QuickList.ItemsSource = _quick;
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
        FavoritesButton.IsEnabled = false;
        QuickButton.IsEnabled = false;
        _favoritesOnly = false;
        PaintFavorites();
        QuickPanel.Visibility = Visibility.Collapsed;
        PaintQuick();
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
        if (_favoritesOnly && !line.Favorite) return false;
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
        if (any)
        {
            EmptyChat.Visibility = Visibility.Collapsed;
            return;
        }
        var query = ChatSearch.Text.Trim();
        if (_favoritesOnly && query.Length == 0) EmptyChat.Text = "Favori mesaj yok.";
        else if (_favoritesOnly) EmptyChat.Text = "Eşleşen favori yok.";
        else if (query.Length > 0) EmptyChat.Text = "Eşleşen mesaj yok.";
        else
        {
            EmptyChat.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyChat.Visibility = Visibility.Visible;
    }

    void Favorites_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId == null) return;
        _favoritesOnly = !_favoritesOnly;
        PaintFavorites();
        _messageView?.Refresh();
        UpdateChatEmpty();
    }

    void PaintFavorites()
    {
        FavoritesButton.ToolTip = _favoritesOnly ? "Tüm mesajları göster" : "Sadece favori mesajları göster";
        if (_favoritesOnly)
        {
            FavoritesButton.SetResourceReference(BackgroundProperty, "Accent");
            FavoritesButton.SetResourceReference(BorderBrushProperty, "Accent");
            FavoritesButton.SetResourceReference(ForegroundProperty, "OnAccent");
            return;
        }
        FavoritesButton.ClearValue(BackgroundProperty);
        FavoritesButton.ClearValue(BorderBrushProperty);
        FavoritesButton.ClearValue(ForegroundProperty);
    }

    void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId == null || LineOf(sender) is not ChatLine line) return;
        line.Favorite = !line.Favorite;
        App.Me.Chats.Save(SelectedId);
        if (_favoritesOnly)
        {
            _messageView?.Refresh();
            UpdateChatEmpty();
        }
    }

    void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId == null) return;
        var open = QuickPanel.Visibility != Visibility.Visible;
        QuickPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) LoadQuick();
        PaintQuick();
    }

    void PaintQuick()
    {
        if (QuickPanel.Visibility == Visibility.Visible)
        {
            QuickButton.SetResourceReference(BackgroundProperty, "Accent");
            QuickButton.SetResourceReference(BorderBrushProperty, "Accent");
            QuickButton.SetResourceReference(ForegroundProperty, "OnAccent");
            return;
        }
        QuickButton.ClearValue(BackgroundProperty);
        QuickButton.ClearValue(BorderBrushProperty);
        QuickButton.ClearValue(ForegroundProperty);
    }

    void LoadQuick()
    {
        _quick.Clear();
        foreach (var text in App.Me.Store.CopyQuick())
            _quick.Add(text);
        QuickEmpty.Visibility = _quick.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void QuickInput_TextChanged(object sender, TextChangedEventArgs e) =>
        QuickHint.Visibility = QuickInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void QuickInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveQuick();
    }

    void QuickSave_Click(object sender, RoutedEventArgs e) => SaveQuick();

    void SaveQuick()
    {
        var text = QuickInput.Text.Trim();
        if (text.Length == 0)
        {
            Note("Önce sabit mesaj yazın.");
            return;
        }
        if (!App.Me.Store.AddQuick(text))
        {
            Note("Bu metin zaten sabit mesajlarda.");
            return;
        }
        QuickInput.Clear();
        LoadQuick();
        Note("Sabit mesaj kaydedildi.");
    }

    async void QuickSend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string text }) return;
        await SendText(text);
    }

    void QuickRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string text }) return;
        App.Me.Store.RemoveQuick(text);
        LoadQuick();
    }

    void LeaveFavorites()
    {
        if (!_favoritesOnly) return;
        _favoritesOnly = false;
        PaintFavorites();
        _messageView?.Refresh();
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
        if (!App.Me.Store.Allows(peer.Id))
        {
            StatusText.Text = LicenseService.SeatStatus;
            LicenseService.NotifySeat();
            return;
        }
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
        _favoritesOnly = false;
        PaintFavorites();
        _lines = App.Me.Chats.Get(id);
        _lines.CollectionChanged += LinesChanged;
        _messageView = new ListCollectionView(_lines);
        _messageView.Filter = FilterLine;
        Messages.ItemsSource = _messageView;
        Input.IsEnabled = true;
        SendButton.IsEnabled = true;
        FileButton.IsEnabled = true;
        FavoritesButton.IsEnabled = true;
        QuickButton.IsEnabled = true;
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
        if (SelectedId == null || _lines == null) return;
        var peerId = SelectedId;
        var line = LineOf(e.OriginalSource);
        if (line == null) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (line.Text.Length > 0)
        {
            var selected = e.OriginalSource is DependencyObject source ? MessageOf(source)?.SelectedPlain() ?? "" : "";
            var whole = line.Text;
            if (selected.Length > 0 && !string.Equals(selected, whole, StringComparison.Ordinal))
            {
                var part = new MenuItem { Header = "Seçileni kopyala", InputGestureText = "Ctrl+C" };
                var copy = selected;
                part.Click += (_, _) => MessageText.CopyToClipboard(copy, this);
                menu.Items.Add(part);
                var allText = new MenuItem { Header = "Mesajı kopyala" };
                allText.Click += (_, _) => MessageText.CopyToClipboard(whole, this);
                menu.Items.Add(allText);
            }
            else
            {
                var allText = new MenuItem { Header = "Mesajı kopyala", InputGestureText = "Ctrl+C" };
                var copy = selected.Length > 0 ? selected : whole;
                allText.Click += (_, _) => MessageText.CopyToClipboard(copy, this);
                menu.Items.Add(allText);
            }
        }

        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        if (!line.IsFile && line.Text.Length > 0)
        {
            var keep = new MenuItem { Header = "Sabit mesajlara ekle" };
            var saved = line.Text;
            keep.Click += (_, _) =>
            {
                if (!App.Me.Store.AddQuick(saved))
                {
                    Note("Bu metin zaten sabit mesajlarda.");
                    return;
                }
                if (QuickPanel.Visibility == Visibility.Visible) LoadQuick();
                Note("Sabit mesajlara eklendi.");
            };
            menu.Items.Add(keep);
        }
        var one = new MenuItem { Header = "Sil" };
        one.Click += async (_, _) => await ForgetLine(peerId, line);
        menu.Items.Add(one);
        menu.IsOpen = true;
        e.Handled = true;
    }

    async Task ForgetLine(string chatId, ChatLine line)
    {
        var id = line.Id;
        var unsent = chatId != RoomChat.Id && App.Me.Node.CancelPending(chatId, id);
        App.Me.Chats.Remove(chatId, line);
        UpdateChatEmpty();
        if (unsent || id.Length == 0) return;
        try
        {
            if (chatId == RoomChat.Id) await App.Me.Node.BroadcastDropAsync(id);
            else await App.Me.Node.SendDropAsync(chatId, id);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    internal void Note(string text) => StatusText.Text = text;

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

    async Task SendText(string? preset = null)
    {
        if (SelectedId == null) return;
        if (_busy)
        {
            if (preset != null) Note("Gönderim bitince yeniden deneyin.");
            return;
        }
        var text = (preset ?? Input.Text).Trim();
        if (text.Length == 0) return;
        if (SelectedId != RoomChat.Id && !App.Me.Store.Allows(SelectedId))
        {
            StatusText.Text = LicenseService.SeatStatus;
            LicenseService.NotifySeat();
            return;
        }
        _busy = true;
        if (preset == null) Input.Clear();
        LeaveFavorites();
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
        if (peerId != RoomChat.Id && !App.Me.Store.Allows(peerId))
        {
            StatusText.Text = LicenseService.SeatStatus;
            LicenseService.NotifySeat();
            return;
        }
        if (_busy) return;
        LeaveFavorites();
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
            if (current is FrameworkContentElement content && content.DataContext is ChatLine inline)
                return inline;
            current = NextParent(current);
        }
        return null;
    }

    static MessageText? MessageOf(DependencyObject source)
    {
        var current = (DependencyObject?)source;
        while (current != null)
        {
            if (current is MessageText text) return text;
            current = NextParent(current);
        }
        return null;
    }

    static DependencyObject? NextParent(DependencyObject item)
    {
        if (item is Visual)
            return VisualTreeHelper.GetParent(item);
        if (item is FrameworkContentElement content)
            return content.Parent;
        return LogicalTreeHelper.GetParent(item);
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
