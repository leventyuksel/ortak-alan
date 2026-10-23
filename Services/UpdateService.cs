using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OrtakAlan;

sealed record UpdateOffer(Version Version, string Name, string Url);

static class UpdateService
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OrtakAlan", AppInfo.Display));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<UpdateOffer?> CheckAsync()
    {
        try
        {
            var address = "https://api.github.com/repos/" + AppInfo.Owner + "/" + AppInfo.Repo + "/releases/latest";
            using var response = await Http.GetAsync(address);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (version <= AppInfo.Current) return null;

            string? url = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(url)) return null;
            var title = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : tag;
            return new UpdateOffer(version, title ?? tag, url);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
    }

    public static async Task ApplyAsync(UpdateOffer offer)
    {
        var root = Path.Combine(Path.GetTempPath(), "OrtakAlanGuncelleme");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        var zipPath = Path.Combine(root, "paket.zip");
        using (var download = await Http.GetStreamAsync(offer.Url))
        using (var file = File.Create(zipPath))
            await download.CopyToAsync(file);

        var extract = Path.Combine(root, "dosyalar");
        ZipFile.ExtractToDirectory(zipPath, extract);
        var source = Directory.GetFiles(extract, "OrtakAlan.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (source == null) throw new InvalidOperationException("Paket içinde OrtakAlan.exe yok.");
        var sourceDir = Path.GetDirectoryName(source)!;

        Directory.CreateDirectory(AppInfo.InstallDir);
        var script = Path.Combine(root, "uygula.ps1");
        var scriptText =
            "$ErrorActionPreference = 'Stop'\r\n" +
            "Wait-Process -Id " + Environment.ProcessId + " -ErrorAction SilentlyContinue\r\n" +
            "Start-Sleep -Milliseconds 500\r\n" +
            "Copy-Item -LiteralPath '" + sourceDir.Replace("'", "''") + "\\*' -Destination '" + AppInfo.InstallDir.Replace("'", "''") + "' -Recurse -Force\r\n" +
            "Start-Process -FilePath '" + AppInfo.InstalledExe.Replace("'", "''") + "'\r\n";
        File.WriteAllText(script, scriptText, new System.Text.UTF8Encoding(true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    static int _apply;

    public static bool Applying => Volatile.Read(ref _apply) == 1;
    public static UpdateOffer? Pending { get; private set; }
    public static event Action? PendingChanged;

    public static async Task LookAsync()
    {
        Pending = await CheckAsync();
        var handler = PendingChanged;
        if (handler == null) return;
        foreach (var item in handler.GetInvocationList())
        {
            try { ((Action)item)(); }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    public static async Task<string?> ApplyPendingAsync()
    {
        if (Interlocked.CompareExchange(ref _apply, 1, 0) != 0)
            return "İndiriliyor…";
        var offer = Pending;
        if (offer == null)
        {
            Interlocked.Exchange(ref _apply, 0);
            return "Güncelleme yok.";
        }

        try
        {
            await ApplyAsync(offer);
            return null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Interlocked.Exchange(ref _apply, 0);
            return "Güncelleme indirilemedi.";
        }
    }
}
