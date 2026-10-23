using System.Text.Encodings.Web;
using System.Text.Json;

namespace OrtakAlan;

public sealed class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    readonly object _gate = new();
    readonly string _path;
    public string DirectoryPath { get; }
    public SettingsModel Current { get; private set; }

    public SettingsService(string directory)
    {
        DirectoryPath = directory;
        _path = Path.Combine(directory, "settings.json");
        Current = new SettingsModel();
    }

    public SettingsModel Load()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(_path))
            {
                Current = new SettingsModel();
                return Current;
            }

            try
            {
                var model = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(_path), JsonOptions) ?? new SettingsModel();
                model.Approved ??= new();
                model.Rejected ??= new();
                model.People ??= new();
                model.Shares ??= new();
                model.FolderColors ??= new();
                if (string.IsNullOrWhiteSpace(model.ComputerId))
                    model.ComputerId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(model.Theme))
                    model.Theme = "Tiffany";
                if (model.Appearance != "Karanlık")
                    model.Appearance = "Aydınlık";
                if (string.IsNullOrWhiteSpace(model.TextColor))
                    model.TextColor = "Otomatik";
                Current = model;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                Current = new SettingsModel();
            }

            return Current;
        }
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    public bool IsApproved(string id)
    {
        lock (_gate) return Current.Approved.Any(p => p.Id == id);
    }

    public bool IsRejected(string id)
    {
        lock (_gate) return Current.Rejected.Any(p => p.Id == id);
    }

    public void Decide(string id, string name, bool approved)
    {
        lock (_gate)
        {
            Current.Approved.RemoveAll(p => p.Id == id);
            Current.Rejected.RemoveAll(p => p.Id == id);
            var peer = new KnownPeer { Id = id, Name = name };
            if (approved) Current.Approved.Add(peer);
            else Current.Rejected.Add(peer);
            SaveLocked();
        }
    }

    public void RememberPerson(string id, string name)
    {
        if (id.Length == 0 || id == Current.ComputerId) return;
        lock (_gate)
        {
            var found = Current.People.FirstOrDefault(p => p.Id == id);
            var label = name.Length == 0 ? "Bilgisayar" : name;
            if (found == null)
            {
                Current.People.Add(new KnownPeer { Id = id, Name = label });
                SaveLocked();
                return;
            }
            if (!string.Equals(found.Name, label, StringComparison.Ordinal))
            {
                found.Name = label;
                SaveLocked();
            }
        }
    }

    public void AddShare(ShareItem item)
    {
        lock (_gate)
        {
            Current.Shares.Add(item);
            SaveLocked();
        }
    }

    public ShareItem? TakeShare(string id)
    {
        lock (_gate)
        {
            var item = Current.Shares.FirstOrDefault(s => s.Id == id);
            if (item == null) return null;
            Current.Shares.Remove(item);
            SaveLocked();
            return item;
        }
    }

    public List<ShareItem> CopyShares()
    {
        lock (_gate) return Current.Shares.ToList();
    }

    void SaveLocked()
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
