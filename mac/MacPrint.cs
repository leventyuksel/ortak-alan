using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OrtakAlan.Mac;

static class MacPrint
{
    const int FirstPort = 47631;
    const int LastPort = 47730;

    static readonly object Gate = new();
    static MacNode? _node;
    static List<QueueItem> _items = new();
    static readonly Dictionary<string, (long Length, DateTime At)> Stable = new(StringComparer.Ordinal);
    static readonly HashSet<string> Told = new(StringComparer.Ordinal);
    static readonly HashSet<int> Listening = new();

    public static event Action<string>? Status;

    static string Spool => Path.Combine(Desk.Current.DirectoryPath, "kuyruk");
    static string MapPath => Path.Combine(Desk.Current.DirectoryPath, "yazicilar.json");

    public static void Start(MacNode node)
    {
        _node = node;
        Load();
        foreach (var item in Snapshot())
            Listen(item);
        _ = Task.Run(Loop);
    }

    public static async Task<string> AddQueue(string peerId, string peerName, string shareId, string shareName)
    {
        var queue = QueueName(peerName, shareName);
        QueueItem item;
        lock (Gate)
        {
            item = _items.FirstOrDefault(q => q.Peer == peerId && q.Share == shareId)
                   ?? _items.FirstOrDefault(q => q.Queue == queue)
                   ?? new QueueItem();
            if (item.Port == 0)
            {
                item.Peer = peerId;
                item.Share = shareId;
                item.Queue = queue;
                item.Port = FreePort();
                _items.Add(item);
                Save();
            }
            else
            {
                item.Peer = peerId;
                item.Share = shareId;
                item.Queue = queue;
                Save();
            }
        }
        Listen(item);
        var uri = "socket://127.0.0.1:" + item.Port;
        if (QueueExists(queue, uri))
            return "\"" + queue + "\" yazıcılar listesinde. Word, Safari ve diğer programlardan bu yazıcıyı seçebilirsiniz.";

        var error = await Install(queue, uri);
        return error ?? "\"" + queue + "\" yazıcılar listesine eklendi. Word, Safari ve diğer programlardan bu yazıcıyı seçebilirsiniz.";
    }

    static int FreePort()
    {
        var used = _items.Select(q => q.Port).ToHashSet();
        for (var port = FirstPort; port <= LastPort; port++)
            if (!used.Contains(port)) return port;
        return LastPort;
    }

    static void Listen(QueueItem item)
    {
        lock (Gate)
        {
            if (!Listening.Add(item.Port)) return;
        }
        _ = Task.Run(() => AcceptLoop(item));
    }

    static async Task AcceptLoop(QueueItem item)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, item.Port);
            listener.Start();
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => Capture(client, item));
            }
        }
        catch
        {
            lock (Gate) Listening.Remove(item.Port);
        }
        finally
        {
            listener?.Stop();
        }
    }

    static void Capture(TcpClient client, QueueItem item)
    {
        var folder = Path.Combine(Spool, item.Peer + "_" + item.Share);
        var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bin");
        try
        {
            Directory.CreateDirectory(folder);
            using var stream = client.GetStream();
            using var file = File.Create(path);
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > 150L * 1024 * 1024)
                {
                    file.Dispose();
                    TryDelete(path);
                    return;
                }
                file.Write(buffer, 0, read);
            }
        }
        catch
        {
            TryDelete(path);
        }
        finally
        {
            client.Dispose();
        }
    }

    static bool QueueExists(string queue, string uri)
    {
        var text = Run("lpstat", "-v");
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(uri, StringComparison.Ordinal) && line.Contains(queue, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    static async Task<string?> Install(string queue, string uri)
    {
        var dir = "/tmp/ortakalan-kur";
        Directory.CreateDirectory(dir);
        var ppd = Path.Combine(dir, "ortakalan.ppd");
        var script = Path.Combine(dir, "kur.sh");
        File.WriteAllText(ppd, Ppd.Replace("\r\n", "\n"), new UTF8Encoding(false));
        var body = "#!/bin/bash\nset -e\n" +
                   "lpadmin -p " + Quote(queue) + " -D " + Quote(queue) + " -L " + Quote("Ortak Alan") +
                   " -E -v " + Quote(uri) + " -P " + Quote(ppd) + " -o printer-is-shared=false\n" +
                   "cupsenable " + Quote(queue) + "\n" +
                   "cupsaccept " + Quote(queue) + "\n";
        File.WriteAllText(script, body, new UTF8Encoding(false));

        var start = new System.Diagnostics.ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("do shell script \"bash " + script + "\" with administrator privileges");
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return "Yazıcı eklenemedi.";
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return null;
            if (error.Contains("cancel", StringComparison.OrdinalIgnoreCase) || error.Contains("iptal", StringComparison.OrdinalIgnoreCase))
                return "Yazıcı eklenmedi. macOS izin penceresinde onay gerekir.";
            return error.Trim().Length == 0 ? "Yazıcı eklenemedi." : error.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static async Task Loop()
    {
        while (true)
        {
            try
            {
                Desk.Current.SyncPrinters();
                await Pump();
            }
            catch
            {
                /* sonraki tur */
            }
            await Task.Delay(1000);
        }
    }

    static async Task Pump()
    {
        var node = _node;
        if (node == null || !Directory.Exists(Spool)) return;
        foreach (var folder in Directory.EnumerateDirectories(Spool))
        {
            var name = Path.GetFileName(folder);
            var split = name.IndexOf('_');
            if (split <= 0 || split >= name.Length - 1) continue;
            var peer = name[..split];
            var share = name[(split + 1)..];
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (!Ready(file)) continue;
                var pdf = EnsurePdf(file);
                if (pdf == null)
                {
                    if (Told.Add(file)) Status?.Invoke("Çıktı PDF’ye çevrilemedi.");
                    continue;
                }
                string? error;
                try { error = await node.SendPrint(peer, share, pdf); }
                catch (Exception ex) { error = ex.Message; }
                if (pdf != file) TryDelete(pdf);
                if (error == null)
                {
                    TryDelete(file);
                    Told.Remove(file);
                    Status?.Invoke("Çıktı gitti.");
                    continue;
                }
                if (error == "Çevrimdışı") continue;
                if (Told.Add(file)) Status?.Invoke(error);
            }
        }
    }

    static bool Ready(string path)
    {
        var info = new FileInfo(path);
        if (info.Length < 8 || info.Length > 150L * 1024 * 1024) return false;
        if (!Stable.TryGetValue(path, out var mark) || mark.Length != info.Length)
        {
            Stable[path] = (info.Length, DateTime.UtcNow);
            return false;
        }
        return DateTime.UtcNow - mark.At >= TimeSpan.FromMilliseconds(700);
    }

    static string? EnsurePdf(string path)
    {
        if (IsPdf(path)) return path;
        var mark = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(mark, 0, 4) < 4) return null;
        }
        if (mark[0] == (byte)'%' && mark[1] == (byte)'P' && mark[2] == (byte)'D' && mark[3] == (byte)'F') return null;
        if (mark[0] != (byte)'%' || mark[1] != (byte)'!') return null;
        var dest = path + ".pdf";
        var start = new System.Diagnostics.ProcessStartInfo("/usr/sbin/cupsfilter")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("application/pdf");
        start.ArgumentList.Add(path);
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return null;
            using var output = File.Create(dest);
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit(20000);
            if (process.ExitCode == 0 && IsPdf(dest)) return dest;
        }
        catch
        {
            /* dönüşüm yok */
        }
        TryDelete(dest);
        return null;
    }

    static bool IsPdf(string path)
    {
        try
        {
            var head = new byte[5];
            using var stream = File.OpenRead(path);
            if (stream.Read(head, 0, 5) < 5) return false;
            if (head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F') return false;
            var tailLen = (int)Math.Min(8192, stream.Length);
            var tail = new byte[tailLen];
            stream.Position = stream.Length - tailLen;
            if (stream.Read(tail, 0, tailLen) < tailLen) return false;
            return Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static string Run(string file, string arg)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start);
            var text = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(4000);
            return text;
        }
        catch
        {
            return "";
        }
    }

    static string QueueName(string peer, string share)
    {
        var raw = (peer.Length == 0 ? "Ortak Alan" : peer) + " - " + share;
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' ? ch : ' ').ToArray();
        var name = string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) name = "Ortak Alan";
        if (name.Length > 80) name = name[..80].Trim();
        return name;
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* sonraki deneme */ }
    }

    static List<QueueItem> Snapshot()
    {
        lock (Gate) return _items.ToList();
    }

    static void Load()
    {
        try
        {
            if (!File.Exists(MapPath)) return;
            _items = JsonSerializer.Deserialize<List<QueueItem>>(File.ReadAllText(MapPath)) ?? new();
        }
        catch
        {
            _items = new();
        }
    }

    static void Save()
    {
        var temp = MapPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items));
        File.Move(temp, MapPath, true);
    }

    sealed class QueueItem
    {
        public string Peer { get; set; } = "";
        public string Share { get; set; } = "";
        public string Queue { get; set; } = "";
        public int Port { get; set; }
    }

    const string Ppd = """
        *PPD-Adobe: "4.3"
        *FormatVersion: "4.3"
        *FileVersion: "1.0"
        *LanguageVersion: English
        *LanguageEncoding: ISOLatin1
        *PCFileName: "ORTAK.PPD"
        *Manufacturer: "Ortak Alan"
        *Product: "(Ortak Alan)"
        *ModelName: "Ortak Alan"
        *ShortNickName: "Ortak Alan"
        *NickName: "Ortak Alan"
        *PSVersion: "(3010.000) 0"
        *LanguageLevel: "3"
        *ColorDevice: True
        *DefaultColorSpace: RGB
        *FileSystem: False
        *Throughput: "1"
        *LandscapeOrientation: Plus90
        *TTRasterizer: Type42
        *cupsVersion: 1.4
        *cupsFilter: "application/vnd.cups-pdf 0 -"
        *cupsFilter: "application/pdf 0 -"
        *OpenUI *PageSize/Page Size: PickOne
        *OrderDependency: 10 AnySetup *PageSize
        *DefaultPageSize: A4
        *PageSize A4/A4: "<</PageSize[595 842]>>setpagedevice"
        *PageSize Letter/Letter: "<</PageSize[612 792]>>setpagedevice"
        *CloseUI: *PageSize
        *DefaultImageableArea: A4
        *ImageableArea A4: "0 0 595 842"
        *ImageableArea Letter: "0 0 612 792"
        *DefaultPaperDimension: A4
        *PaperDimension A4: "595 842"
        *PaperDimension Letter: "612 792"
        """;
}
