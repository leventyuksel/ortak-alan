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
    ChatWindow? _chat;
    FilesWindow? _files;
    EventWaitHandle? _commandWait;
    CancellationTokenSource? _commandStop;
    ThemeWindow? _theme;
    SettingsWindow? _settings;
    bool _demo;

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
        ThemeService.ApplySaved(Settings.Theme, Settings.Appearance, Settings.TextColor);
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
            WindowsShareService.RefreshAccess(dataDir, Settings.Shares);
        }

        Directory = new PeerDirectory();
        Chats = new ConversationStore(dataDir);
        _node = new PeerNode(Settings, Store, AskApproval);
        _node.PeersUpdated += list => Directory.Apply(list);
        _node.ChatReceived += OnChat;
        _node.FileReceived += OnFile;
        _node.Start();

        if (!_demo)
        {
            _tray = new TrayService(
                "Ortak Alan — " + Settings.ComputerName,
                ToggleChat,
                ShowShare,
                () => ShowChat(),
                ShowTheme,
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

    public void ShowChat(string? peerId = null)
    {
        _unread = 0;
        _tray?.SetUnread(0);
        _chat ??= new ChatWindow();
        if (_chat.WindowState == WindowState.Minimized)
            _chat.WindowState = WindowState.Normal;
        _chat.Show();
        _chat.Activate();
        if (!string.IsNullOrEmpty(peerId)) _chat.SelectPeer(peerId);
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
        var (ok, share, error) = WindowsShareService.TryShareFolder(path, name, item.Id);
        item.WindowsShare = ok ? share : null;
        item.WindowsError = ok ? null : error;
        Store.AddShare(item);
        _node?.Announce();
        return ok ? "Klasör paylaşıldı. Ağdaki bilgisayarlar görebilir." : "Klasör uygulama içinde açık. " + error;
    }

    public string AddPrinter(string printerName)
    {
        if (Store.CopyShares().Any(s => s.Kind == "printer" && string.Equals(s.Path, printerName, StringComparison.OrdinalIgnoreCase)))
            return "Bu yazıcı zaten paylaşımda.";

        var item = new ShareItem { Kind = "printer", Name = printerName, Path = printerName };
        var (ok, share, error) = WindowsShareService.TrySharePrinter(printerName, item.Id);
        item.WindowsShare = ok ? share : null;
        item.WindowsError = ok ? null : error;
        Store.AddShare(item);
        _node?.Announce();
        return ok ? "Yazıcı paylaşıldı." : "Yazıcı listede. " + error;
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
        Chats.Add(message.PeerId, new ChatLine { Mine = false, Text = message.Text, Utc = DateTime.Now });
        Notice(message.PeerId, message.PeerName, message.Text);
    }

    void OnFile(IncomingFile file)
    {
        Chats.Add(file.PeerId, new ChatLine
        {
            Mine = false,
            FileName = file.FileName,
            FilePath = file.Path,
            Text = file.FileName,
            Status = "Alındı",
            Utc = DateTime.Now
        });
        Notice(file.PeerId, file.PeerName, "Dosya: " + file.FileName);
    }

    void Notice(string peerId, string title, string text)
    {
        if (_chat is { IsVisible: true } && _chat.SelectedId == peerId) return;
        _unread++;
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
                    await ShareConnect.ConnectPrinter(peer, share);
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
