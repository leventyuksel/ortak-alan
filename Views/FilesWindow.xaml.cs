using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrtakAlan.Views;

public partial class FilesWindow : ChromeWindow
{
    readonly List<Place> _history = new();
    Place _place = new();
    List<ClipItem>? _clip;
    bool _cut;
    bool _busy;
    bool _force = true;
    string _sig = "";
    int _load;
    bool _press;
    Point _pressAt;
    bool _dragBusy;
    uint _clipSeq;
    readonly List<Place> _ahead = new();
    List<FileRow> _loaded = new();
    string _query = "";
    string _sort = "ad";
    bool _icons = true;
    bool _navLock;

    public FilesWindow()
    {
        InitializeComponent();
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        UseLogo(LogoImage);
        ApplyChrome();
        App.Me.Directory.Changed += OnDirectory;
        Closed += (_, _) => App.Me.Directory.Changed -= OnDirectory;
        _ = Load();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = ExplorerChrome.Light ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        catch
        {
            // Başlık çubuğu sistem renginde kalır.
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void OpenShare(string peerId, string peerName, string shareId, string shareName)
    {
        _history.Clear();
        _history.Add(new Place());
        _history.Add(new Place { Kind = "pc", Title = peerName, PeerId = peerId, PeerName = peerName });
        _place = new Place
        {
            Kind = "remote",
            Title = shareName,
            PeerId = peerId,
            PeerName = peerName,
            ShareId = shareId,
            ShareName = shareName
        };
        _force = true;
        _ = Load();
    }

    void OnDirectory()
    {
        if (_busy || _place.Kind is not ("root" or "pc")) return;
        Dispatcher.BeginInvoke(() => _ = Load());
    }

    async Task Load()
    {
        var ticket = ++_load;
        var (error, rows) = await Fetch();
        if (ticket != _load) return;
        var sig = string.Join("|", rows.Select(row => row.Kind + "\t" + row.Name + "\t" + row.Size + "\t" + row.WhenText));
        Title = string.IsNullOrEmpty(_place.Title) ? "Paylaşılan Dosyalar" : _place.Title;
        Crumbs.ItemsSource = BuildCrumbs();
        FillNav();
        BackButton.IsEnabled = _history.Count > 0;
        ForwardButton.IsEnabled = _ahead.Count > 0;
        if (!_force && sig == _sig && _query.Length == 0) return;
        _force = false;
        _sig = sig;
        _loaded = rows;
        Present();
        if (error != null) StatusText.Text = error;
    }

    void Present()
    {
        var keep = SelectedItems.Cast<FileRow>().Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Arrange(_loaded).ToList();
        Icons.ItemsSource = list;
        Rows.ItemsSource = list;
        foreach (FileRow row in Sheet.Items)
        {
            if (keep.Contains(row.Name)) SelectedItems.Add(row);
        }
        StatusText.Text = list.Count + " öğe";
    }

    IEnumerable<FileRow> Arrange(IEnumerable<FileRow> rows)
    {
        if (_query.Length > 0)
            rows = rows.Where(row => row.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));
        return _sort switch
        {
            "tarih" => rows.OrderByDescending(row => row.When),
            "tur" => rows.OrderBy(row => row.TypeText, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
            "boyut" => rows.OrderByDescending(row => row.Size),
            _ => rows.OrderByDescending(row => row.IsDir).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    void FillNav()
    {
        var items = new List<NavRow>
        {
            new()
            {
                Title = "Paylaşılan Dosyalar",
                Icon = FolderArt.Create(FolderArt.Tiffany),
                Place = new Place()
            },
            new()
            {
                Title = "Gelen",
                Icon = IconOf("Gelen", true, false, "y|" + SharedFiles.Gelen.ToLowerInvariant()),
                Place = new Place { Kind = "local", Title = "Gelen", LocalPath = SharedFiles.Gelen }
            }
        };
        foreach (var peer in App.Me.Directory.Peers.OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            items.Add(new NavRow
            {
                Title = peer.Name,
                Icon = ExplorerIcons.For(peer.Name, true, true),
                Place = new Place { Kind = "pc", Title = peer.Name, PeerId = peer.Id, PeerName = peer.Name }
            });
        }
        _navLock = true;
        Nav.ItemsSource = items;
        Nav.SelectedItem = items.FirstOrDefault(item => Same(item.Place, _place));
        _navLock = false;
    }

    async Task<(string? Error, List<FileRow> Rows)> Fetch()
    {
        try { SharedFiles.Prepare(); }
        catch (Exception ex) { Log.Write(ex); }

        if (_place.Kind == "pc")
        {
            var peer = App.Me.Directory.Peers.FirstOrDefault(item => item.Id == _place.PeerId);
            if (peer == null) return ("Bilgisayar bulunamadı", new());
            var shares = peer.Shares.Where(share => share.Kind == "folder").OrderBy(share => share.Name).Select(share => ShareRow(peer, share)).ToList();
            return (null, shares);
        }

        if (_place.Kind == "remote")
        {
            var (error, entries) = await App.Me.Node.ListAsync(_place.PeerId, _place.ShareId, _place.Rel);
            if (error != null) return (error, new());
            var rows = entries.OrderByDescending(entry => entry.IsDir).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(EntryRow).ToList();
            return (null, rows);
        }

        if (_place.Kind == "local")
            return LocalRows(_place.LocalPath);

        return (null, RootRows());
    }

    List<FileRow> RootRows()
    {
        var rows = App.Me.Directory.Peers
            .OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(peer => new FileRow
            {
                Name = peer.Name,
                Glyph = "\uE977",
                Icon = ExplorerIcons.For(peer.Name, true, true),
                TypeText = !peer.Online ? "Çevrimdışı" : TailscalePeers.IsAddress(peer.Ip) ? "Tailscale" : "Bilgisayar",
                Kind = "pc",
                IsDir = true,
                PeerId = peer.Id,
                PeerName = peer.Name
            }).ToList();
        rows.Add(new FileRow
        {
            Name = "Gelen",
            Glyph = "\uE8B7",
            Icon = IconOf("Gelen", true, false, "y|" + SharedFiles.Gelen.ToLowerInvariant()),
            TypeText = "Gelen dosyalar",
            Kind = "gelen",
            IsDir = true,
            LocalPath = SharedFiles.Gelen
        });
        return rows;
    }

    static FileRow ShareRow(PeerVm peer, RemoteShareVm share) => new()
    {
        Name = share.Name,
        Glyph = "\uE8B7",
        Icon = IconOf(share.Name, true, false, "u|" + peer.Id + "|" + share.Id + "|"),
        TypeText = "Paylaşılan klasör",
        Kind = "share",
        IsDir = true,
        PeerId = peer.Id,
        PeerName = peer.Name,
        ShareId = share.Id,
        ShareName = share.Name
    };

    FileRow EntryRow(DirEntry entry) => new()
    {
        Name = entry.Name,
        Glyph = entry.IsDir ? "\uE8B7" : "\uE8A5",
        Icon = IconOf(entry.Name, entry.IsDir, false, entry.IsDir ? "u|" + _place.PeerId + "|" + _place.ShareId + "|" + Join(_place.Rel, entry.Name) : ""),
        TypeText = entry.IsDir ? "Klasör" : TypeName(entry.Name),
        SizeText = entry.IsDir ? "" : SizeOf(entry.Size),
        WhenText = entry.WhenText,
        When = entry.Modified > 0 ? new DateTime(entry.Modified, DateTimeKind.Utc).ToLocalTime() : default,
        Size = entry.Size,
        IsDir = entry.IsDir,
        Kind = entry.IsDir ? "dir" : "file",
        PeerId = _place.PeerId,
        PeerName = _place.PeerName,
        ShareId = _place.ShareId,
        ShareName = _place.ShareName,
        Rel = Join(_place.Rel, entry.Name)
    };

    static (string? Error, List<FileRow> Rows) LocalRows(string path)
    {
        if (!Directory.Exists(path)) return ("Klasör bulunamadı", new());
        var rows = new List<FileRow>();
        foreach (var dir in new DirectoryInfo(path).EnumerateDirectories().OrderBy(dir => dir.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Skip(dir)) continue;
            rows.Add(LocalRow(dir.Name, dir.FullName, true, 0, dir.LastWriteTime));
        }
        foreach (var file in new DirectoryInfo(path).EnumerateFiles().OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Skip(file)) continue;
            rows.Add(LocalRow(file.Name, file.FullName, false, file.Length, file.LastWriteTime));
        }
        return (null, rows);
    }

    static FileRow LocalRow(string name, string path, bool dir, long size, DateTime when) => new()
    {
        Name = name,
        Glyph = dir ? "\uE8B7" : "\uE8A5",
        Icon = IconOf(name, dir, false, dir ? "y|" + path.ToLowerInvariant() : ""),
        TypeText = dir ? "Klasör" : TypeName(name),
        SizeText = dir ? "" : SizeOf(size),
        WhenText = when == default ? "" : when.ToString("dd.MM.yyyy HH:mm"),
        When = when,
        Size = size,
        IsDir = dir,
        Kind = dir ? "dir" : "file",
        LocalPath = path
    };

    List<Crumb> BuildCrumbs()
    {
        var list = new List<Crumb> { new() { Title = "Paylaşılan Dosyalar", Place = new Place() } };
        if (_place.Kind is "pc" or "remote")
        {
            list.Add(new Crumb
            {
                Title = _place.PeerName,
                ShowSep = true,
                Place = new Place { Kind = "pc", Title = _place.PeerName, PeerId = _place.PeerId, PeerName = _place.PeerName }
            });
        }
        if (_place.Kind == "remote")
        {
            list.Add(new Crumb
            {
                Title = _place.ShareName,
                ShowSep = true,
                Place = new Place
                {
                    Kind = "remote",
                    Title = _place.ShareName,
                    PeerId = _place.PeerId,
                    PeerName = _place.PeerName,
                    ShareId = _place.ShareId,
                    ShareName = _place.ShareName
                }
            });
            var rel = "";
            foreach (var part in _place.Rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                rel = Join(rel, part);
                list.Add(new Crumb
                {
                    Title = part,
                    ShowSep = true,
                    Place = new Place
                    {
                        Kind = "remote",
                        Title = part,
                        PeerId = _place.PeerId,
                        PeerName = _place.PeerName,
                        ShareId = _place.ShareId,
                        ShareName = _place.ShareName,
                        Rel = rel
                    }
                });
            }
        }
        if (_place.Kind == "local")
        {
            var root = SharedFiles.Gelen.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            list.Add(new Crumb
            {
                Title = "Gelen",
                ShowSep = true,
                Place = new Place { Kind = "local", Title = "Gelen", LocalPath = SharedFiles.Gelen }
            });
            var current = _place.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (current.StartsWith(root, StringComparison.OrdinalIgnoreCase) && current.Length > root.Length)
            {
                var acc = root;
                foreach (var part in current[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    acc = Path.Combine(acc, part);
                    list.Add(new Crumb
                    {
                        Title = part,
                        ShowSep = true,
                        Place = new Place { Kind = "local", Title = part, LocalPath = acc }
                    });
                }
            }
        }
        return list;
    }

    void Go(Place place)
    {
        if (_busy) return;
        _ahead.Clear();
        _history.Add(_place);
        if (_history.Count > 40) _history.RemoveAt(0);
        _place = place;
        _force = true;
        _ = Load();
    }

    void Back_Click(object sender, RoutedEventArgs e) => Back();

    void Forward_Click(object sender, RoutedEventArgs e) => Forward();

    void Back()
    {
        if (_busy || _history.Count == 0) return;
        _ahead.Add(_place);
        _place = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _force = true;
        _ = Load();
    }

    void Forward()
    {
        if (_busy || _ahead.Count == 0) return;
        _history.Add(_place);
        _place = _ahead[^1];
        _ahead.RemoveAt(_ahead.Count - 1);
        _force = true;
        _ = Load();
    }

    void Up_Click(object sender, RoutedEventArgs e) => Up();

    void Up()
    {
        if (_busy || _place.Kind == "root") return;
        if (_place.Kind == "pc")
        {
            Go(new Place());
            return;
        }
        if (_place.Kind == "local")
        {
            var root = SharedFiles.Gelen.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = _place.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                Go(new Place());
            else
            {
                var parent = Path.GetDirectoryName(current) ?? root;
                Go(new Place { Kind = "local", Title = Path.GetFileName(parent), LocalPath = parent });
            }
            return;
        }
        if (_place.Rel.Length == 0)
        {
            Go(new Place { Kind = "pc", Title = _place.PeerName, PeerId = _place.PeerId, PeerName = _place.PeerName });
            return;
        }
        var parentRel = ParentRel(_place.Rel);
        var title = parentRel.Length == 0 ? _place.ShareName : parentRel[(parentRel.LastIndexOf('/') + 1)..];
        Go(new Place
        {
            Kind = "remote",
            Title = title,
            PeerId = _place.PeerId,
            PeerName = _place.PeerName,
            ShareId = _place.ShareId,
            ShareName = _place.ShareName,
            Rel = parentRel
        });
    }

    void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not Crumb crumb) return;
        if (Same(_place, crumb.Place)) return;
        Go(crumb.Place);
    }

    async void Rows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is not FileRow row) return;
        await OpenRow(row);
    }

    async Task OpenRow(FileRow row)
    {
        if (_busy) return;
        if (row.Kind == "pc")
        {
            Go(new Place { Kind = "pc", Title = row.Name, PeerId = row.PeerId, PeerName = row.PeerName });
            return;
        }
        if (row.Kind is "share")
        {
            Go(new Place
            {
                Kind = "remote",
                Title = row.Name,
                PeerId = row.PeerId,
                PeerName = row.PeerName,
                ShareId = row.ShareId,
                ShareName = row.Name
            });
            return;
        }
        if (row.Kind == "gelen" || (row.IsDir && row.LocalPath.Length > 0 && _place.Kind != "remote"))
        {
            Go(new Place { Kind = "local", Title = row.Name, LocalPath = row.LocalPath });
            return;
        }
        if (row.IsDir && _place.Kind == "remote")
        {
            Go(new Place
            {
                Kind = "remote",
                Title = row.Name,
                PeerId = _place.PeerId,
                PeerName = _place.PeerName,
                ShareId = _place.ShareId,
                ShareName = _place.ShareName,
                Rel = row.Rel
            });
            return;
        }
        if (row.IsDir && row.LocalPath.Length > 0)
        {
            Go(new Place { Kind = "local", Title = row.Name, LocalPath = row.LocalPath });
            return;
        }
        await OpenFile(row);
    }

    async Task OpenFile(FileRow row)
    {
        try
        {
            if (row.LocalPath.Length > 0 && File.Exists(row.LocalPath))
            {
                Process.Start(new ProcessStartInfo(row.LocalPath) { UseShellExecute = true });
                return;
            }
            StatusText.Text = "Açılıyor…";
            var temp = Path.Combine(Path.GetTempPath(), "OrtakAlan", "ac");
            Directory.CreateDirectory(temp);
            var dest = Unique(temp, row.Name);
            var (error, path) = await App.Me.Node.DownloadToAsync(row.PeerId, row.ShareId, row.Rel, row.Name, dest);
            if (error != null || string.IsNullOrEmpty(path))
            {
                StatusText.Text = error ?? "Dosya alınamadı";
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusText.Text = Rows.Items.Count + " öğe";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Dosya açılamadı.";
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Remember(false);
    void Cut_Click(object sender, RoutedEventArgs e) => Remember(true);

    void Remember(bool cut)
    {
        var rows = Selected().Where(row => cut ? row.Kind is "file" or "dir" : row.Kind != "pc").ToList();
        if (rows.Count == 0)
        {
            StatusText.Text = cut ? "Kesmek için bir dosya veya klasör seçin." : "Önce bir öğe seçin.";
            return;
        }
        _clip = rows.Select(ToClip).ToList();
        _cut = cut;
        try
        {
            _clipSeq = ShellDrag.CopyToClipboard(rows.Select(ToNode).ToList(), cut, cut ? () => RemoveAfterMove(rows) : null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Panoya alınamadı.";
            return;
        }
        StatusText.Text = cut ? "Kesildi. Masaüstüne yapıştırınca taşınır." : "Kopyalandı. Masaüstüne yapıştırabilirsiniz.";
    }

    async void Paste_Click(object sender, RoutedEventArgs e) => await Paste();

    async Task Paste()
    {
        if (_place.Kind is not ("local" or "remote"))
        {
            StatusText.Text = "Bu konuma yapıştırılamaz.";
            return;
        }
        try
        {
            if (_clip is { Count: > 0 } && ShellDrag.CurrentSequence() == _clipSeq)
            {
                await PasteItems(_clip.ToList(), _cut);
                return;
            }
            var data = Clipboard.GetDataObject();
            if (data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await PasteItems(files.Select(PathItem).ToList(), false);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        StatusText.Text = "Yapıştırılacak öğe yok.";
    }

    async Task<bool> PasteItems(List<ClipItem> items, bool cut)
    {
        if (_busy) return false;
        if (_place.Kind is not ("local" or "remote"))
        {
            StatusText.Text = "Bu konuma yapıştırılamaz.";
            return false;
        }
        foreach (var item in items)
        {
            if (InsideSelf(item))
            {
                StatusText.Text = "Klasör kendi içine kopyalanamaz.";
                return false;
            }
        }

        SetBusy(true);
        try
        {
            var names = Names();
            var replace = false;
            var asked = false;
            foreach (var item in items)
            {
                var same = SameFolder(item);
                string destName;
                if (same && cut) continue;
                if (same)
                    destName = CopyName(item.Name, item.IsDir, names.Contains);
                else if (names.Contains(item.Name))
                {
                    if (!asked)
                    {
                        var answer = MessageBox.Show(this, "Aynı adda öğeler var. Değiştirilsin mi?", "Paylaşılan Dosyalar", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        replace = answer == MessageBoxResult.Yes;
                        asked = true;
                    }
                    if (!replace) continue;
                    destName = item.Name;
                    var removed = await RemoveDest(destName);
                    if (removed != null)
                    {
                        StatusText.Text = removed;
                        return false;
                    }
                }
                else destName = item.Name;

                StatusText.Text = (cut ? "Taşınıyor: " : "Kopyalanıyor: ") + item.Name;
                var sameShare = item.LocalPath.Length == 0 && _place.Kind == "remote" && item.PeerId == _place.PeerId && item.ShareId == _place.ShareId;
                var error = await Transfer(item, destName);
                if (error != null)
                {
                    StatusText.Text = error;
                    return false;
                }
                if (cut && !sameShare)
                {
                    error = await DeleteSource(item);
                    if (error != null)
                    {
                        StatusText.Text = error;
                        return false;
                    }
                }
                names.Add(destName);
            }
            if (cut)
            {
                _clip = null;
                _cut = false;
            }
            StatusText.Text = "Tamam";
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Yapıştırılamadı.";
            return false;
        }
        finally
        {
            SetBusy(false);
            _force = true;
            await Load();
        }
    }

    async Task<string?> Transfer(ClipItem item, string destName)
    {
        if (_place.Kind == "local")
        {
            var dest = Path.Combine(_place.LocalPath, destName);
            if (item.LocalPath.Length > 0) return CopyLocal(item.LocalPath, dest, item.IsDir);
            return await SaveRemote(item, dest);
        }
        if (item.LocalPath.Length == 0 && item.PeerId == _place.PeerId && item.ShareId == _place.ShareId)
        {
            var to = Join(_place.Rel, destName);
            return _cut
                ? await App.Me.Node.MoveAsync(item.PeerId, item.ShareId, item.Rel, to)
                : await App.Me.Node.CopyAsync(item.PeerId, item.ShareId, item.Rel, to);
        }
        if (item.LocalPath.Length > 0)
            return await UploadTree(item.LocalPath, _place.Rel, destName, item.IsDir);
        var temp = Path.Combine(Path.GetTempPath(), "ortakalan-kop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var staged = Path.Combine(temp, destName);
            var error = await SaveRemote(item, staged);
            if (error != null) return error;
            return await UploadTree(staged, _place.Rel, destName, item.IsDir);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* geçici */ }
        }
    }

    static string? CopyLocal(string source, string dest, bool dir)
    {
        try
        {
            if (dir)
            {
                CopyTree(source, dest);
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, true);
            return null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "Kopyalanamadı.";
        }
    }

    static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    async Task<string?> SaveRemote(ClipItem item, string dest)
    {
        if (!item.IsDir)
        {
            var (error, _) = await App.Me.Node.DownloadToAsync(item.PeerId, item.ShareId, item.Rel, item.Name, dest);
            return error;
        }
        Directory.CreateDirectory(dest);
        var (listError, entries) = await App.Me.Node.ListAsync(item.PeerId, item.ShareId, item.Rel);
        if (listError != null) return listError;
        foreach (var entry in entries)
        {
            var child = new ClipItem
            {
                Name = entry.Name,
                IsDir = entry.IsDir,
                PeerId = item.PeerId,
                ShareId = item.ShareId,
                Rel = Join(item.Rel, entry.Name)
            };
            var error = await SaveRemote(child, Path.Combine(dest, entry.Name));
            if (error != null) return error;
        }
        return null;
    }

    async Task<string?> UploadTree(string source, string parentRel, string destName, bool dir)
    {
        if (!dir)
        {
            var path = source;
            string? temp = null;
            if (!string.Equals(Path.GetFileName(source), destName, StringComparison.OrdinalIgnoreCase))
            {
                temp = Path.Combine(Path.GetTempPath(), "ortakalan-ad-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                path = Path.Combine(temp, destName);
                File.Copy(source, path);
            }
            try
            {
                return await App.Me.Node.UploadAsync(_place.PeerId, _place.ShareId, parentRel, path);
            }
            finally
            {
                if (temp != null)
                {
                    try { Directory.Delete(temp, true); } catch { /* geçici */ }
                }
            }
        }

        var error = await App.Me.Node.MakeDirAsync(_place.PeerId, _place.ShareId, parentRel, destName);
        if (error != null) return error;
        var rel = Join(parentRel, destName);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            error = await UploadTree(file, rel, Path.GetFileName(file), false);
            if (error != null) return error;
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            error = await UploadTree(folder, rel, Path.GetFileName(folder), true);
            if (error != null) return error;
        }
        return null;
    }

    async Task<string?> RemoveDest(string name)
    {
        try
        {
            if (_place.Kind == "local")
            {
                var path = Path.Combine(_place.LocalPath, name);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                return null;
            }
            return await App.Me.Node.DeleteAsync(_place.PeerId, _place.ShareId, Join(_place.Rel, name));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "Var olan öğe kaldırılamadı.";
        }
    }

    static async Task<string?> DeleteSource(ClipItem item)
    {
        try
        {
            if (item.LocalPath.Length > 0)
            {
                if (item.IsDir) Directory.Delete(item.LocalPath, true);
                else File.Delete(item.LocalPath);
                return null;
            }
            return await App.Me.Node.DeleteAsync(item.PeerId, item.ShareId, item.Rel);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "Kaynak silinemedi.";
        }
    }

    async void Delete_Click(object sender, RoutedEventArgs e) => await Delete();

    async Task Delete()
    {
        var rows = Selected().Where(row => row.Kind is "file" or "dir").ToList();
        if (rows.Count == 0)
        {
            StatusText.Text = "Silinecek dosya veya klasör seçin.";
            return;
        }
        var answer = MessageBox.Show(this, rows.Count == 1 ? "\"" + rows[0].Name + "\" silinsin mi?" : rows.Count + " öğe silinsin mi?", "Paylaşılan Dosyalar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            foreach (var row in rows)
            {
                StatusText.Text = "Siliniyor: " + row.Name;
                var error = await DeleteSource(ToClip(row));
                if (error != null)
                {
                    StatusText.Text = error;
                    return;
                }
            }
            StatusText.Text = "Silindi.";
        }
        finally
        {
            SetBusy(false);
            _force = true;
            await Load();
        }
    }

    async void Rename_Click(object sender, RoutedEventArgs e) => await Rename();

    async Task Rename()
    {
        var rows = Selected();
        if (rows.Count != 1 || rows[0].Kind is not ("file" or "dir"))
        {
            StatusText.Text = "Yeniden adlandırmak için bir dosya veya klasör seçin.";
            return;
        }
        var row = rows[0];
        var name = Ask("Yeniden adlandır", "Yeni ad", row.Name, selectStem: !row.IsDir);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, row.Name, StringComparison.OrdinalIgnoreCase)) return;
        if (!SafeName(name))
        {
            StatusText.Text = "Ad geçersiz.";
            return;
        }
        SetBusy(true);
        try
        {
            string? error;
            if (row.LocalPath.Length > 0)
            {
                var dest = Path.Combine(Path.GetDirectoryName(row.LocalPath)!, name);
                if (File.Exists(dest) || Directory.Exists(dest)) error = "Bu ad kullanılıyor";
                else
                {
                    if (row.IsDir) Directory.Move(row.LocalPath, dest);
                    else File.Move(row.LocalPath, dest);
                    error = null;
                }
            }
            else error = await App.Me.Node.RenameAsync(row.PeerId, row.ShareId, row.Rel, name);
            StatusText.Text = error ?? "Ad değiştirildi.";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Ad değiştirilemedi.";
        }
        finally
        {
            SetBusy(false);
            _force = true;
            await Load();
        }
    }

    async void New_Click(object sender, RoutedEventArgs e) => await NewFolder();

    async Task NewFolder()
    {
        if (_place.Kind is not ("local" or "remote"))
        {
            StatusText.Text = "Bu konumda klasör oluşturulamaz.";
            return;
        }
        var name = Ask("Yeni klasör", "Klasör adı", FreeName("Yeni klasör"), selectStem: false);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!SafeName(name))
        {
            StatusText.Text = "Ad geçersiz.";
            return;
        }
        SetBusy(true);
        try
        {
            string? error = null;
            if (_place.Kind == "local")
            {
                var path = Path.Combine(_place.LocalPath, name);
                if (File.Exists(path) || Directory.Exists(path)) error = "Bu ad kullanılıyor";
                else Directory.CreateDirectory(path);
            }
            else error = await App.Me.Node.MakeDirAsync(_place.PeerId, _place.ShareId, _place.Rel, name);
            StatusText.Text = error ?? "Klasör oluşturuldu.";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = "Klasör oluşturulamadı.";
        }
        finally
        {
            SetBusy(false);
            _force = true;
            await Load();
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _force = true;
        _ = Load();
    }

    void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        var count = SelectedItems.Count;
        StatusText.Text = count == 0 ? Sheet.Items.Count + " öğe" : count + " öğe seçili";
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { Remember(false); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X) { Remember(true); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V) { _ = Paste(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            if (_icons) Icons.SelectAll();
            else Rows.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N) { _ = NewFolder(); e.Handled = true; }
        else if (e.Key == Key.Delete) { _ = Delete(); e.Handled = true; }
        else if (e.Key == Key.F2) { _ = Rename(); e.Handled = true; }
        else if (e.Key == Key.F5) { _force = true; _ = Load(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            var row = Selected().FirstOrDefault();
            if (row != null) _ = OpenRow(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Back) { Up(); e.Handled = true; }
    }

    void Rows_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemFrom(e.OriginalSource);
        if (row != null && !SelectedItems.Contains(row))
        {
            SelectedItems.Clear();
            SelectedItems.Add(row);
        }
        var rows = Selected();
        var inside = _place.Kind is "local" or "remote";
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (rows.Count == 1) Add("Aç", () => _ = OpenRow(rows[0]), true);
        Add("Kes", () => Remember(true), rows.Count > 0 && rows.All(item => item.Kind is "file" or "dir"));
        Add("Kopyala", () => Remember(false), rows.Count > 0 && rows.All(item => item.Kind != "pc"));
        Add("Yapıştır", () => _ = Paste(), inside);
        menu.Items.Add(new Separator());
        Add("Yeni klasör", () => _ = NewFolder(), inside);
        Add("Yeniden adlandır", () => _ = Rename(), rows.Count == 1 && rows[0].Kind is "file" or "dir");
        Add("Sil", () => _ = Delete(), rows.Count > 0 && rows.All(item => item.Kind is "file" or "dir"));
        Add("Klasör rengi", ColorFolder, rows.Count == 1 && ColorKey(rows[0]).Length > 0);
        menu.PlacementTarget = (FrameworkElement)Sheet;
        menu.IsOpen = true;
        e.Handled = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        var inside = _place.Kind is "local" or "remote";
        if (!inside || _busy || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        var ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        var want = shift && !ctrl ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & want) == 0)
            want = e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move);
        e.Effects = want == 0 ? DragDropEffects.None : want;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (_place.Kind is not ("local" or "remote")) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var move = e.Effects == DragDropEffects.Move;
        string? staged = null;
        List<ClipItem> items;
        if (move)
        {
            var copy = Stage(files);
            if (copy == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                StatusText.Text = "Kopyalanamadı.";
                return;
            }
            staged = copy.Value.Dir;
            items = copy.Value.Items;
        }
        else items = files.Select(PathItem).ToList();
        e.Handled = true;
        _ = PasteStaged(items, staged);
    }

    async Task PasteStaged(List<ClipItem> items, string? staged)
    {
        try
        {
            await PasteItems(items, false);
        }
        finally
        {
            if (staged != null)
            {
                try { Directory.Delete(staged, true); } catch { /* geçici */ }
            }
        }
    }

    (string Dir, List<ClipItem> Items)? Stage(string[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OrtakAlan", "birak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var items = new List<ClipItem>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Length == 0) continue;
            var dest = Path.Combine(dir, name);
            var error = CopyLocal(file, dest, Directory.Exists(file));
            if (error != null)
            {
                try { Directory.Delete(dir, true); } catch { /* geçici */ }
                return null;
            }
            items.Add(PathItem(dest));
        }
        return items.Count == 0 ? null : (dir, items);
    }

    void Rows_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _press = ItemFrom(e.OriginalSource) != null;
        _pressAt = e.GetPosition(Rows);
    }

    void Rows_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _press = false;

    void Rows_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_press || _dragBusy || _busy || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Rows);
        if (Math.Abs(pos.X - _pressAt.X) < 8 && Math.Abs(pos.Y - _pressAt.Y) < 8) return;
        _press = false;
        var under = ItemFrom(e.OriginalSource);
        var rows = Selected().Where(row => row.Kind != "pc").ToList();
        if (under != null && under.Kind != "pc" && !rows.Contains(under))
        {
            SelectedItems.Clear();
            SelectedItems.Add(under);
            rows = [under];
        }
        if (rows.Count == 0) return;
        _dragBusy = true;
        try
        {
            var removable = rows.Where(row => row.Kind is "file" or "dir").ToList();
            var effect = ShellDrag.Drag(rows.Select(ToNode).ToList(), removable.Count == 0 ? null : () => RemoveAfterMove(removable));
            StatusText.Text = !string.IsNullOrEmpty(ShellDrag.LastError) ? ShellDrag.LastError
                : effect == 2 ? "Taşındı."
                : effect == 0 ? Sheet.Items.Count + " öğe"
                : "Kopyalandı.";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            StatusText.Text = ShellDrag.LastError ?? "Sürüklenemedi.";
        }
        finally
        {
            _dragBusy = false;
        }
    }

    void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Rows.ActualWidth - 430;
        if (width > 180) NameColumn.Width = width;
    }

    void RemoveAfterMove(List<FileRow> rows)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            foreach (var row in rows)
                await DeleteSource(ToClip(row));
            _force = true;
            await Load();
        });
    }

    ShellNode ToNode(FileRow row)
    {
        if (row.LocalPath.Length > 0)
            return LocalNode(row.Name, row.LocalPath, row.IsDir);
        return RemoteNode(row.Name, row.IsDir, row.Size, row.PeerId, row.ShareId, row.Rel);
    }

    ShellNode LocalNode(string name, string path, bool dir)
    {
        if (!dir)
        {
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            return new ShellNode
            {
                Name = name,
                Size = length,
                Open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            };
        }
        return new ShellNode
        {
            Name = name,
            IsDirectory = true,
            Children = () => new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(info => !Skip(info))
                .OrderByDescending(info => info is DirectoryInfo)
                .ThenBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(info => LocalNode(info.Name, info.FullName, info is DirectoryInfo))
                .ToList()
        };
    }

    ShellNode RemoteNode(string name, bool dir, long size, string peer, string share, string rel)
    {
        if (!dir)
        {
            string? cached = null;
            return new ShellNode
            {
                Name = name,
                Size = size,
                Open = () =>
                {
                    if (cached == null || !File.Exists(cached))
                    {
                        var folder = Path.Combine(Path.GetTempPath(), "OrtakAlan", "surukle", Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(folder);
                        var dest = Path.Combine(folder, name);
                        var (error, got) = Task.Run(() => App.Me.Node.DownloadToAsync(peer, share, rel, name, dest)).GetAwaiter().GetResult();
                        if (error != null || string.IsNullOrEmpty(got)) throw new IOException(error ?? "Dosya alınamadı");
                        cached = got;
                    }
                    return new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
            };
        }
        return new ShellNode
        {
            Name = name,
            IsDirectory = true,
            Children = () =>
            {
                var (error, entries) = Task.Run(() => App.Me.Node.ListAsync(peer, share, rel)).GetAwaiter().GetResult();
                if (error != null) throw new IOException(error);
                return entries
                    .OrderByDescending(entry => entry.IsDir)
                    .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(entry => RemoteNode(entry.Name, entry.IsDir, entry.Size, peer, share, Join(rel, entry.Name)))
                    .ToList();
            }
        };
    }

    string? Ask(string title, string label, string value, bool selectStem)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
            FontSize = 14
        };
        window.Background = Brushes.White;
        window.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var box = new TextBox
        {
            Text = value,
            Margin = new Thickness(0, 8, 0, 0),
            Background = Brushes.White,
            Foreground = window.Foreground,
            CaretBrush = window.Foreground
        };
        var ok = new Button { Content = "Tamam", IsDefault = true, Style = (Style)FindResource("PrimaryButton"), MinWidth = 90 };
        var cancel = new Button { Content = "Vazgeç", IsCancel = true, Style = (Style)FindResource("SecondaryButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text.Trim();
            window.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        window.Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    buttons
                }
            }
        };
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            if (!selectStem) return;
            var ext = Path.GetExtension(value);
            if (ext.Length > 0 && value.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                box.Select(0, value.Length - ext.Length);
        };
        return window.ShowDialog() == true ? result : null;
    }

    void ApplyChrome()
    {
        ExplorerChrome.Read();
        Paint("ExBg", ExplorerChrome.Bg);
        Paint("ExFg", ExplorerChrome.Fg);
        Paint("ExMuted", ExplorerChrome.Muted);
        Paint("ExAddress", ExplorerChrome.Address);
        Paint("ExHover", ExplorerChrome.Hover);
        Paint("ExSelect", ExplorerChrome.Select);
        Paint("ExLine", ExplorerChrome.Line);
        Background = (Brush)Resources["ExBg"];
        Foreground = (Brush)Resources["ExFg"];
    }

    void Paint(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        SearchHint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loaded.Count > 0 || _query.Length > 0) Present();
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navLock || Nav.SelectedItem is not NavRow row || Same(_place, row.Place)) return;
        Go(row.Place);
    }

    void Yeni_Click(object sender, RoutedEventArgs e) => MenuOf((FrameworkElement)sender, ("Klasör", () => _ = NewFolder()));

    void Sort_Click(object sender, RoutedEventArgs e) => MenuOf((FrameworkElement)sender,
        ("Ad", () => Sort("ad")),
        ("Değiştirme tarihi", () => Sort("tarih")),
        ("Tür", () => Sort("tur")),
        ("Boyut", () => Sort("boyut")));

    void View_Click(object sender, RoutedEventArgs e) => MenuOf((FrameworkElement)sender,
        ("Büyük simgeler", () => ShowIcons(true)),
        ("Ayrıntılar", () => ShowIcons(false)));

    void Sort(string mode)
    {
        _sort = mode;
        Present();
    }

    void ShowIcons(bool icons)
    {
        var keep = Selected().Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _icons = icons;
        Icons.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = icons ? Visibility.Collapsed : Visibility.Visible;
        SelectedItems.Clear();
        foreach (FileRow row in Sheet.Items)
        {
            if (keep.Contains(row.Name)) SelectedItems.Add(row);
        }
    }

    static void MenuOf(FrameworkElement target, params (string Title, Action Run)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (title, run) in items)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void ColorFolder()
    {
        var row = Selected().FirstOrDefault();
        var key = row == null ? "" : ColorKey(row);
        if (key.Length == 0) return;
        if (!PickColor(out var hex)) return;
        var map = App.Me.Settings.FolderColors;
        if (string.IsNullOrEmpty(hex)) map.Remove(key);
        else map[key] = hex;
        App.Me.Store.Save();
        _force = true;
        _ = Load();
    }

    bool PickColor(out string? hex)
    {
        hex = null;
        var window = new Window
        {
            Title = "Klasör rengi",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Resources["ExBg"],
            Foreground = (Brush)Resources["ExFg"],
            FontFamily = FontFamily
        };
        string? chosen = null;
        var picked = false;
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Swatch(string? value, Color color, string tip)
        {
            var button = new Button
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(color),
                BorderBrush = (Brush)Resources["ExLine"],
                BorderThickness = new Thickness(1),
                ToolTip = tip,
                Cursor = Cursors.Hand
            };
            button.Click += (_, _) =>
            {
                chosen = value;
                picked = true;
                window.DialogResult = true;
            };
            wrap.Children.Add(button);
        }
        Swatch(null, FolderArt.Tiffany, "Varsayılan");
        Swatch("#E85D4C", Color.FromRgb(0xE8, 0x5D, 0x4C), "Kırmızı");
        Swatch("#F08A24", Color.FromRgb(0xF0, 0x8A, 0x24), "Turuncu");
        Swatch("#F0C42E", Color.FromRgb(0xF0, 0xC4, 0x2E), "Sarı");
        Swatch("#3CB46E", Color.FromRgb(0x3C, 0xB4, 0x6E), "Yeşil");
        Swatch("#2F80ED", Color.FromRgb(0x2F, 0x80, 0xED), "Mavi");
        Swatch("#8E5BD0", Color.FromRgb(0x8E, 0x5B, 0xD0), "Mor");
        Swatch("#E85A9B", Color.FromRgb(0xE8, 0x5A, 0x9B), "Pembe");
        Swatch("#8A8F98", Color.FromRgb(0x8A, 0x8F, 0x98), "Gri");
        var custom = new Button
        {
            Content = "Özel renk…",
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        custom.Click += (_, _) =>
        {
            var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var color = dialog.Color;
            chosen = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            picked = true;
            window.DialogResult = true;
        };
        var reset = new Button
        {
            Content = "Varsayılan",
            Margin = new Thickness(8, 4, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand
        };
        reset.Click += (_, _) =>
        {
            chosen = null;
            picked = true;
            window.DialogResult = true;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(custom);
        row.Children.Add(reset);
        window.Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Klasör rengi" },
                    wrap,
                    row
                }
            }
        };
        var ok = window.ShowDialog() == true && picked;
        hex = chosen;
        return ok;
    }

    static ImageSource? IconOf(string name, bool dir, bool computer, string key)
    {
        if (!dir || computer)
            return ExplorerIcons.For(name, dir, computer);
        if (key.Length > 0 && App.Me.Settings.FolderColors.TryGetValue(key, out var hex) && TryColor(hex, out var color))
            return FolderArt.Create(color);
        return FolderArt.Create(FolderArt.Tiffany);
    }

    static string ColorKey(FileRow row)
    {
        if (!row.IsDir) return "";
        if (row.LocalPath.Length > 0) return "y|" + row.LocalPath.ToLowerInvariant();
        if (row.PeerId.Length > 0 && row.Kind is "share" or "dir")
            return "u|" + row.PeerId + "|" + row.ShareId + "|" + row.Rel;
        return "";
    }

    static bool TryColor(string hex, out Color color)
    {
        color = default;
        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6) return false;
        if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)) return false;
        if (!byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)) return false;
        if (!byte.TryParse(text[4..], System.Globalization.NumberStyles.HexNumber, null, out var b)) return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        Toolbar.IsEnabled = !busy;
        Icons.IsEnabled = !busy;
        Rows.IsEnabled = !busy;
        Nav.IsEnabled = !busy;
    }

    ItemsControl Sheet => _icons ? Icons : Rows;

    System.Collections.IList SelectedItems => _icons ? Icons.SelectedItems : Rows.SelectedItems;

    List<FileRow> Selected() => SelectedItems.Cast<FileRow>().ToList();

    HashSet<string> Names() => _loaded.Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    string FreeName(string stem)
    {
        var names = Names();
        if (!names.Contains(stem)) return stem;
        for (var i = 2; i < 100; i++)
        {
            var candidate = stem + " (" + i + ")";
            if (!names.Contains(candidate)) return candidate;
        }
        return stem;
    }

    bool SameFolder(ClipItem item)
    {
        if (_place.Kind == "local" && item.LocalPath.Length > 0)
            return string.Equals(Path.GetDirectoryName(item.LocalPath), _place.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (_place.Kind == "remote" && item.LocalPath.Length == 0 && item.PeerId == _place.PeerId && item.ShareId == _place.ShareId)
            return string.Equals(ParentRel(item.Rel), _place.Rel, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    bool InsideSelf(ClipItem item)
    {
        if (!item.IsDir) return false;
        if (_place.Kind == "local" && item.LocalPath.Length > 0)
        {
            var dest = _place.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var source = item.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return dest.Equals(source, StringComparison.OrdinalIgnoreCase) || dest.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        if (_place.Kind == "remote" && item.LocalPath.Length == 0 && item.PeerId == _place.PeerId && item.ShareId == _place.ShareId)
        {
            var source = item.Rel;
            var dest = _place.Rel;
            return dest.Equals(source, StringComparison.OrdinalIgnoreCase) || dest.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    static string CopyName(string name, bool dir, Func<string, bool> exists)
    {
        var stem = dir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = dir ? "" : Path.GetExtension(name);
        var first = stem + " - Kopya" + ext;
        if (!exists(first)) return first;
        for (var i = 2; i < 100; i++)
        {
            var candidate = stem + " - Kopya (" + i + ")" + ext;
            if (!exists(candidate)) return candidate;
        }
        return stem + " - Kopya " + Guid.NewGuid().ToString("N")[..4] + ext;
    }

    static ClipItem ToClip(FileRow row) => new()
    {
        Name = row.Name,
        IsDir = row.IsDir,
        PeerId = row.PeerId,
        ShareId = row.ShareId,
        Rel = row.Rel,
        LocalPath = row.LocalPath
    };

    static ClipItem PathItem(string path) => new()
    {
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
        LocalPath = path,
        IsDir = Directory.Exists(path)
    };

    static bool Same(Place left, Place right) =>
        left.Kind == right.Kind && left.PeerId == right.PeerId && left.ShareId == right.ShareId && left.Rel == right.Rel &&
        string.Equals(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase);

    static FileRow? ItemFrom(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (current is ListBoxItem box && box.DataContext is FileRow picked) return picked;
            if (current is ListViewItem item && item.DataContext is FileRow row) return row;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    static bool Skip(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System) || info.Name.StartsWith('.');

    static bool SafeName(string name) =>
        name.Length is > 0 and < 180 && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    static string Join(string rel, string name) => string.IsNullOrEmpty(rel) ? name : rel + "/" + name;

    static string ParentRel(string rel)
    {
        var slash = rel.LastIndexOf('/');
        return slash < 0 ? "" : rel[..slash];
    }

    static string Unique(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        return Path.Combine(dir, Guid.NewGuid().ToString("N")[..6] + "-" + name);
    }

    static string TypeName(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Length == 0 ? "Dosya" : ext.TrimStart('.').ToUpperInvariant() + " dosyası";
    }

    static string SizeOf(long size)
    {
        if (size < 1024) return size + " B";
        if (size < 1024 * 1024) return (size / 1024d).ToString("0.#") + " KB";
        if (size < 1024d * 1024 * 1024) return (size / 1024d / 1024d).ToString("0.#") + " MB";
        return (size / 1024d / 1024d / 1024d).ToString("0.#") + " GB";
    }
}

sealed class NavRow
{
    public string Title { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public Place Place { get; init; } = new();
}

sealed class Place
{
    public string Kind { get; set; } = "root";
    public string Title { get; set; } = "Paylaşılan Dosyalar";
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string ShareId { get; set; } = "";
    public string ShareName { get; set; } = "";
    public string Rel { get; set; } = "";
    public string LocalPath { get; set; } = "";
}

sealed class FileRow
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public string TypeText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string WhenText { get; init; } = "";
    public DateTime When { get; init; }
    public long Size { get; init; }
    public bool IsDir { get; init; }
    public string Kind { get; init; } = "";
    public string PeerId { get; init; } = "";
    public string PeerName { get; init; } = "";
    public string ShareId { get; init; } = "";
    public string ShareName { get; init; } = "";
    public string Rel { get; init; } = "";
    public string LocalPath { get; init; } = "";
}

sealed class ClipItem
{
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    public string PeerId { get; init; } = "";
    public string ShareId { get; init; } = "";
    public string Rel { get; init; } = "";
    public string LocalPath { get; init; } = "";
}

sealed class Crumb
{
    public string Title { get; init; } = "";
    public bool ShowSep { get; init; }
    public Place Place { get; init; } = new();
}
