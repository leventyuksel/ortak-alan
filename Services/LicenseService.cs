using System.Globalization;

namespace OrtakAlan;

public static class LicenseService
{
    public const int TrialDays = 30;
    public const int FreeUsers = 5;
    public const string SeatStatus = "Daha fazla kullanıcı için lisans gerekir.";

    static SettingsService? _store;

    public static bool IsLicensed { get; private set; }
    public static string LicenseName { get; private set; } = "";
    public static int Seats { get; private set; }
    public static int Limit { get; private set; } = FreeUsers;
    public static bool TrialEnded { get; private set; }
    public static int TrialDaysLeft { get; private set; } = TrialDays;
    public static event Action? SeatNeeded;
    public static event Action? Changed;
    public static event Action? Adopted;
    public static string Key => IsLicensed ? _store?.Current.LicenseKey ?? "" : "";

    public static string BlockText =>
        IsLicensed
            ? "Lisanstaki kullanıcı sınırına ulaşıldı."
            : "Beş kullanıcıdan fazlası için lisans gerekir. Program bu beş kullanıcıyla çalışmaya devam eder. Lisans için " + LicenseCodec.Contact + " adresine yazın. Anahtarı künyeye girin.";

    public static string TrialText =>
        "30 günlük deneme süresi doldu. Program çalışmaya devam eder. Lisans için " + LicenseCodec.Contact + " adresine yazın. Anahtarı künyeye girin.";

    public static void Start(SettingsService store)
    {
        _store = store;
        var model = store.Current;
        var path = Path.Combine(store.DirectoryPath, "deneme.txt");
        long start = model.TrialStartUtc;
        if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved) && saved > 0)
            start = start > 0 ? Math.Min(start, saved) : saved;
        if (start <= 0) start = DateTime.UtcNow.Ticks;
        var changed = model.TrialStartUtc != start;
        model.TrialStartUtc = start;
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(path, start.ToString(CultureInfo.InvariantCulture));
        ApplyKey(model.LicenseKey);
        RefreshClock();
        if (changed) store.Save();
    }

    public static bool ShouldRemind()
    {
        if (_store == null || IsLicensed || !TrialEnded) return false;
        var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_store.Current.LicenseNotice == day) return false;
        _store.Current.LicenseNotice = day;
        _store.Save();
        return true;
    }

    public static bool TryAccept(string key, out string error)
    {
        if (_store == null)
        {
            error = "Ayarlar hazır değil.";
            return false;
        }

        key = Compact(key);
        if (key.Length == 0)
        {
            error = "Anahtarı yapıştırın.";
            return false;
        }

        if (!LicenseCodec.TryRead(key, out _))
        {
            error = "Bu anahtar geçersiz.";
            return false;
        }

        StoreKey(key);
        error = "";
        return true;
    }

    public static bool AcceptShared(string key)
    {
        key = Compact(key);
        if (_store == null || key.Length == 0 || !LicenseCodec.TryRead(key, out var incoming)) return false;
        if (IsLicensed && LicenseCodec.TryRead(_store.Current.LicenseKey, out var current) && !Better(incoming, current))
            return false;
        if (string.Equals(_store.Current.LicenseKey, key, StringComparison.Ordinal)) return false;
        StoreKey(key);
        return true;
    }

    static void StoreKey(string key)
    {
        if (_store == null) return;
        _store.Current.LicenseKey = key;
        _store.Save();
        ApplyKey(key);
        Changed?.Invoke();
        Adopted?.Invoke();
    }

    static bool Better(LicenseInfo incoming, LicenseInfo current)
    {
        if (current.Seats == 0) return false;
        if (incoming.Seats == 0) return true;
        return incoming.Seats > current.Seats;
    }

    static string Compact(string key) => string.Concat(key.Where(ch => !char.IsWhiteSpace(ch)));

    public static void NotifySeat()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(NotifySeat);
            return;
        }

        SeatNeeded?.Invoke();
    }

    public static bool Fits(SettingsModel model, string id)
    {
        if (IsKnown(model, id)) return true;
        return Count(model) < Limit;
    }

    public static bool IsKnown(SettingsModel model, string id)
    {
        if (id.Length == 0 || id == model.ComputerId) return false;
        if (model.Hidden.Any(person => person.Id == id)) return false;
        return model.People.Any(person => person.Id == id) || model.Approved.Any(person => person.Id == id);
    }

    public static int Count(SettingsModel model)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var person in model.People)
            if (person.Id.Length > 0 && person.Id != model.ComputerId) ids.Add(person.Id);
        foreach (var person in model.Approved)
            if (person.Id.Length > 0 && person.Id != model.ComputerId) ids.Add(person.Id);
        foreach (var person in model.Hidden)
            ids.Remove(person.Id);
        return ids.Count;
    }

    public static string TrialLine()
    {
        if (IsLicensed)
            return LicenseName.Length == 0 ? "Lisanslı" : "Lisanslı · " + LicenseName;
        if (TrialEnded) return "Deneme süresi doldu";
        return TrialDaysLeft == 1 ? "Deneme: 1 gün kaldı" : "Deneme: " + TrialDaysLeft + " gün kaldı";
    }

    public static string UsersLine(int count)
    {
        if (IsLicensed && Seats == 0) return count + " kullanıcı · sınır yok";
        var cap = IsLicensed ? Seats : FreeUsers;
        return count + " / " + cap + " kullanıcı";
    }

    static void ApplyKey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && LicenseCodec.TryRead(key, out var info))
        {
            IsLicensed = true;
            LicenseName = info.Name;
            Seats = info.Seats;
            Limit = info.Seats == 0 ? int.MaxValue : info.Seats;
            return;
        }

        IsLicensed = false;
        LicenseName = "";
        Seats = 0;
        Limit = FreeUsers;
    }

    static void RefreshClock()
    {
        var start = new DateTime(_store?.Current.TrialStartUtc ?? DateTime.UtcNow.Ticks, DateTimeKind.Utc);
        var end = start.AddDays(TrialDays);
        var now = DateTime.UtcNow;
        TrialEnded = now >= end;
        TrialDaysLeft = TrialEnded ? 0 : Math.Max(1, (int)Math.Ceiling((end - now).TotalDays));
    }
}
