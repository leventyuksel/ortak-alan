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

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    public string Theme { get; set; } = "Lacivert";
    public string Appearance { get; set; } = "Aydınlık";
    public string TextColor { get; set; } = "Otomatik";
    public bool StartWithWindows { get; set; } = true;
    public List<KnownPeer> Approved { get; set; } = new();
    public List<KnownPeer> Rejected { get; set; } = new();
    public List<ShareItem> Shares { get; set; } = new();
    public Dictionary<string, string> FolderColors { get; set; } = new();
}

public sealed class ChatLine : Observable
{
    public string Text { get; set; } = "";
    public bool Mine { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public DateTime Utc { get; set; } = DateTime.Now;

    string _status = "";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    [JsonIgnore]
    public bool IsFile => !string.IsNullOrEmpty(FileName);
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
    public event Action? Changed;
    public int OnlineCount => Peers.Count(p => p.Online);

    public void Apply(IReadOnlyList<PeerSnapshot> list)
    {
        var ids = new HashSet<string>();
        foreach (var snapshot in list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
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
            if (!ids.Contains(Peers[i].Id))
                Peers.RemoveAt(i);
        }

        Changed?.Invoke();
    }
}

public sealed record IncomingChat(string PeerId, string PeerName, string Text);
public sealed record IncomingFile(string PeerId, string PeerName, string FileName, string Path);
