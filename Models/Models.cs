using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace OrtakAlan;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class KnownPeer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ShareItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "folder";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? WindowsShare { get; set; }
    public string? WindowsError { get; set; }
}

public sealed class SettingsModel
{
    public string ComputerId { get; set; } = Guid.NewGuid().ToString("N");
    public string ComputerName { get; set; } = "";
    public string Theme { get; set; } = "Kağıt";
    public string Appearance { get; set; } = "Aydınlık";
    public string TextColor { get; set; } = "Otomatik";
    public bool StartWithWindows { get; set; } = true;
    public long TrialStartUtc { get; set; }
    public string LicenseKey { get; set; } = "";
    public string LicenseNotice { get; set; } = "";
    public string MachineStamp { get; set; } = "";
    public List<KnownPeer> Approved { get; set; } = new();
    public List<KnownPeer> Rejected { get; set; } = new();
    public List<KnownPeer> People { get; set; } = new();
    public List<KnownPeer> Hidden { get; set; } = new();
    public List<ShareItem> Shares { get; set; } = new();
    public Dictionary<string, string> FolderColors { get; set; } = new();
    public List<string> QuickMessages { get; set; } = new();
}

public static class RoomChat
{
    public const string Id = "ortak";
    public const string Title = "Ortak konuşma";
}

public sealed record RoomPost(string Id, string Text, string Author, string AuthorId, string? FileName, long UtcTicks, bool Struck = false);

public sealed record RoomFile(string MessageId, string Author, string AuthorId, string FileName, string Path);

public sealed class ChatLine : Observable
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Mine { get; set; }
    public string Author { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string? FileName { get; set; }
    string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        set
        {
            _filePath = value;
            Raise(nameof(FilePath));
            Raise(nameof(Action));
        }
    }
    public DateTime Utc { get; set; } = DateTime.Now;

    string _status = "";
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(HasStatus));
                Raise(nameof(TickText));
            }
        }
    }

    bool _seen;
    public bool Seen
    {
        get => _seen;
        set
        {
            if (Set(ref _seen, value)) Raise(nameof(TickText));
        }
    }

    bool _failed;
    public bool Failed
    {
        get => _failed;
        set
        {
            if (Set(ref _failed, value)) Raise(nameof(ShowTicks));
        }
    }

    bool _struck;
    public bool Struck
    {
        get => _struck;
        set => Set(ref _struck, value);
    }

    bool _favorite;
    public bool Favorite
    {
        get => _favorite;
        set
        {
            if (!Set(ref _favorite, value)) return;
            Raise(nameof(FavoriteGlyph));
            Raise(nameof(FavoriteTip));
        }
    }

    [JsonIgnore]
    public string FavoriteGlyph => _favorite ? "\uE735" : "\uE734";

    [JsonIgnore]
    public string FavoriteTip => _favorite ? "Favorilerden çıkar" : "Favorilere ekle";

    [JsonIgnore]
    public bool IsFile => !string.IsNullOrEmpty(FileName);

    [JsonIgnore]
    public bool HasStatus => !IsFile && Status.Length > 0;

    [JsonIgnore]
    public bool ShowTicks => Mine && !Failed && Id.Length > 0;

    [JsonIgnore]
    public string TickText => Seen ? "Görüldü" : Status == "İletilecek" ? "İletilecek" : "İletildi";

    [JsonIgnore]
    public bool ShowAuthor => !Mine && Author.Length > 0;

    [JsonIgnore]
    public string Action => IsFile && (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) ? "Al" : "Aç";
}

public sealed class RemoteShare
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "folder";
    public string Name { get; set; } = "";
    public string? WindowsShare { get; set; }
}

public sealed class PeerSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public IPAddress Ip { get; set; } = IPAddress.Loopback;
    public bool Online { get; set; }
    public List<RemoteShare> Shares { get; set; } = new();
}

public sealed class RemoteShareVm
{
    public string PeerId { get; init; } = "";
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string? WindowsShare { get; init; }
    public string KindLabel { get; init; } = "";
    public string Glyph { get; init; } = "";
}

public sealed class PeerVm : Observable
{
    public string Id { get; init; } = "";
    public ObservableCollection<RemoteShareVm> Shares { get; } = new();
    string _sig = "";

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    bool _online;
    public bool Online
    {
        get => _online;
        set
        {
            if (!SetField(ref _online, value)) return;
            Raise(nameof(Status));
            Raise(nameof(Dot));
            Raise(nameof(BoxBorder));
            Raise(nameof(BoxFill));
            Raise(nameof(GlowColor));
        }
    }

    public Brush BoxBorder => Online ? OnlineBorder : OfflineBorder;
    public Brush BoxFill => Online ? OnlineFill : OfflineFill;
    public Color GlowColor => Online ? Color.FromRgb(0x39, 0xFF, 0x6A) : Color.FromRgb(0xFF, 0x3B, 0x4A);

    int _unread;
    public int Unread
    {
        get => _unread;
        set
        {
            if (!Set(ref _unread, value)) return;
            Raise(nameof(HasUnread));
            Raise(nameof(UnreadText));
        }
    }

    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString();

    double _breathOpacity = 0.7;
    public double BreathOpacity
    {
        get => _breathOpacity;
        set
        {
            if (Math.Abs(_breathOpacity - value) < 0.02) return;
            _breathOpacity = value;
            Raise(nameof(BreathOpacity));
        }
    }

    double _breathBlur = 12;
    public double BreathBlur
    {
        get => _breathBlur;
        set
        {
            if (Math.Abs(_breathBlur - value) < 0.4) return;
            _breathBlur = value;
            Raise(nameof(BreathBlur));
        }
    }

    public string Status => !Online ? "Çevrimdışı" : TailscalePeers.IsAddress(Ip) ? "Çevrimiçi · Tailscale" : "Çevrimiçi";
    public Brush Dot => Online ? OnlineBrush : OfflineBrush;
    public string Ip { get; private set; } = "127.0.0.1";
    public int Port { get; private set; }

    static readonly SolidColorBrush OnlineBrush = Freeze(Color.FromRgb(0x16, 0xA3, 0x4A));
    static readonly SolidColorBrush OfflineBrush = Freeze(Color.FromRgb(0xFF, 0x3B, 0x4A));
    static readonly SolidColorBrush OnlineBorder = Freeze(Color.FromRgb(0x39, 0xFF, 0x6A));
    static readonly SolidColorBrush OfflineBorder = Freeze(Color.FromRgb(0xFF, 0x3B, 0x4A));
    static readonly SolidColorBrush OnlineFill = Freeze(Color.FromArgb(210, 0x0C, 0x2E, 0x18));
    static readonly SolidColorBrush OfflineFill = Freeze(Color.FromArgb(210, 0x3A, 0x10, 0x16));

    static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    public void Apply(PeerSnapshot snapshot)
    {
        Name = snapshot.Name;
        Ip = snapshot.Ip.ToString();
        Port = snapshot.Port;
        Online = snapshot.Online;
        Raise(nameof(Status));
        var sig = string.Join("|", snapshot.Shares.Select(s => $"{s.Id}:{s.Kind}:{s.Name}:{s.WindowsShare}"));
        if (sig == _sig) return;
        Shares.Clear();
        foreach (var share in snapshot.Shares)
        {
            Shares.Add(new RemoteShareVm
            {
                PeerId = snapshot.Id,
                Id = share.Id,
                Kind = share.Kind,
                Name = share.Name,
                WindowsShare = share.WindowsShare,
                KindLabel = share.Kind == "printer" ? "Yazıcı" : "Klasör",
                Glyph = share.Kind == "printer" ? "\uE749" : "\uE8B7"
            });
        }
        _sig = sig;
    }
}

public sealed class ShareVm
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Line { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Warning { get; init; } = "";
    public string Tip { get; init; } = "";
}

public sealed class DirEntry
{
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public long Modified { get; init; }
    public string Glyph => IsDir ? "\uE8B7" : "\uE8A5";
    public string SizeText => IsDir ? "Klasör" : Format(Size);
    public string WhenText => Modified <= 0 ? "" : new DateTime(Modified, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    static string Format(long size)
    {
        if (size < 1024) return size + " B";
        if (size < 1024 * 1024) return (size / 1024d).ToString("0.#") + " KB";
        return (size / 1024d / 1024d).ToString("0.#") + " MB";
    }
}

public sealed class PeerDirectory
{
    public ObservableCollection<PeerVm> Peers { get; } = new();
    public HashSet<string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    public int OnlineCount => Peers.Count(p => p.Online);

    public void Apply(IReadOnlyList<PeerSnapshot> list)
    {
        var ids = new HashSet<string>();
        foreach (var snapshot in list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Hidden.Contains(snapshot.Id)) continue;
            ids.Add(snapshot.Id);
            var vm = Peers.FirstOrDefault(p => p.Id == snapshot.Id);
            if (vm == null)
            {
                vm = new PeerVm { Id = snapshot.Id };
                Peers.Add(vm);
            }
            vm.Apply(snapshot);
        }

        for (var i = Peers.Count - 1; i >= 0; i--)
        {
            if (Hidden.Contains(Peers[i].Id))
                Peers.RemoveAt(i);
            else if (!ids.Contains(Peers[i].Id))
                Peers[i].Online = false;
        }

        Changed?.Invoke();
    }

    public void Forget(string id)
    {
        if (id.Length == 0) return;
        Hidden.Add(id);
        var peer = Peers.FirstOrDefault(item => item.Id == id);
        if (peer != null) Peers.Remove(peer);
        Changed?.Invoke();
    }

    public void Unhide(string id)
    {
        if (id.Length == 0) return;
        Hidden.Remove(id);
    }

    public void Remember(string id, string name)
    {
        if (id.Length == 0 || Hidden.Contains(id) || Peers.Any(p => p.Id == id)) return;
        var vm = new PeerVm { Id = id };
        vm.Apply(new PeerSnapshot
        {
            Id = id,
            Name = name.Length == 0 ? "Bilgisayar" : name,
            Online = false,
            Ip = IPAddress.Loopback
        });
        Peers.Add(vm);
        Changed?.Invoke();
    }
}

public sealed record IncomingChat(string PeerId, string PeerName, string Text, string MessageId);
public sealed record IncomingFile(string PeerId, string PeerName, string FileName, string Path, string MessageId);
public sealed record IncomingPrint(string PeerName, string FileName, string PrinterName, string? Error);
