using System.Media;
using System.Threading;
using System.Windows;
using OrtakAlan.Views;

namespace OrtakAlan;

public partial class App : Application
{
    public static App Me => (App)Current;

    public SettingsService Store { get; private set; } = null!;
    public SettingsModel Settings => Store.Current;
    public PeerDirectory Directory { get; private set; } = null!;
    public ConversationStore Chats { get; private set; } = null!;
    public PeerNode Node => _node ?? throw new InvalidOperationException("Bağlantı henüz başlamadı.");
    PeerNode? _node;
    public bool Exiting { get; private set; }

    Mutex? _mutex;
    TrayService? _tray;
    ShareWindow? _share;
    int _unread;
    readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    ChatWindow? _chat;
    FilesWindow? _files;
    EventWaitHandle? _commandWait;
    CancellationTokenSource? _commandStop;
    ThemeWindow? _theme;
    SettingsWindow? _settings;
    bool _demo;
    int _restoreGate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write(args.Exception);
            args.Handled = true;
        };

        var dataDir = Arg(e.Args, "--veri")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrtakAlan");
        _demo = Arg(e.Args, "--veri") != null;
        System.IO.Directory.CreateDirectory(dataDir);
        Log.FilePath = Path.Combine(dataDir, "gunluk.log");

        ThemeService.Register();
        Store = new SettingsService(dataDir);
        Store.Load();
        if (Settings.Theme is "Lacivert" or "Mavi" or "Grafit")
        {
            Settings.Theme = "Tiffany";
            Store.Save();
        }
        ThemeService.ApplySaved(Settings.Theme, Settings.Appearance, Settings.TextColor);
        ForgetWindowsErrors();
        BrandAssets.Init();

        var fresh = false;
        if (string.IsNullOrWhiteSpace(Settings.ComputerName))
        {
            var setup = new SetupWindow();
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            Settings.ComputerName = setup.ChosenName;
            Settings.StartWithWindows = !_demo;
            Store.Save();
            fresh = true;
        }

        try
        {
            _mutex = new Mutex(true, @"Local\OrtakAlan-" + Settings.ComputerId, out var created);
            if (!created)
            {
                if (TryKlasor(e.Args, out var peerId, out var shareId))
                    CommandBus.Send(peerId, shareId);
                else if (HasFlag(e.Args, "--paylasilan"))
                {
                    if (!CommandBus.ShowFiles())
                        MessageBox.Show("Ortak Alan zaten çalışıyor. Sağ alttaki simgeden açabilirsiniz.", "Ortak Alan");
                }
                else if (!CommandBus.ShowChat())
                    MessageBox.Show("Ortak Alan zaten çalışıyor. Sağ alttaki simgeden açabilirsiniz.", "Ortak Alan");
                Shutdown();
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // Önceki kapanış yarım kaldıysa bu oturum devam eder.
        }

        if (!_demo)
        {
            if (Settings.StartWithWindows) AutostartService.Enable();
            else AutostartService.Disable();
        }
        if (!_demo)
        {
            DesktopShortcut.Ensure(fresh);
        }

        Directory = new PeerDirectory();
        foreach (var person in Settings.Hidden)
            Directory.Hidden.Add(person.Id);
        foreach (var person in Settings.People.Concat(Settings.Approved))
        {
            if (!Directory.Hidden.Contains(person.Id))
                Directory.Remember(person.Id, person.Name);
        }
        Chats = new ConversationStore(dataDir);
        _node = new PeerNode(Settings, Store, AskApproval);
        _node.PeersUpdated += list =>
        {
            Directory.Apply(list);
            LocalPrinters.Note(list);
            foreach (var peer in list)
            {
                Store.RememberPerson(peer.Id, peer.Name);
                if (peer.Online) _ = _node.FlushAsync(peer.Id);
            }
            DesktopShortcut.Maintain(list);
        };
        _node.ChatReceived += OnChat;
        _node.FileReceived += OnFile;
        _node.Delivered += (peer, id) => Dispatcher.BeginInvoke(() => NoteDelivery(peer, id, null));
        _node.SendFailed += (peer, id, error) => Dispatcher.BeginInvoke(() => NoteDelivery(peer, id, error));
        _node.MessageSeen += (chat, ids) => Dispatcher.BeginInvoke(() => NoteSeen(chat, ids));
        _node.PrintReceived += OnPrint;
        _node.RoomReceived += OnRoom;
        _node.RoomFileSaved += OnRoomFile;
        _node.RoomHistory = () => Chats.Posts(RoomChat.Id);
        _node.RoomFilePath = id => Chats.Find(RoomChat.Id, id)?.FilePath;
        _node.SeedRoom(Chats.Posts(RoomChat.Id).Select(post => post.Id));
        _node.Start();
        LocalPrinters.Start(
            dataDir,
            (peer, share, file) => _node.SendPrintAsync(peer, share, file),
            (title, text, error) => Dispatcher.BeginInvoke(() => PrintNotice.Show(title, text, error)));

        if (!_demo)
        {
            _tray = new TrayService(
                "Ortak Alan — " + Settings.ComputerName,
                ToggleChat,
                ShowShare,
                () => ShowChat(),
                ShowSettings,
                ExitApp);
        }

        if (!_demo)
        {
            ShowChat();
            ListenForCommands();
        }
        _ = UpdateService.LookAsync();
        if (HasFlag(e.Args, "--paylasilan"))
            ShowFiles();
        if (TryKlasor(e.Args, out var openPeer, out var openShare))
            _ = OpenFolderRequest(openPeer, openShare);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { LocalPrinters.Stop(); } catch { /* kapanış */ }
        try { _commandStop?.Cancel(); } catch { /* kapanış */ }
        try { _commandWait?.Dispose(); } catch { /* kapanış */ }
        try { _node?.Dispose(); } catch { /* kapanış */ }
        try { _tray?.Dispose(); } catch { /* kapanış */ }
        base.OnExit(e);
    }

    public void ShowFiles(string? peerId = null, string? peerName = null, string? shareId = null, string? shareName = null)
    {
        if (_files == null)
        {
            _files = new FilesWindow();
            _files.Closed += (_, _) => _files = null;
        }
        if (_files.WindowState == WindowState.Minimized)
            _files.WindowState = WindowState.Normal;
        _files.Show();
        _files.Activate();
        if (!string.IsNullOrEmpty(peerId) && !string.IsNullOrEmpty(shareId))
            _files.OpenShare(peerId, peerName ?? "", shareId, shareName ?? "");
    }

    public void ShowShare()
    {
        _share ??= new ShareWindow();
        _share.Reload();
        _share.ShowAtCorner();
    }

    public void ToggleChat()
    {
        if (_chat is { IsVisible: true } && _unread == 0) _chat.Hide();
        else ShowChat();
    }

    public void ToggleShare()
    {
        if (_share is { IsVisible: true }) _share.Hide();
        else ShowShare();
    }

    public void RemoveFromList(string id, string name)
    {
        Store.HidePerson(id, name);
        Directory.Forget(id);
        _chat?.ClearPeer(id);
    }

    public void MarkRead(string? peerId)
    {
        if (string.IsNullOrEmpty(peerId) || !_pending.Remove(peerId)) return;
        _unread = _pending.Count;
        _tray?.SetUnread(_unread);
    }

    public void ShowChat(string? peerId = null)
    {
        _chat ??= new ChatWindow();
        if (_chat.WindowState == WindowState.Minimized)
            _chat.WindowState = WindowState.Normal;
        _chat.Show();
        _chat.Activate();
        if (!string.IsNullOrEmpty(peerId)) _chat.SelectPeer(peerId);
        else MarkRead(_chat.SelectedId);
    }

    public void ShowSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow();
        _settings.Show();
    }

    public void PublishIdentity()
    {
        _chat?.RefreshIdentity();
        _node?.Announce();
    }

    public void ShowTheme()
    {
        if (_theme is { IsVisible: true })
        {
            _theme.Activate();
            return;
        }

        _theme = new ThemeWindow();
        _theme.Show();
    }

    public string AddFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (Store.CopyShares().Any(s => s.Kind == "folder" && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            return "Bu klasör zaten paylaşımda.";

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = path;
        var item = new ShareItem { Kind = "folder", Name = name, Path = path };
        Store.AddShare(item);
        _node?.Announce();
        return "Klasör paylaşıldı. Ağdaki bilgisayarlar görebilir.";
    }

    public string AddPrinter(string printerName)
    {
        if (Store.CopyShares().Any(s => s.Kind == "printer" && string.Equals(s.Path, printerName, StringComparison.OrdinalIgnoreCase)))
            return "Bu yazıcı zaten paylaşımda.";
        if (!PrinterRelay.Installed(printerName))
            return "Yazıcı bulunamadı.";

        var item = new ShareItem { Kind = "printer", Name = printerName, Path = printerName };
        Store.AddShare(item);
        _node?.Announce();
        return "Yazıcı paylaşıldı. Karşı bilgisayar bunu kendi Yazıcılar klasörüne ekler.";
    }

    void ForgetWindowsErrors()
    {
        var dirty = false;
        foreach (var share in Settings.Shares)
        {
            if (string.IsNullOrEmpty(share.WindowsError)) continue;
            share.WindowsError = null;
            dirty = true;
        }
        if (dirty) Store.Save();
    }

    public void RemoveShare(string id)
    {
        var item = Store.TakeShare(id);
        if (item == null) return;
        if (!string.IsNullOrEmpty(item.WindowsShare))
        {
            if (item.Kind == "folder") WindowsShareService.TryUnshareFolder(item.WindowsShare);
            else WindowsShareService.TryUnsharePrinter(item.Path);
        }
        _node?.Announce();
    }

    public void ExitApp()
    {
        Exiting = true;
        Shutdown();
    }

    Task<bool?> AskApproval(string id, string name)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            SystemSounds.Asterisk.Play();
            var window = new ApprovalWindow(name, Settings.ComputerName) { Topmost = true };
            window.ShowDialog();
            return window.Decision;
        }).Task;
    }

    void OnChat(IncomingChat message)
    {
        var added = Chats.Add(message.PeerId, new ChatLine
        {
            Id = message.MessageId,
            Mine = false,
            AuthorId = message.PeerId,
            Text = message.Text,
            Utc = DateTime.Now
        });
        Notice(message.PeerId, message.PeerName, message.Text);
        if (added) AskRestore(message.PeerId, message.PeerName);
    }

    void OnFile(IncomingFile file)
    {
        var added = Chats.Add(file.PeerId, new ChatLine
        {
            Id = file.MessageId,
            Mine = false,
            AuthorId = file.PeerId,
            FileName = file.FileName,
            FilePath = file.Path,
            Text = file.FileName,
            Status = "Alındı",
            Utc = DateTime.Now
        });
        Notice(file.PeerId, file.PeerName, "Dosya: " + file.FileName);
        if (added) AskRestore(file.PeerId, file.PeerName);
    }

    void AskRestore(string id, string name)
    {
        if (!Store.IsHidden(id)) return;
        if (Interlocked.CompareExchange(ref _restoreGate, 1, 0) != 0) return;
        try
        {
            var label = name.Length == 0 ? "Bu bilgisayar" : name;
            var owner = _chat is { IsVisible: true } ? _chat : null;
            var yes = MessageBox.Show(
                owner,
                label + " mesaj gönderdi. Kişi listesine eklensin mi?",
                "Ortak Alan",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!yes) return;
            Store.RestorePerson(id, label);
            Directory.Unhide(id);
            Directory.Remember(id, label);
            ShowChat(id);
        }
        finally
        {
            _restoreGate = 0;
        }
    }

    void NoteDelivery(string peerId, string messageId, string? error)
    {
        var line = Chats.Find(peerId, messageId);
        if (line == null) return;
        if (error == null)
        {
            line.Failed = false;
            if (line.Status is "İletilecek" or "Gönderiliyor" || line.Status.Contains("bekleniyor", StringComparison.OrdinalIgnoreCase))
                line.Status = line.IsFile ? "Gönderildi" : "";
        }
        else
        {
            line.Status = error;
            line.Failed = error.Contains("reddetti", StringComparison.Ordinal);
        }
        Chats.Save(peerId);
    }

    void NoteSeen(string chatId, string ids)
    {
        var changed = false;
        foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = Chats.Find(chatId, id);
            if (line == null || line.Seen) continue;
            line.Seen = true;
            changed = true;
        }
        if (changed) Chats.Save(chatId);
    }

    void OnRoom(RoomPost post)
    {
        var mine = post.AuthorId == Settings.ComputerId;
        var when = post.UtcTicks > 0 ? new DateTime(post.UtcTicks, DateTimeKind.Utc).ToLocalTime() : DateTime.Now;
        var line = new ChatLine
        {
            Id = post.Id,
            Mine = mine,
            Author = mine ? "" : post.Author,
            AuthorId = post.AuthorId,
            Text = post.Text,
            FileName = post.FileName,
            Status = string.IsNullOrEmpty(post.FileName) ? "" : "Almak için tıklayın",
            Utc = when
        };
        if (!Chats.Add(RoomChat.Id, line)) return;
        if (!mine)
            Notice(RoomChat.Id, post.Author.Length == 0 ? "Ortak konuşma" : post.Author, string.IsNullOrEmpty(post.FileName) ? post.Text : "Dosya: " + post.FileName);
    }

    void OnRoomFile(RoomFile file)
    {
        var line = Chats.Find(RoomChat.Id, file.MessageId);
        if (line == null)
        {
            var mine = file.AuthorId == Settings.ComputerId;
            if (!Chats.Add(RoomChat.Id, new ChatLine
            {
                Id = file.MessageId,
                Mine = mine,
                Author = mine ? "" : file.Author,
                AuthorId = file.AuthorId,
                Text = file.FileName,
                FileName = file.FileName,
                FilePath = file.Path,
                Status = "Alındı",
                Utc = DateTime.Now
            })) return;
            if (!mine)
                Notice(RoomChat.Id, file.Author.Length == 0 ? "Ortak konuşma" : file.Author, "Dosya: " + file.FileName);
            return;
        }

        line.FilePath = file.Path;
        if (string.IsNullOrEmpty(line.FileName)) line.FileName = file.FileName;
        line.Status = "Alındı";
        Chats.Save(RoomChat.Id);
    }

    void OnPrint(IncomingPrint job)
    {
        var text = job.Error == null
            ? job.FileName + " yazıcıya gönderildi."
            : job.FileName + " yazdırılamadı. " + job.Error;
        _tray?.Balloon(job.PeerName, text);
    }

    void Notice(string peerId, string title, string text)
    {
        if (_chat is { IsVisible: true } && _chat.SelectedId == peerId) return;
        _pending.Add(peerId);
        _unread = _pending.Count;
        _tray?.SetUnread(_unread);
        _tray?.Balloon(title, text);
    }

    void ListenForCommands()
    {
        _commandWait = new EventWaitHandle(false, EventResetMode.AutoReset, CommandBus.EventName);
        _commandStop = new CancellationTokenSource();
        var token = _commandStop.Token;
        var wait = _commandWait;
        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                bool signaled;
                try { signaled = wait.WaitOne(400); }
                catch { break; }
                if (!signaled) continue;
                if (!File.Exists(CommandBus.FilePath)) continue;
                var text = File.ReadAllText(CommandBus.FilePath);
                try { File.Delete(CommandBus.FilePath); } catch { /* bir sonraki deneme */ }
                if (text == "sohbet")
                {
                    Dispatcher.BeginInvoke(() => ShowChat());
                    continue;
                }
                if (text == "paylasilan")
                {
                    Dispatcher.BeginInvoke(() => ShowFiles());
                    continue;
                }
                var parts = text.Split('\t');
                if (parts.Length == 2)
                    Dispatcher.BeginInvoke(() => _ = OpenFolderRequest(parts[0], parts[1]));
            }
        }, token);
    }

    async Task OpenFolderRequest(string peerId, string shareId)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var peer = Directory.Peers.FirstOrDefault(p => p.Id == peerId);
            var share = peer?.Shares.FirstOrDefault(s => s.Id == shareId);
            if (peer != null && share != null)
            {
                if (share.Kind == "printer")
                    await ShareConnect.ConnectPrinter(peer, share, null);
                else
                    ShareConnect.OpenFolder(peer, share, false);
                return;
            }
            await Task.Delay(500);
        }
    }

    static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    static bool TryKlasor(string[] args, out string peerId, out string shareId)
    {
        peerId = "";
        shareId = "";
        for (var i = 0; i < args.Length - 2; i++)
        {
            if (!string.Equals(args[i], "--klasor", StringComparison.OrdinalIgnoreCase)) continue;
            peerId = args[i + 1];
            shareId = args[i + 2];
            return peerId.Length > 0 && shareId.Length > 0;
        }
        return false;
    }

    static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
