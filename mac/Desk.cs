using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrtakAlan.Mac;

sealed class Desk
{
    public const int TrialDays = 7;
    public const int FreeUsers = 3;
    public const string Contact = "LvntYxl@gmail.com";
    public const string SeatStatus = "Üç bilgisayardan fazlası için lisans gerekir.";
    public const string LockedStatus = "Kullanım süresi doldu. Lisans girilmeden işlem yapılamaz.";

    static readonly byte[] PublicKey = Convert.FromBase64String(
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5hyhr+P2+Z/oKDffo5Aafi4+dSlGtNR2i2jzoU1THFlj7bhqIF6PjdzIQ6Yo4+sfE3RmfjqtX01LDcyMbLlpOA==");

    public static Desk Current { get; private set; } = null!;

    public string DirectoryPath { get; }
    public Profile Profile { get; private set; } = new();
    public bool IsLicensed { get; private set; }
    public string LicenseName { get; private set; } = "";
    public int Limit { get; private set; } = FreeUsers;
    public bool TrialEnded { get; private set; }
    public int TrialDaysLeft { get; private set; } = TrialDays;
    public string Key => IsLicensed ? Profile.LicenseKey : "";
    public bool Usable => IsLicensed || !TrialEnded;
    public string StopText => Usable ? SeatStatus : LockedStatus;

    Desk(string directory)
    {
        DirectoryPath = directory;
    }

    public static Desk Open()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrtakAlan");
        Directory.CreateDirectory(dir);
        var desk = new Desk(dir);
        desk.Load();
        Current = desk;
        return desk;
    }

    public string TrialLine()
    {
        if (IsLicensed)
            return LicenseName.Length == 0 ? "Lisanslı" : "Lisanslı · " + LicenseName;
        if (!Usable) return "Lisans gerekli";
        var days = TrialDaysLeft == 1 ? "1 gün kaldı" : TrialDaysLeft + " gün kaldı";
        return days + " · 3 bilgisayar";
    }

    public bool Fits(string id)
    {
        if (!Usable) return false;
        if (IsKnown(id)) return true;
        return Count() < Limit;
    }

    public bool IsKnown(string id)
    {
        if (id.Length == 0 || id == Profile.ComputerId) return false;
        if (Profile.Hidden.Any(p => p.Id == id)) return false;
        return Profile.People.Any(p => p.Id == id) || Profile.Approved.Any(p => p.Id == id);
    }

    public int Count()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var person in Profile.People)
            if (person.Id.Length > 0 && person.Id != Profile.ComputerId) ids.Add(person.Id);
        foreach (var person in Profile.Approved)
            if (person.Id.Length > 0 && person.Id != Profile.ComputerId) ids.Add(person.Id);
        foreach (var person in Profile.Hidden)
            ids.Remove(person.Id);
        return ids.Count;
    }

    public bool Remember(string id, string name)
    {
        if (id.Length == 0 || id == Profile.ComputerId || Profile.Hidden.Any(p => p.Id == id)) return false;
        var label = name.Length == 0 ? "Bilgisayar" : name;
        var found = Profile.People.FirstOrDefault(p => p.Id == id);
        if (found == null)
        {
            if (!Fits(id)) return false;
            Profile.People.Add(new Person { Id = id, Name = label });
            Save();
            return true;
        }
        if (!string.Equals(found.Name, label, StringComparison.Ordinal))
        {
            found.Name = label;
            Save();
        }
        return true;
    }

    public void Hide(string id, string name)
    {
        if (id.Length == 0) return;
        Profile.People.RemoveAll(p => p.Id == id);
        Profile.Hidden.RemoveAll(p => p.Id == id);
        Profile.Hidden.Add(new Person { Id = id, Name = name.Length == 0 ? "Bilgisayar" : name });
        Save();
    }

    public bool IsApproved(string id) => Profile.Approved.Any(p => p.Id == id);
    public bool IsRejected(string id) => Profile.Rejected.Any(p => p.Id == id);

    public void Decide(string id, string name, bool approved)
    {
        Profile.Approved.RemoveAll(p => p.Id == id);
        Profile.Rejected.RemoveAll(p => p.Id == id);
        var peer = new Person { Id = id, Name = name };
        if (approved) Profile.Approved.Add(peer);
        else Profile.Rejected.Add(peer);
        Save();
    }

    public bool TryAccept(string key, out string error)
    {
        key = Compact(key);
        if (key.Length == 0)
        {
            error = "Anahtarı yapıştırın.";
            return false;
        }
        if (!TryRead(key, out _))
        {
            error = "Bu anahtar geçersiz.";
            return false;
        }
        StoreKey(key);
        error = "";
        return true;
    }

    public bool AcceptShared(string key)
    {
        key = Compact(key);
        if (key.Length == 0 || !TryRead(key, out var incoming)) return false;
        if (IsLicensed && TryRead(Profile.LicenseKey, out var current) && !Better(incoming, current))
            return false;
        if (string.Equals(Profile.LicenseKey, key, StringComparison.Ordinal)) return false;
        StoreKey(key);
        return true;
    }

    public void AddShare(Share share)
    {
        Profile.Shares.Add(share);
        Save();
    }

    public void RemoveShare(string id)
    {
        Profile.Shares.RemoveAll(s => s.Id == id);
        Save();
    }

    public List<Share> Shares() => Profile.Shares.ToList();

    public void Save()
    {
        var path = Path.Combine(DirectoryPath, "ayar.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Profile, Json));
        File.Move(temp, path, true);
    }

    void StoreKey(string key)
    {
        Profile.LicenseKey = key;
        ApplyKey(key);
        Save();
    }

    void Load()
    {
        var path = Path.Combine(DirectoryPath, "ayar.json");
        if (File.Exists(path))
        {
            try
            {
                Profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? new Profile();
            }
            catch
            {
                Profile = new Profile();
            }
        }
        Profile.People ??= new();
        Profile.Approved ??= new();
        Profile.Rejected ??= new();
        Profile.Hidden ??= new();
        Profile.Shares ??= new();
        if (Profile.ComputerId.Length == 0) Profile.ComputerId = Guid.NewGuid().ToString("N");
        var stamp = MachineStamp();
        var stampFile = Path.Combine(DirectoryPath, "makine.damga");
        var previous = File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : "";
        if (previous.Length > 0 && !string.Equals(previous, stamp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bu program başka bir bilgisayardan kopyalanmış. Bu bilgisayarda çalışmaz.");
        if (Profile.MachineStamp.Length > 0 && !string.Equals(Profile.MachineStamp, stamp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bu program başka bir bilgisayardan kopyalanmış. Bu bilgisayarda çalışmaz.");
        File.WriteAllText(stampFile, stamp);
        Profile.MachineStamp = stamp;

        var trialPath = Path.Combine(DirectoryPath, "deneme.txt");
        long start = Profile.TrialStartUtc;
        if (File.Exists(trialPath) && long.TryParse(File.ReadAllText(trialPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved) && saved > 0)
            start = start > 0 ? Math.Min(start, saved) : saved;
        if (start <= 0) start = DateTime.UtcNow.Ticks;
        Profile.TrialStartUtc = start;
        File.WriteAllText(trialPath, start.ToString(CultureInfo.InvariantCulture));
        ApplyKey(Profile.LicenseKey);
        var end = new DateTime(start, DateTimeKind.Utc).AddDays(TrialDays);
        var now = DateTime.UtcNow;
        TrialEnded = now >= end;
        TrialDaysLeft = TrialEnded ? 0 : Math.Max(1, (int)Math.Ceiling((end - now).TotalDays));
        Save();
    }

    void ApplyKey(string key)
    {
        IsLicensed = TryRead(key, out var info);
        LicenseName = IsLicensed ? info.Name : "";
        Limit = !IsLicensed ? FreeUsers : info.Seats == 0 ? int.MaxValue : info.Seats;
    }

    static bool Better(LicenseInfo incoming, LicenseInfo current)
    {
        if (current.Seats == 0) return false;
        if (incoming.Seats == 0) return true;
        return incoming.Seats > current.Seats;
    }

    static string Compact(string key) => string.Concat(key.Where(ch => !char.IsWhiteSpace(ch)));

    static bool TryRead(string key, out LicenseInfo info)
    {
        info = default;
        key = Compact(key);
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0] != "OA1") return false;
        try
        {
            var data = Base64Decode(parts[1]);
            var signature = Base64Decode(parts[2]);
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(PublicKey, out _);
            if (!ec.VerifyData(data, signature, HashAlgorithmName.SHA256)) return false;
            var payload = JsonSerializer.Deserialize<PayloadDto>(data);
            if (payload == null || payload.v != 1 || payload.s < 0) return false;
            var name = payload.n ?? "";
            if (name.Length > 80) name = name[..80];
            info = new LicenseInfo(name, payload.s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static byte[] Base64Decode(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        var pad = value.Length % 4;
        if (pad == 2) value += "==";
        else if (pad == 3) value += "=";
        else if (pad != 0) throw new FormatException();
        return Convert.FromBase64String(value);
    }

    static string MachineStamp()
    {
        try
        {
            var start = new ProcessStartInfo("ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            var text = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(4000);
            foreach (var line in text.Split('\n'))
            {
                if (!line.Contains("IOPlatformUUID", StringComparison.Ordinal)) continue;
                var parts = line.Split('"');
                var uuid = parts.LastOrDefault(part => part.Contains('-') && part.Length >= 32);
                if (!string.IsNullOrWhiteSpace(uuid)) return uuid.Trim();
            }
        }
        catch
        {
            /* ioreg yoksa makine adı */
        }
        return Environment.MachineName;
    }

    public static List<string> Printers()
    {
        var names = new List<string>();
        try
        {
            var start = new ProcessStartInfo("lpstat", "-p")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            var text = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(4000);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("printer ", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = trimmed[8..];
                var name = rest.Split(' ', 2)[0].Trim();
                if (name.Length > 0 && !names.Contains(name)) names.Add(name);
            }
        }
        catch
        {
            /* CUPS yok */
        }
        return names;
    }

    public int SyncPrinters()
    {
        if (!Usable) return 0;
        var added = 0;
        foreach (var name in Printers())
        {
            if (Profile.Shares.Any(s => s.Kind == "printer" && string.Equals(s.Path, name, StringComparison.Ordinal)))
                continue;
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ortak-yazici:" + name));
            Profile.Shares.Add(new Share
            {
                Id = Convert.ToHexString(bytes)[..16].ToLowerInvariant(),
                Kind = "printer",
                Name = name.Replace('_', ' '),
                Path = name
            });
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public static string? Print(string printer, string path)
    {
        try
        {
            var start = new ProcessStartInfo("lp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(printer);
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("fit-to-page");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            var error = process?.StandardError.ReadToEnd() ?? "";
            process?.WaitForExit(20000);
            if (process == null || process.ExitCode != 0)
                return error.Length == 0 ? "Yazıcıya gönderilemedi." : error.Trim();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    sealed record PayloadDto(int v, string? n, int s);
}

readonly record struct LicenseInfo(string Name, int Seats);

sealed class Profile
{
    public string ComputerId { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public long TrialStartUtc { get; set; }
    public string LicenseKey { get; set; } = "";
    public string MachineStamp { get; set; } = "";
    public List<Person> People { get; set; } = new();
    public List<Person> Approved { get; set; } = new();
    public List<Person> Rejected { get; set; } = new();
    public List<Person> Hidden { get; set; } = new();
    public List<Share> Shares { get; set; } = new();
}

sealed class Person
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

sealed class Share
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "folder";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

sealed class Note
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Mine { get; set; }
    public string Author { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long UtcTicks { get; set; }
    public bool Struck { get; set; }
}
