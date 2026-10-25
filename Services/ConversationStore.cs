using System.Collections.ObjectModel;
using System.Text.Json;

namespace OrtakAlan;

public sealed class ConversationStore
{
    readonly string _dir;
    readonly Dictionary<string, ObservableCollection<ChatLine>> _open = new();
    readonly Dictionary<string, List<string>> _dropped = new();
    readonly Dictionary<string, List<string>> _pendingStrikes = new();

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

    public bool Add(string peerId, ChatLine line)
    {
        if (line.Id.Length > 0 && Dropped(peerId).Contains(line.Id)) return false;
        var collection = Get(peerId);
        if (line.Id.Length > 0 && collection.Any(item => item.Id == line.Id)) return false;
        if (line.Id.Length > 0 && PendingStrikes(peerId).Contains(line.Id)) line.Struck = true;
        collection.Add(line);
        while (collection.Count > 200)
            collection.RemoveAt(0);
        Save(peerId);
        return true;
    }

    public ChatLine? Find(string peerId, string lineId)
    {
        if (lineId.Length == 0) return null;
        return Get(peerId).FirstOrDefault(item => item.Id == lineId);
    }

    public List<RoomPost> Posts(string peerId)
    {
        return Get(peerId).Select(line => new RoomPost(
            line.Id,
            line.Text,
            line.Author,
            line.AuthorId,
            line.FileName,
            line.Utc.Kind == DateTimeKind.Utc ? line.Utc.Ticks : line.Utc.ToUniversalTime().Ticks,
            line.Struck)).ToList();
    }

    public void Strike(string peerId, string id)
    {
        if (id.Length == 0 || Dropped(peerId).Contains(id)) return;
        var line = Find(peerId, id);
        if (line != null)
        {
            if (line.Struck) return;
            line.Struck = true;
            Save(peerId);
            return;
        }

        var pending = PendingStrikes(peerId);
        if (pending.Contains(id)) return;
        pending.Add(id);
        if (pending.Count > 500) pending.RemoveRange(0, pending.Count - 500);
        File.WriteAllText(StrikePath(peerId), JsonSerializer.Serialize(pending, SettingsService.JsonOptions));
    }

    public List<string> StrikeIds(string peerId)
    {
        var ids = new List<string>();
        foreach (var id in Dropped(peerId)) ids.Add(id);
        foreach (var line in Get(peerId))
            if (line.Struck && line.Id.Length > 0 && !ids.Contains(line.Id)) ids.Add(line.Id);
        foreach (var id in PendingStrikes(peerId))
            if (!ids.Contains(id)) ids.Add(id);
        return ids;
    }

    public void Remove(string peerId, ChatLine line)
    {
        var collection = Get(peerId);
        var removed = collection.Remove(line);
        RememberDrop(peerId, line.Id);
        if (removed) Save(peerId);
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

    List<string> Dropped(string peerId)
    {
        if (_dropped.TryGetValue(peerId, out var list)) return list;
        list = new List<string>();
        var path = DropPath(peerId);
        if (File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), SettingsService.JsonOptions);
                if (saved != null)
                {
                    foreach (var id in saved)
                        if (id.Length > 0 && !list.Contains(id)) list.Add(id);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
        _dropped[peerId] = list;
        return list;
    }

    void RememberDrop(string peerId, string id)
    {
        if (id.Length == 0) return;
        var list = Dropped(peerId);
        if (list.Contains(id)) return;
        list.Add(id);
        if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
        var json = JsonSerializer.Serialize(list, SettingsService.JsonOptions);
        File.WriteAllText(DropPath(peerId), json);
    }

    List<string> PendingStrikes(string peerId)
    {
        if (_pendingStrikes.TryGetValue(peerId, out var list)) return list;
        list = new List<string>();
        var path = StrikePath(peerId);
        if (File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), SettingsService.JsonOptions);
                if (saved != null)
                {
                    foreach (var id in saved)
                        if (id.Length > 0 && !list.Contains(id)) list.Add(id);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
        _pendingStrikes[peerId] = list;
        return list;
    }

    string PathOf(string peerId) => Path.Combine(_dir, Safe(peerId) + ".json");

    string DropPath(string peerId) => Path.Combine(_dir, Safe(peerId) + ".silinen.json");

    string StrikePath(string peerId) => Path.Combine(_dir, Safe(peerId) + ".cizik.json");

    static string Safe(string peerId)
    {
        var safe = new string(peerId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return safe.Length == 0 ? "sohbet" : safe;
    }
}
