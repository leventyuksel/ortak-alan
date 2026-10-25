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
                model.Hidden ??= new();
                model.Shares ??= new();
                model.FolderColors ??= new();
                model.QuickMessages ??= new();
                if (string.IsNullOrWhiteSpace(model.ComputerId))
                    model.ComputerId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(model.Theme))
                    model.Theme = "Kağıt";
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

    public bool IsHidden(string id)
    {
        lock (_gate) return Current.Hidden.Any(p => p.Id == id);
    }

    public void HidePerson(string id, string name)
    {
        if (id.Length == 0) return;
        lock (_gate)
        {
            Current.People.RemoveAll(p => p.Id == id);
            Current.Hidden.RemoveAll(p => p.Id == id);
            Current.Hidden.Add(new KnownPeer { Id = id, Name = name.Length == 0 ? "Bilgisayar" : name });
            SaveLocked();
        }
    }

    public bool Allows(string id)
    {
        if (id.Length == 0) return false;
        lock (_gate) return LicenseService.Fits(Current, id);
    }

    public int UserCount()
    {
        lock (_gate) return LicenseService.Count(Current);
    }

    public bool RestorePerson(string id, string name)
    {
        if (id.Length == 0) return false;
        lock (_gate)
        {
            if (!LicenseService.Fits(Current, id)) return false;
            Current.Hidden.RemoveAll(p => p.Id == id);
        }
        RememberPerson(id, name);
        return true;
    }

    public bool RememberPerson(string id, string name)
    {
        if (id.Length == 0 || id == Current.ComputerId || IsHidden(id)) return false;
        lock (_gate)
        {
            var found = Current.People.FirstOrDefault(p => p.Id == id);
            var label = name.Length == 0 ? "Bilgisayar" : name;
            if (found == null)
            {
                if (!LicenseService.Fits(Current, id)) return false;
                Current.People.Add(new KnownPeer { Id = id, Name = label });
                SaveLocked();
                return true;
            }
            if (!string.Equals(found.Name, label, StringComparison.Ordinal))
            {
                found.Name = label;
                SaveLocked();
            }
            return true;
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

    public List<string> CopyQuick()
    {
        lock (_gate) return Current.QuickMessages.ToList();
    }

    public bool AddQuick(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        lock (_gate)
        {
            if (Current.QuickMessages.Any(item => string.Equals(item, text, StringComparison.Ordinal)))
                return false;
            Current.QuickMessages.Insert(0, text);
            while (Current.QuickMessages.Count > 40)
                Current.QuickMessages.RemoveAt(Current.QuickMessages.Count - 1);
            SaveLocked();
            return true;
        }
    }

    public void RemoveQuick(string text)
    {
        lock (_gate)
        {
            if (Current.QuickMessages.RemoveAll(item => item == text) == 0) return;
            SaveLocked();
        }
    }

    void SaveLocked()
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
