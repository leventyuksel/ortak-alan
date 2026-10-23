using System.Collections.ObjectModel;
using System.Text.Json;

namespace OrtakAlan;

public sealed class ConversationStore
{
    readonly string _dir;
    readonly Dictionary<string, ObservableCollection<ChatLine>> _open = new();

    public ConversationStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "sohbet");
        Directory.CreateDirectory(_dir);
    }

    public ObservableCollection<ChatLine> Get(string peerId)
    {
        if (_open.TryGetValue(peerId, out var existing)) return existing;
        var lines = Load(peerId);
        var collection = new ObservableCollection<ChatLine>(lines);
        _open[peerId] = collection;
        return collection;
    }

    public void Add(string peerId, ChatLine line)
    {
        var collection = Get(peerId);
        collection.Add(line);
        while (collection.Count > 200)
            collection.RemoveAt(0);
        Save(peerId);
    }

    public void Remove(string peerId, ChatLine line)
    {
        var collection = Get(peerId);
        if (!collection.Remove(line)) return;
        Save(peerId);
    }

    public void Clear(string peerId)
    {
        var collection = Get(peerId);
        if (collection.Count == 0) return;
        collection.Clear();
        Save(peerId);
    }

    public void Save(string peerId)
    {
        if (!_open.TryGetValue(peerId, out var collection)) return;
        var json = JsonSerializer.Serialize(collection.ToList(), SettingsService.JsonOptions);
        File.WriteAllText(PathOf(peerId), json);
    }

    List<ChatLine> Load(string peerId)
    {
        var path = PathOf(peerId);
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<ChatLine>>(File.ReadAllText(path), SettingsService.JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return new();
        }
    }

    string PathOf(string peerId)
    {
        var safe = new string(peerId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (safe.Length == 0) safe = "sohbet";
        return Path.Combine(_dir, safe + ".json");
    }
}
