using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace OrtakAlan;

readonly record struct QueueResult(bool Ok, bool Created, string Message);

static class LocalPrinters
{
    const int MaxBody = 150 * 1024 * 1024;
    const string Driver = "Microsoft IPP Class Driver";

    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly SemaphoreSlim SendGate = new(1, 1);
    static readonly object ListLock = new();
    static readonly Dictionary<string, long> Seen = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, (long Length, DateTime At)> Stable = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> JobNames = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Told = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTime> Hold = new(StringComparer.OrdinalIgnoreCase);

    static List<PrinterLink> _links = new();
    static string _dataDir = "";
    static string _path = "";
    static volatile HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    static Func<string, string, string, Task<string?>>? _send;
    static Action<string, string>? _notify;
    static CancellationTokenSource? _loopStop;
    static int _started;

    public static void Start(string dataDir, Func<string, string, string, Task<string?>> send, Action<string, string> notify)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _dataDir = dataDir;
        _path = Path.Combine(dataDir, "yazicilar.json");
        _send = send;
        _notify = notify;
        Load();
        _ = Task.Run(Restore);
        _loopStop = new CancellationTokenSource();
        _ = Loop(_loopStop.Token);
    }

    public static void Stop()
    {
        try { _loopStop?.Cancel(); } catch { /* kapanış */ }
    }

    public static void Note(IReadOnlyList<PeerSnapshot> peers)
    {
        _online = peers.Where(peer => peer.Online).Select(peer => peer.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_started == 1) _ = Pump();
    }

    public static bool IsVirtual(string name)
    {
        lock (ListLock)
            return _links.Any(link => string.Equals(link.Queue, name, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<QueueResult> EnsureAsync(string peerId, string peerName, string shareId, string shareName, bool online)
    {
        if (peerId.Length == 0 || shareId.Length == 0)
            return new QueueResult(false, false, "Yazıcı bulunamadı.");
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return Ensure(peerId, peerName, shareId, shareName, online);
        }
        finally
        {
            Gate.Release();
        }
    }

    static QueueResult Ensure(string peerId, string peerName, string shareId, string shareName, bool online)
    {
        if (_dataDir.Length == 0)
            return new QueueResult(false, false, "Yazıcı kuyruğu henüz hazır değil.");

        PrinterLink link;
        var createdLink = false;
        lock (ListLock)
        {
            link = _links.FirstOrDefault(item => item.PeerId == peerId && item.ShareId == shareId) ?? new PrinterLink();
            if (link.PeerId.Length == 0)
            {
                link.PeerId = peerId;
                link.ShareId = shareId;
                link.Queue = QueueName(peerName, shareName);
                link.PortFile = Path.Combine(PortDir(), Safe(peerId) + "_" + Safe(shareId) + ".pdf");
                createdLink = true;
                _links.Add(link);
            }
            if (link.PortFile.Length == 0)
                link.PortFile = Path.Combine(PortDir(), Safe(peerId) + "_" + Safe(shareId) + ".pdf");
            link.PeerName = Clean(peerName);
            link.ShareName = Clean(shareName);
        }

        Directory.CreateDirectory(PortDir());
        var existed = PrinterRelay.Installed(link.Queue);
        if (existed && string.Equals(PortOf(link.Queue), link.PortFile, StringComparison.OrdinalIgnoreCase))
        {
            Save();
            return new QueueResult(true, false, Describe(link.Queue, false, online));
        }

        var added = Install(link.Queue, link.PortFile);
        if (added == InstallState.NoDriver)
        {
            if (createdLink) Forget(link);
            return new QueueResult(false, false, "Bu bilgisayarda yazıcı kuyruğu için hazır sürücü yok.");
        }
        if (added == InstallState.Failed || !PrinterRelay.Installed(link.Queue))
        {
            if (createdLink) Forget(link);
            return new QueueResult(false, false, "Windows yazıcı kuyruğunu eklemedi.");
        }

        Save();
        return new QueueResult(true, !existed, Describe(link.Queue, !existed, online));
    }

    static async Task Restore()
    {
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            List<PrinterLink> links;
            lock (ListLock) links = _links.ToList();
            foreach (var link in links)
                Ensure(link.PeerId, link.PeerName.Length == 0 ? "Bilgisayar" : link.PeerName, link.ShareId, link.ShareName, false);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    static async Task Loop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Grab();
                await Pump().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Kapanış.
        }
    }

    static void Grab()
    {
        List<PrinterLink> links;
        lock (ListLock) links = _links.ToList();
        foreach (var link in links)
        {
            try { Take(link); }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    static void Take(PrinterLink link)
    {
        var path = link.PortFile;
        if (path.Length == 0 || !File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length < 8 || info.Length > MaxBody)
        {
            if (info.Length > MaxBody) Tell("Yazıcı", "Yazdırma işi 150 MB sınırını aşıyor.");
            return;
        }

        var (count, document) = Spool.Peek(link.Queue);
        if (count > 0)
        {
            if (document.Length > 0) JobNames[link.Queue] = document;
            Stable.Remove(path);
            return;
        }

        if (!Stable.TryGetValue(path, out var mark) || mark.Length != info.Length)
        {
            Stable[path] = (info.Length, DateTime.UtcNow);
            return;
        }
        if (DateTime.UtcNow - mark.At < TimeSpan.FromMilliseconds(700)) return;

        var stamp = info.Length + info.LastWriteTimeUtc.Ticks;
        if (Seen.TryGetValue(path, out var seen) && seen == stamp) return;
        JobNames.TryGetValue(link.Queue, out var jobName);
        var folder = Path.Combine(_dataDir, "kuyruk", Safe(link.PeerId) + "_" + Safe(link.ShareId));
        Directory.CreateDirectory(folder);
        var dest = Unique(folder, FileName(jobName ?? "", ".pdf"));
        var copied = false;
        try
        {
            copied = CopyPdf(path, dest);
        }
        catch
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* sonraki deneme */ }
            throw;
        }
        if (!copied)
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* sonraki deneme */ }
            return;
        }

        Seen[path] = stamp;
        try { File.Delete(path); } catch (Exception ex) { Log.Write(ex); }
        Log.Write("yazıcı işi " + info.Length + " " + dest);
        if (!_online.Contains(link.PeerId))
            Tell(link.PeerName.Length == 0 ? "Yazıcı" : link.PeerName, link.ShareName + " için yazdırma işi bekliyor.");
        _ = Pump();
    }

    static bool CopyPdf(string source, string dest)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length < 8 || input.Length > MaxBody) return false;
        var head = new byte[5];
        if (input.Read(head, 0, 5) < 5) return false;
        if (head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F') return false;
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(head, 0, 5);
        input.CopyTo(output);
        output.Flush();
        var tailLen = (int)Math.Min(8192, output.Length);
        var tail = new byte[tailLen];
        output.Position = output.Length - tailLen;
        if (output.Read(tail, 0, tailLen) < tailLen) return false;
        return Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
    }

    static async Task Pump()
    {
        if (_send == null || _dataDir.Length == 0) return;
        if (!await SendGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var root = Path.Combine(_dataDir, "kuyruk");
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(folder);
                if (name == "baglanti") continue;
                var split = name.IndexOf('_');
                if (split <= 0) continue;
                var peer = name[..split];
                var share = name[(split + 1)..];
                if (!_online.Contains(peer)) continue;
                if (Hold.TryGetValue(peer, out var until) && until > DateTime.UtcNow) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    string? error;
                    try { error = await _send(peer, share, file).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Log.Write(ex);
                        error = "Bağlanılamadı";
                    }
                    if (error == null)
                    {
                        try { File.Delete(file); } catch (Exception ex) { Log.Write(ex); }
                        Tell("Yazıcı", Path.GetFileName(file) + " yazıcıya ulaştı.");
                        continue;
                    }
                    if (error == "Çevrimdışı") continue;
                    Hold[peer] = DateTime.UtcNow.Add(error.Contains("reddetti", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(25));
                    if (Told.Add(file)) Tell("Yazıcı", error);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            SendGate.Release();
        }
    }

    static string PortDir() => Path.Combine(_dataDir, "kuyruk", "baglanti");

    static string PortOf(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers\" + name);
            return key?.GetValue("Port") as string ?? "";
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "";
        }
    }

    static string QueueName(string peerName, string shareName)
    {
        var left = Clean(peerName);
        var right = Clean(shareName);
        var name = left.Length == 0 ? right : right.Length == 0 ? left : left + " - " + right;
        if (name.Length == 0) name = "Ortak Alan";
        if (name.Length > 110) name = name[..110].Trim();
        var unique = name;
        for (var n = 2; n < 40; n++)
        {
            var taken = PrinterRelay.Installed(unique);
            if (!taken)
            {
                lock (ListLock)
                    taken = _links.Any(link => string.Equals(link.Queue, unique, StringComparison.OrdinalIgnoreCase));
            }
            if (!taken) return unique;
            var suffix = " (" + n + ")";
            var stem = name.Length + suffix.Length > 110 ? name[..(110 - suffix.Length)].Trim() : name;
            unique = stem + suffix;
        }
        return unique;
    }

    static string Describe(string queue, bool created, bool online)
    {
        var quoted = "\"" + queue + "\"";
        if (created)
            return online
                ? quoted + " Yazıcılar klasörüne eklendi. Word ve diğer programlardan bu yazıcıyı seçebilirsiniz."
                : quoted + " Yazıcılar klasörüne eklendi. Karşı bilgisayar çevrimiçi olunca yazdırdığınız işler gider.";
        return online
            ? quoted + " Yazıcılar klasöründe. Word ve diğer programlardan bu yazıcıyı seçebilirsiniz."
            : quoted + " Yazıcılar klasöründe. Karşı bilgisayar çevrimiçi olunca yazdırdığınız işler gider.";
    }

    static InstallState Install(string name, string portFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(portFile)!);
        var portError = Spool.AddLocalPort(portFile);
        if (portError != 0 && portError != 183)
        {
            Log.Write("yazıcı bağlantı noktası " + portError);
            return InstallState.Failed;
        }

        if (PrinterRelay.Installed(name))
        {
            if (string.Equals(PortOf(name), portFile, StringComparison.OrdinalIgnoreCase))
                return InstallState.Ok;
            var moved = Spool.SetPort(name, portFile);
            Log.Write("yazıcı bağlantı noktası güncelle " + moved);
            return moved == 0 ? InstallState.Ok : InstallState.Failed;
        }

        var added = Spool.AddQueue(name, portFile, Driver);
        Log.Write("yazıcı ekle " + added);
        if (added == 1797) return InstallState.NoDriver;
        if (added != 0 && added != 1802) return InstallState.Failed;
        return PrinterRelay.Installed(name) ? InstallState.Ok : InstallState.Failed;
    }

    static void Forget(PrinterLink link)
    {
        lock (ListLock) _links.Remove(link);
        Save();
    }

    static void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<PrinterFile>(File.ReadAllText(_path), SettingsService.JsonOptions);
            if (file?.Links == null) return;
            lock (ListLock)
                _links = file.Links.Where(link => link.PeerId.Length > 0 && link.ShareId.Length > 0 && link.Queue.Length > 0).ToList();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    static void Save()
    {
        try
        {
            if (_path.Length == 0) return;
            PrinterFile file;
            lock (ListLock) file = new PrinterFile { Links = _links.ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(file, SettingsService.JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    static void Tell(string title, string text)
    {
        try { _notify?.Invoke(title, text); }
        catch (Exception ex) { Log.Write(ex); }
    }

    static string Clean(string value)
    {
        var text = new string(value.Select(c => c < 32 || "\\/:*?\"<>|".Contains(c) ? ' ' : c).ToArray());
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Trim().TrimEnd('.');
    }

    static string Safe(string value)
    {
        var text = new string(value.Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
        return text.Length == 0 ? "y" : text;
    }

    static string FileName(string jobName, string ext)
    {
        var name = Clean(jobName);
        if (name.Length == 0) name = "belge";
        if (name.Length > 80) name = name[..80].Trim();
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
        return name;
    }

    static string Unique(string folder, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, name + ext);
        for (var n = 2; File.Exists(candidate) && n < 1000; n++)
            candidate = Path.Combine(folder, name + " (" + n + ")" + ext);
        return candidate;
    }

    enum InstallState { Ok, NoDriver, Failed }

    sealed class PrinterLink
    {
        public string PeerId { get; set; } = "";
        public string ShareId { get; set; } = "";
        public string PeerName { get; set; } = "";
        public string ShareName { get; set; } = "";
        public string Queue { get; set; } = "";
        public string PortFile { get; set; } = "";
    }

    sealed class PrinterFile
    {
        public List<PrinterLink> Links { get; set; } = new();
    }

    static class Spool
    {
        public static (int Count, string Name) Peek(string printer)
        {
            if (!OpenPrinter(printer, out var handle, IntPtr.Zero)) return (0, "");
            try
            {
                EnumJobs(handle, 0, 8, 1, IntPtr.Zero, 0, out var needed, out _);
                if (needed == 0 || needed > 1024 * 1024) return (0, "");
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!EnumJobs(handle, 0, 8, 1, buffer, needed, out _, out var returned) || returned == 0)
                        return (0, "");
                    var info = Marshal.PtrToStructure<JobInfo1>(buffer);
                    var name = info.Document == IntPtr.Zero ? "" : Marshal.PtrToStringUni(info.Document) ?? "";
                    return ((int)returned, name);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                return (0, "");
            }
            finally
            {
                ClosePrinter(handle);
            }
        }

        public static int AddLocalPort(string port)
        {
            var monitors = new[] { "Local Port", "Yerel Bağlantı Noktası" };
            var last = 0;
            foreach (var monitor in monitors)
            {
                last = AddPort(monitor, port);
                if (last == 0 || last == 183) return last;
            }
            return last;
        }

        public static int AddQueue(string name, string port, string driver)
        {
            var info = new PrinterInfo2
            {
                pPrinterName = name,
                pPortName = port,
                pDriverName = driver,
                pPrintProcessor = "winprint",
                pDatatype = "RAW",
                pComment = "Ortak Alan paylaşılan yazıcı",
                pLocation = "Ortak Alan",
                Priority = 1,
                DefaultPriority = 1
            };
            var handle = AddPrinter(null, 2, ref info);
            if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error();
            ClosePrinter(handle);
            return 0;
        }

        public static int SetPort(string name, string port)
        {
            var defaults = new PrinterDefaults { Access = 4 };
            if (!OpenPrinterWith(name, out var handle, ref defaults))
            {
                defaults.Access = 0xF000C;
                if (!OpenPrinterWith(name, out handle, ref defaults))
                    return Marshal.GetLastWin32Error();
            }
            try
            {
                GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
                if (needed == 0 || needed > 1_000_000) return Marshal.GetLastWin32Error();
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!GetPrinter(handle, 2, buffer, needed, out _))
                        return Marshal.GetLastWin32Error();
                    var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                    info.pPortName = port;
                    info.pComment = "Ortak Alan paylaşılan yazıcı";
                    info.pLocation = "Ortak Alan";
                    var output = Marshal.AllocHGlobal(Marshal.SizeOf<PrinterInfo2>());
                    try
                    {
                        Marshal.StructureToPtr(info, output, false);
                        return SetPrinter(handle, 2, output, 0) ? 0 : Marshal.GetLastWin32Error();
                    }
                    finally
                    {
                        Marshal.DestroyStructure<PrinterInfo2>(output);
                        Marshal.FreeHGlobal(output);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                ClosePrinter(handle);
            }
        }

        public static bool RemoveQueue(string name)
        {
            var defaults = new PrinterDefaults { Access = 4 };
            if (!OpenPrinterWith(name, out var handle, ref defaults))
            {
                defaults.Access = 0xF000C;
                if (!OpenPrinterWith(name, out handle, ref defaults)) return false;
            }
            try { return DeletePrinter(handle); }
            finally { ClosePrinter(handle); }
        }

        static int AddPort(string monitor, string port)
        {
            var defaults = new PrinterDefaults { Access = 1 };
            if (!OpenPrinterWith(",XcvMonitor " + monitor, out var handle, ref defaults))
                return Marshal.GetLastWin32Error();
            try
            {
                var data = Encoding.Unicode.GetBytes(port + "\0");
                var input = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, input, data.Length);
                    if (!XcvData(handle, "AddPort", input, (uint)data.Length, IntPtr.Zero, 0, out _, out var status))
                        return Marshal.GetLastWin32Error();
                    return (int)status;
                }
                finally
                {
                    Marshal.FreeHGlobal(input);
                }
            }
            finally
            {
                ClosePrinter(handle);
            }
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
        static extern bool OpenPrinterWith(string name, out IntPtr handle, ref PrinterDefaults defaults);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr AddPrinter(string? server, uint level, ref PrinterInfo2 printer);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buffer, uint size, out uint needed);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetPrinter(IntPtr handle, uint level, IntPtr buffer, uint command);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeletePrinter(IntPtr handle);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool XcvData(IntPtr handle, string name, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint needed, out uint status);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool EnumJobs(IntPtr handle, uint first, uint count, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool ClosePrinter(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PrinterDefaults
        {
            public IntPtr Datatype;
            public IntPtr DevMode;
            public int Access;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PrinterInfo2
        {
            public string? pServerName;
            public string? pPrinterName;
            public string? pShareName;
            public string? pPortName;
            public string? pDriverName;
            public string? pComment;
            public string? pLocation;
            public IntPtr pDevMode;
            public string? pSepFile;
            public string? pPrintProcessor;
            public string? pDatatype;
            public string? pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint cJobs;
            public uint AveragePPM;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JobInfo1
        {
            public uint JobId;
            public IntPtr PrinterName;
            public IntPtr MachineName;
            public IntPtr UserName;
            public IntPtr Document;
            public IntPtr Datatype;
            public IntPtr StatusText;
            public uint Status;
            public uint Priority;
            public uint Position;
            public uint TotalPages;
            public uint PagesPrinted;
            public ushort Year;
            public ushort Month;
            public ushort DayOfWeek;
            public ushort Day;
            public ushort Hour;
            public ushort Minute;
            public ushort Second;
            public ushort Milliseconds;
        }
    }
}
