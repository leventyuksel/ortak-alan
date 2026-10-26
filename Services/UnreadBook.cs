using System.Text.Json;

namespace OrtakAlan;

public sealed class UnreadBook
{
    readonly string _path;
    readonly Dictionary<string, List<string>> _ids = new(StringComparer.Ordinal);

    public event Action? Changed;

    public UnreadBook(string dataDir)
    {
        _path = Path.Combine(dataDir, "okunmamis.json");
        if (!File.Exists(_path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path));
            if (saved == null) return;
            foreach (var pair in saved)
            {
                if (pair.Key.Length == 0 || pair.Value == null) continue;
                var list = pair.Value.Where(id => id.Length > 0).Distinct().ToList();
                if (list.Count > 0) _ids[pair.Key] = list;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    public int Count(string id) => _ids.TryGetValue(id, out var list) ? list.Count : 0;

    public int Total => _ids.Values.Sum(list => list.Count);

    public void Add(string id, string messageId)
    {
        if (id.Length == 0) return;
        if (!_ids.TryGetValue(id, out var list))
        {
            list = new List<string>();
            _ids[id] = list;
        }
        if (messageId.Length == 0) messageId = Guid.NewGuid().ToString("N");
        if (list.Contains(messageId)) return;
        list.Add(messageId);
        if (list.Count > 99) list.RemoveRange(0, list.Count - 99);
        Save();
        Changed?.Invoke();
    }

    public void Clear(string id)
    {
        if (id.Length == 0 || !_ids.Remove(id)) return;
        Save();
        Changed?.Invoke();
    }

    void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_ids));
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}
