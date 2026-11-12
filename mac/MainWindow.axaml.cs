using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace OrtakAlan.Mac;

public partial class MainWindow : Window
{
    readonly Desk _desk;
    readonly MacNode _node;
    readonly List<Row> _rows = new();
    string _chat = MacNode.RoomId;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _desk = Desk.Open();
        }
        catch (Exception ex)
        {
            _desk = null!;
            _node = null!;
            Title = "Ortak Alan";
            StatusText.Text = ex.Message;
            Input.IsEnabled = false;
            Loaded += (_, _) => { };
            return;
        }

        _node = new MacNode(_desk);
        _node.PeersChanged += () => Dispatcher.UIThread.Post(RefreshPeople);
        _node.NoteArrived += (chat, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshPeople();
            if (chat == _chat) ShowChat();
        });
        _node.AskApproval = Ask;
        Loaded += async (_, _) => await Start();
        Closing += (_, _) => _node.Dispose();
    }

    async Task Start()
    {
        if (_desk.Profile.ComputerName.Length == 0)
        {
            var name = await AskText("Bu bilgisayarın adı", Environment.MachineName, "Adı kaydet");
            if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;
            _desk.Profile.ComputerName = name.Trim();
            _desk.Save();
        }
        Title = "Ortak Alan — " + _desk.Profile.ComputerName;
        SelfName.Text = _desk.Profile.ComputerName;
        ApplyLicense();
        _desk.SyncPrinters();
        _node.Start();
        MacPrint.Status += text => Dispatcher.UIThread.Post(() => StatusText.Text = text);
        MacPrint.Start(_node);
        RefreshPeople();
        ShowChat();
        if (!_desk.Usable) await OfferLicense();
    }

    void ApplyLicense()
    {
        LicenseText.Text = _desk.TrialLine();
        var on = _desk.Usable;
        Input.IsEnabled = on;
        ScreenButton.IsEnabled = on;
        FileButton.IsEnabled = on;
        SendButton.IsEnabled = on;
        ShareButton.IsEnabled = on;
        PrinterButton.IsEnabled = on;
        PrinterRemoveButton.IsEnabled = on;
        if (!on) StatusText.Text = "7 günlük kullanım süresi doldu. Lisans girilmeden sohbet, dosya, ekran alıntısı ve yazıcı çalışmaz.";
    }

    async Task OfferLicense()
    {
        var key = await AskText("Lisans anahtarı", "", "Kaydet");
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!_desk.TryAccept(key, out var error))
        {
            StatusText.Text = error;
            return;
        }
        ApplyLicense();
        StatusText.Text = "Lisans kaydedildi.";
    }

    void RefreshPeople()
    {
        var selected = (People.SelectedItem as Row)?.Id ?? _chat;
        _rows.Clear();
        _rows.Add(new Row { Id = MacNode.RoomId, Title = "Ortak konuşma", Subtitle = "Ağdaki herkes" });
        foreach (var peer in _node.Peers())
        {
            if (_desk.Profile.Hidden.Any(p => p.Id == peer.Id)) continue;
            var unread = 0;
            _rows.Add(new Row
            {
                Id = peer.Id,
                Title = peer.Name,
                Subtitle = peer.IsOnline ? "Çevrimiçi" + (unread > 0 ? " · " + unread : "") : "Çevrimdışı"
            });
        }
        People.ItemsSource = null;
        People.ItemsSource = _rows.ToList();
        People.SelectedItem = _rows.FirstOrDefault(row => row.Id == selected) ?? _rows[0];
    }

    void ShowChat()
    {
        var room = _chat == MacNode.RoomId;
        ChatTitle.Text = room ? "Ortak konuşma" : _node.Peers().FirstOrDefault(p => p.Id == _chat)?.Name ?? "Sohbet";
        var lines = _node.Messages(_chat).Select(note => new Bubble
        {
            Author = note.Author,
            ShowAuthor = room && !note.Mine && note.Author.Length > 0,
            Body = (note.Struck ? "silindi — " : "") + (note.FileName.Length > 0 ? note.FileName : note.Text),
            Align = note.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        }).ToList();
        Transcript.ItemsSource = lines;
        if (Transcript.ItemCount > 0) Transcript.ScrollIntoView(Transcript.ItemCount - 1);
    }

    async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var text = Input.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        Input.Text = "";
        StatusText.Text = _chat == MacNode.RoomId
            ? await _node.SendRoom(text) ?? ""
            : await _node.SendChat(_chat, text) ?? "";
        ShowChat();
    }

    async void File_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Gönderilecek dosya", AllowMultiple = false });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = "Dosya yolu alınamadı.";
            return;
        }
        StatusText.Text = _chat == MacNode.RoomId
            ? await _node.SendFile("", path, MacNode.RoomId) ?? "Dosya ortak konuşmaya gönderildi."
            : await _node.SendFile(_chat, path, null) ?? "Dosya gönderildi.";
        ShowChat();
    }

    async void Screen_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var path = Path.Combine(Path.GetTempPath(), "ortakalan-ekran-" + Guid.NewGuid().ToString("N") + ".png");
        Hide();
        string? error;
        try
        {
            error = await GrabScreen(path);
        }
        finally
        {
            Show();
            Activate();
        }
        if (error != null)
        {
            StatusText.Text = error;
            TryDelete(path);
            return;
        }
        if (!File.Exists(path) || new FileInfo(path).Length < 32)
            return;

        var editor = new ShotWindow(path);
        await editor.ShowDialog(this);
        if (string.IsNullOrEmpty(editor.OutputPath))
        {
            TryDelete(path);
            return;
        }
        if (!editor.SendToChat)
        {
            StatusText.Text = "Kaydedildi.";
            TryDelete(path);
            return;
        }
        StatusText.Text = _chat == MacNode.RoomId
            ? await _node.SendFile("", editor.OutputPath, MacNode.RoomId) ?? "Ekran alıntısı ortak konuşmaya gönderildi."
            : await _node.SendFile(_chat, editor.OutputPath, null) ?? "Ekran alıntısı gönderildi.";
        ShowChat();
        TryDelete(path);
    }

    static async Task<string?> GrabScreen(string path)
    {
        var start = new ProcessStartInfo("/usr/sbin/screencapture")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("-x");
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("png");
        start.ArgumentList.Add(path);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return "Ekran alıntısı alınamadı.";
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (File.Exists(path) && new FileInfo(path).Length >= 32) return null;
            if (error.Trim().Length == 0) return null;
            return "Ekran kaydı izni gerekli. Sistem Ayarları, Gizlilik ve Güvenlik, Ekran Kaydı bölümünden Ortak Alan’a izin verin, sonra uygulamayı kapatıp açın.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* geçici dosya */ }
    }

    async void Printer_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        if (_chat == MacNode.RoomId)
        {
            StatusText.Text = "Önce listeden yazıcısı olan bilgisayarı seçin.";
            return;
        }
        var peer = _node.Peers().FirstOrDefault(p => p.Id == _chat);
        var printers = peer?.Shares.Where(s => s.Kind == "printer").ToList() ?? new List<RemoteShare>();
        if (peer == null || printers.Count == 0)
        {
            StatusText.Text = "Bu bilgisayar henüz yazıcı paylaşmıyor.";
            return;
        }
        var chosen = printers[0];
        if (printers.Count > 1)
        {
            var pick = new PickWindow(printers.Select(s => s.Name).ToList());
            await pick.ShowDialog(this);
            if (pick.Index < 0) return;
            chosen = printers[pick.Index];
        }
        if (MacPrint.Has(peer.Id, chosen.Id))
        {
            StatusText.Text = "\"" + chosen.Name + "\" zaten yazıcılar listesinde.";
            return;
        }
        StatusText.Text = "macOS yönetici şifresi isteyecek.";
        StatusText.Text = await MacPrint.AddQueue(peer.Id, peer.Name, chosen.Id, chosen.Name);
    }

    async void PrinterRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || !_desk.Usable) return;
        if (_chat == MacNode.RoomId)
        {
            StatusText.Text = "Önce listeden yazıcısı olan bilgisayarı seçin.";
            return;
        }
        var peer = _node.Peers().FirstOrDefault(p => p.Id == _chat);
        var printers = peer?.Shares.Where(s => s.Kind == "printer" && MacPrint.Has(peer.Id, s.Id)).ToList() ?? new List<RemoteShare>();
        if (peer == null || printers.Count == 0)
        {
            StatusText.Text = "Bu bilgisayarın bağlı yazıcısı yok.";
            return;
        }
        var chosen = printers[0];
        if (printers.Count > 1)
        {
            var pick = new PickWindow(printers.Select(s => s.Name).ToList());
            await pick.ShowDialog(this);
            if (pick.Index < 0) return;
            chosen = printers[pick.Index];
        }
        if (!await Confirm("\"" + chosen.Name + "\" yazıcılar listesinden kaldırılsın mı?")) return;
        StatusText.Text = "macOS yönetici şifresi isteyecek.";
        StatusText.Text = await MacPrint.RemoveQueue(peer.Id, chosen.Id);
    }

    async void Share_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var choice = await Choose("Ne paylaşılsın?", "Klasör", "Yazıcı");
        if (choice == 1)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Paylaşılacak klasör", AllowMultiple = false });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Length == 0) name = path;
            _desk.AddShare(new Share { Kind = "folder", Name = name, Path = path });
            StatusText.Text = "Klasör paylaşıldı. Ağdaki bilgisayarlar görebilir.";
        }
        else if (choice == 2)
        {
            var added = _desk.SyncPrinters();
            var total = _desk.Shares().Count(s => s.Kind == "printer");
            StatusText.Text = total == 0
                ? "Bu Mac’te yazıcı bulunamadı. USB yazıcı takılı ve macOS yazıcı listesinde görünüyor olmalı."
                : (added > 0 ? added + " yazıcı paylaşıldı. " : "Yazıcılar zaten paylaşılıyor. ") + "Windows bilgisayarlar kendi yazıcı listesine ekleyebilir.";
        }
    }

    async void License_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var key = await AskText("Lisans anahtarı", "", "Kaydet");
        if (key == null) return;
        if (!_desk.TryAccept(key, out var error))
        {
            StatusText.Text = error;
            return;
        }
        ApplyLicense();
        StatusText.Text = "Lisans kaydedildi.";
    }

    void People_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_node == null || People.SelectedItem is not Row row) return;
        _chat = row.Id;
        ShowChat();
        PrinterRemoveButton.IsEnabled = _desk.Usable && row.Id != MacNode.RoomId;
        StatusText.Text = row.Id == MacNode.RoomId
            ? "Buraya yazılanı ağdaki herkes görür."
            : "";
    }

    async Task<bool?> Ask(string id, string name)
    {
        var window = new ConfirmWindow(name + " bağlanmak istiyor. İzin verilsin mi?");
        await window.ShowDialog(this);
        return window.Answer;
    }

    async Task<string?> AskText(string title, string value, string ok)
    {
        var window = new TextWindow(title, value, ok);
        await window.ShowDialog(this);
        return window.Value;
    }

    async Task<bool> Confirm(string text)
    {
        var window = new ConfirmWindow(text);
        await window.ShowDialog(this);
        return window.Answer == true;
    }

    async Task<int> Choose(string text, string left, string right)
    {
        var window = new ChoiceWindow(text, left, right);
        await window.ShowDialog(this);
        return window.Choice;
    }

    sealed class Row
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
    }

    sealed class Bubble
    {
        public string Author { get; set; } = "";
        public bool ShowAuthor { get; set; }
        public string Body { get; set; } = "";
        public HorizontalAlignment Align { get; set; }
    }
}

sealed class TextWindow : Window
{
    readonly TextBox _box = new() { Watermark = "Yazın", MinWidth = 360 };
    public string? Value { get; private set; }

    public TextWindow(string title, string value, string ok)
    {
        Title = "Ortak Alan";
        Width = 440;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0E1A2B"));
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16 });
        _box.Text = value;
        panel.Children.Add(_box);
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = ok };
        save.Click += (_, _) => { Value = _box.Text; Close(); };
        var cancel = new Button { Content = "Vazgeç" };
        cancel.Click += (_, _) => { Value = null; Close(); };
        row.Children.Add(cancel);
        row.Children.Add(save);
        panel.Children.Add(row);
        Content = panel;
    }
}

sealed class ConfirmWindow : Window
{
    public bool? Answer { get; private set; }

    public ConfirmWindow(string text)
    {
        Title = "Ortak Alan";
        Width = 440;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0E1A2B"));
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = new Button { Content = "Evet" };
        yes.Click += (_, _) => { Answer = true; Close(); };
        var no = new Button { Content = "Hayır" };
        no.Click += (_, _) => { Answer = false; Close(); };
        row.Children.Add(no);
        row.Children.Add(yes);
        panel.Children.Add(row);
        Content = panel;
    }
}

sealed class ChoiceWindow : Window
{
    public int Choice { get; private set; }

    public ChoiceWindow(string text, string left, string right)
    {
        Title = "Ortak Alan";
        Width = 440;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0E1A2B"));
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var a = new Button { Content = left };
        a.Click += (_, _) => { Choice = 1; Close(); };
        var b = new Button { Content = right };
        b.Click += (_, _) => { Choice = 2; Close(); };
        row.Children.Add(a);
        row.Children.Add(b);
        panel.Children.Add(row);
        Content = panel;
    }
}

sealed class PickWindow : Window
{
    public int Index { get; private set; } = -1;

    public PickWindow(IReadOnlyList<string> names)
    {
        Title = "Ortak Alan";
        Width = 440;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0E1A2B"));
        var list = new ListBox { ItemsSource = names, MinHeight = 160, Background = new SolidColorBrush(Color.Parse("#122033")), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Hangi yazıcı eklensin?", Foreground = Brushes.White, FontSize = 16 });
        panel.Children.Add(list);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Ekle" };
        ok.Click += (_, _) =>
        {
            Index = list.SelectedIndex;
            if (Index >= 0) Close();
        };
        var cancel = new Button { Content = "Vazgeç" };
        cancel.Click += (_, _) => Close();
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);
        Content = panel;
    }
}
