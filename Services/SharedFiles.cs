using System.Runtime.InteropServices;
using System.Text;

namespace OrtakAlan;

sealed record PeerLink(string Id, string Name, string Ip, IReadOnlyList<FolderLink> Folders);

sealed record FolderLink(string ShareId, string Name, string? WindowsShare);

sealed record ShareEntry(string ShareId, string Child);

static class SharedFiles
{
    const string FolderClsid = "{0AFACED1-E828-11D1-9187-B532F1E9575D}";

    static readonly object Gate = new();
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    static int _gen;
    static string _applied = "\u0000";

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrtakAlan",
        "Paylaşılan Dosyalar");

    public static string Gelen => DesktopShared();
    static string IncomingRoot => Path.Combine(Root, "Gelen");

    public static string DesktopShared()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Paylaşılan Dosyalar");
        Directory.CreateDirectory(folder);
        return folder;
    }
    static string Shell32 => Path.Combine(Environment.SystemDirectory, "shell32.dll");

    public static void Prepare()
    {
        lock (Gate)
            UnderMutex(PrepareCore);
    }

    public static void Open()
    {
        try { App.Me.ShowFiles(); }
        catch (Exception ex) { Log.Write(ex); }
    }

    public static void Schedule(IEnumerable<PeerVm> peers)
    {
        var links = new List<PeerLink>();
        foreach (var peer in peers)
        {
            var folders = new List<FolderLink>();
            foreach (var share in peer.Shares)
            {
                if (share.Kind != "folder") continue;
                folders.Add(new FolderLink(share.Id, share.Name, share.WindowsShare));
            }
            links.Add(new PeerLink(peer.Id, peer.Name, peer.Ip, folders));
        }

        var sig = Signature(links);
        if (sig == _applied) return;
        var gen = Interlocked.Increment(ref _gen);
        Task.Run(() =>
        {
            lock (Gate)
            {
                if (gen != _gen) return;
                try
                {
                    UnderMutex(() => SyncCore(links));
                    if (gen == _gen) _applied = sig;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                }
            }
        });
    }

    public static string TakeIncoming(string tempPath, string peerName, string fileName)
    {
        _ = peerName;
        lock (Gate)
        {
            return UnderMutex(() =>
            {
                var dir = DesktopShared();
                var dest = NextFree(dir, SafeFile(fileName));
                File.Move(tempPath, dest);
                Notify(dir);
                return dest;
            });
        }
    }

    public static string? FindIncoming(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var dirs = new[]
        {
            DesktopShared(),
            IncomingRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OrtakAlan")
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
        return null;
    }

    static void PrepareCore()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(IncomingRoot);
        WriteIni(Root, "[.ShellClassInfo]\r\nIconResource=" + Shell32 + ",17\r\nInfoTip=Ağda paylaşılan klasörler\r\n");
        WriteIni(IncomingRoot, "[.ShellClassInfo]\r\nInfoTip=Sohbetten gelen dosyalar\r\n");
    }

    static void SyncCore(List<PeerLink> links)
    {
        PrepareCore();
        var existing = LoadComputers();
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gelen" };
        if (Directory.Exists(Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
                occupied.Add(Path.GetFileName(dir));
        }
        var live = new HashSet<string>(links.Select(p => p.Id), StringComparer.Ordinal);
        foreach (var pair in existing.ToList())
        {
            if (live.Contains(pair.Key)) continue;
            RemoveComputer(pair.Value);
            existing.Remove(pair.Key);
            if (Directory.Exists(pair.Value))
                occupied.Add(Path.GetFileName(pair.Value));
            else
                occupied.Remove(Path.GetFileName(pair.Value));
        }

        foreach (var peer in links.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            try { SyncPeer(peer, existing, occupied); }
            catch (Exception ex) { Log.Write(ex); }
        }
        Notify(Root);
    }

    static void SyncPeer(PeerLink peer, Dictionary<string, string> existing, HashSet<string> occupied)
    {
        string dir;
        if (existing.TryGetValue(peer.Id, out var current))
        {
            var currentName = Path.GetFileName(current);
            occupied.Remove(currentName);
            var desired = TakeName(ComputerName(peer.Name), occupied);
            if (!currentName.Equals(desired, StringComparison.OrdinalIgnoreCase))
            {
                var dest = Path.Combine(Root, desired);
                try
                {
                    Directory.Move(current, dest);
                    dir = dest;
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    occupied.Remove(desired);
                    occupied.Add(currentName);
                    dir = current;
                }
            }
            else dir = current;
        }
        else
        {
            dir = Path.Combine(Root, TakeName(ComputerName(peer.Name), occupied));
            Directory.CreateDirectory(dir);
        }

        var previous = ReadManifest(dir);
        var keep = new List<ShareEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in peer.Folders)
        {
            var unc = Unc(peer, folder);
            string child;
            if (unc != null)
            {
                child = TakeChild(Clean(folder.Name), names);
                EnsureUnc(Path.Combine(dir, child), unc);
            }
            else
            {
                child = TakeChild(Clean(folder.Name) + ".lnk", names);
                EnsureApp(Path.Combine(dir, child), peer, folder);
            }
            keep.Add(new ShareEntry(folder.ShareId, child));
        }

        foreach (var old in previous)
        {
            if (keep.Any(item => item.Child.Equals(old.Child, StringComparison.OrdinalIgnoreCase))) continue;
            try { RemoveLink(Path.Combine(dir, old.Child)); }
            catch (Exception ex) { Log.Write(ex); }
        }

        WriteManifest(dir, peer.Id, keep);
        WriteIni(dir, "[.ShellClassInfo]\r\nIconResource=" + Shell32 + ",15\r\nInfoTip=Paylaşılan klasörler\r\n");
        Notify(dir);
    }

    static void EnsureUnc(string path, string unc)
    {
        if (SameSymlink(path, unc) || SameFolderShortcut(path, unc)) return;
        if (Exists(path) && !IsOurLink(path)) return;
        try { RemoveLink(path); }
        catch (Exception ex)
        {
            Log.Write(ex);
            return;
        }
        if (Exists(path)) return;
        if (!TrySymlink(path, unc))
            CreateFolderShortcut(path, unc);
    }

    static void EnsureApp(string path, PeerLink peer, FolderLink folder)
    {
        var exe = AppInfo.PreferredExe();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
        if (Exists(path) && IsDirectory(path))
        {
            if (!IsOurLink(path)) return;
            try { RemoveLink(path); }
            catch (Exception ex)
            {
                Log.Write(ex);
                return;
            }
        }
        var args = "--klasor \"" + peer.Id + "\" \"" + folder.ShareId + "\"";
        DesktopShortcut.Write(path, exe, args, Path.GetDirectoryName(exe), Shell32 + ",4", peer.Name + " / " + folder.Name);
    }

    static bool TrySymlink(string path, string unc)
    {
        try
        {
            Directory.CreateSymbolicLink(path, unc);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            try
            {
                if (IsReparse(path)) Directory.Delete(path);
            }
            catch
            {
                // Bağ kurulamadıysa klasör kısayoluna düşülür.
            }
            return false;
        }
    }

    static void CreateFolderShortcut(string path, string unc)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "hedef.txt"), unc, new UTF8Encoding(false));
        File.SetAttributes(Path.Combine(path, "hedef.txt"), FileAttributes.Hidden);
        DesktopShortcut.Write(Path.Combine(path, "target.lnk"), unc, "", unc, Shell32 + ",3", "Ortak Alan");
        // CLSID2, Gezgin'in bu klasöre girince hedef paylaşımı açmasını sağlar.
        WriteIni(path, "[.ShellClassInfo]\r\nCLSID2=" + FolderClsid + "\r\nFlags=2\r\nConfirmFileOp=0\r\n");
    }

    static bool SameSymlink(string path, string unc)
    {
        try
        {
            if (!IsReparse(path)) return false;
            var target = Directory.ResolveLinkTarget(path, false);
            return target != null && string.Equals(Norm(target.FullName), Norm(unc), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static bool SameFolderShortcut(string path, string unc)
    {
        var hedef = Path.Combine(path, "hedef.txt");
        var link = Path.Combine(path, "target.lnk");
        try
        {
            if (!File.Exists(hedef) || !File.Exists(link)) return false;
            return string.Equals(Norm(File.ReadAllText(hedef).Trim()), Norm(unc), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static void RemoveComputer(string dir)
    {
        if (!Directory.Exists(dir) || IsReparse(dir)) return;
        foreach (var item in ReadManifest(dir))
        {
            try { RemoveLink(Path.Combine(dir, item.Child)); }
            catch (Exception ex) { Log.Write(ex); }
        }
        TryDeleteFile(Path.Combine(dir, ".ortak"));
        TryDeleteFile(Path.Combine(dir, "desktop.ini"));
        ClearDirReadOnly(dir);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    static void RemoveLink(string path)
    {
        if (!Exists(path)) return;
        if (IsReparse(path))
        {
            Directory.Delete(path);
            return;
        }
        if (!IsDirectory(path))
        {
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
            return;
        }
        if (!File.Exists(Path.Combine(path, "hedef.txt"))) return;
        ClearDirReadOnly(path);
        Directory.Delete(path, true);
    }

    static Dictionary<string, string> LoadComputers()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(Root)) return map;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                if (Path.GetFileName(dir).Equals("Gelen", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsReparse(dir)) continue;
                var id = ReadPeerId(dir);
                if (id != null) map[id] = dir;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
        return map;
    }

    static string? ReadPeerId(string dir)
    {
        var path = Path.Combine(dir, ".ortak");
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return null;
        var id = lines[0].Trim();
        return id.Length == 0 ? null : id;
    }

    static List<ShareEntry> ReadManifest(string dir)
    {
        var list = new List<ShareEntry>();
        var path = Path.Combine(dir, ".ortak");
        if (!File.Exists(path)) return list;
        var lines = File.ReadAllLines(path);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab >= line.Length - 1) continue;
            list.Add(new ShareEntry(line[..tab], line[(tab + 1)..].Trim()));
        }
        return list;
    }

    static void WriteManifest(string dir, string peerId, List<ShareEntry> items)
    {
        var id = peerId.Replace("\r", "").Replace("\n", "").Replace("\t", "");
        var text = new StringBuilder();
        text.AppendLine(id);
        foreach (var item in items)
            text.Append(item.ShareId).Append('\t').AppendLine(item.Child);
        var path = Path.Combine(dir, ".ortak");
        if (File.Exists(path))
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        File.SetAttributes(path, FileAttributes.Hidden);
    }

    static void WriteIni(string folder, string body)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "desktop.ini");
        var next = body.EndsWith("\r\n", StringComparison.Ordinal) ? body : body + "\r\n";
        if (!File.Exists(path) || File.ReadAllText(path) != next)
        {
            if (File.Exists(path))
            {
                var fileAttrs = File.GetAttributes(path);
                if (fileAttrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, fileAttrs & ~FileAttributes.ReadOnly);
            }
            File.WriteAllText(path, next, Encoding.Unicode);
        }
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
        if (!IsReparse(folder))
        {
            var dirAttrs = File.GetAttributes(folder);
            File.SetAttributes(folder, dirAttrs | FileAttributes.ReadOnly);
        }
    }

    static string? Unc(PeerLink peer, FolderLink folder)
    {
        if (string.IsNullOrWhiteSpace(folder.WindowsShare)) return null;
        if (string.IsNullOrWhiteSpace(peer.Ip) || peer.Ip == "0.0.0.0") return null;
        return @"\\" + Host(peer.Ip) + @"\" + folder.WindowsShare;
    }

    static string Host(string ip)
    {
        if (ip is "127.0.0.1" or "::1") return Environment.MachineName;
        if (!ip.Contains(':')) return ip;
        var zone = ip.IndexOf('%');
        if (zone >= 0) ip = ip[..zone];
        return ip.Replace(':', '-') + ".ipv6-literal.net";
    }

    static string Signature(List<PeerLink> links)
    {
        var text = new StringBuilder();
        foreach (var peer in links.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            text.Append(peer.Id).Append('|').Append(peer.Name).Append('|').Append(peer.Ip).Append('|');
            foreach (var folder in peer.Folders.OrderBy(f => f.ShareId, StringComparer.Ordinal))
                text.Append(folder.ShareId).Append('=').Append(folder.Name).Append('=').Append(folder.WindowsShare).Append(';');
            text.Append('\n');
        }
        return text.ToString();
    }

    static string ComputerName(string name)
    {
        var text = Clean(name);
        return text.Equals("Gelen", StringComparison.OrdinalIgnoreCase) ? "Gelen PC" : text;
    }

    static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (text.Length == 0) text = "Bilgisayar";
        if (text.Length > 80) text = text[..80].Trim().TrimEnd('.', ' ');
        if (text.Length == 0) text = "Bilgisayar";
        if (Reserved.Contains(text)) text += " PC";
        return text;
    }

    static string SafeFile(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (text.Length == 0) text = "dosya";
        if (text.Length > 120) text = text[..120].Trim().TrimEnd('.', ' ');
        if (text.Length == 0) text = "dosya";
        var stem = Path.GetFileNameWithoutExtension(text);
        if (Reserved.Contains(stem)) text = "_" + text;
        return text;
    }

    static string NextFree(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, stem + "-" + Guid.NewGuid().ToString("N")[..6] + ext);
    }

    static string TakeName(string preferred, HashSet<string> used)
    {
        if (used.Add(preferred)) return preferred;
        for (var n = 2; n < 500; n++)
        {
            var candidate = preferred + " (" + n + ")";
            if (used.Add(candidate)) return candidate;
        }
        var extra = preferred + " " + Guid.NewGuid().ToString("N")[..4];
        used.Add(extra);
        return extra;
    }

    static string TakeChild(string preferred, HashSet<string> used)
    {
        var name = preferred;
        if (name.Equals(".ortak", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            name += " paylaşım";
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; n < 500; n++)
        {
            var candidate = stem + " (" + n + ")" + ext;
            if (used.Add(candidate)) return candidate;
        }
        var extra = stem + " " + Guid.NewGuid().ToString("N")[..4] + ext;
        used.Add(extra);
        return extra;
    }

    static string Norm(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            path = path[4..];
        return path.TrimEnd('\\', '/');
    }

    static void UnderMutex(Action action) => UnderMutex(() =>
    {
        action();
        return 0;
    });

    static T UnderMutex<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, @"Local\OrtakAlan-Paylasilan");
        var got = false;
        try
        {
            try { got = mutex.WaitOne(8000); }
            catch (AbandonedMutexException) { got = true; }
            if (!got) throw new IOException("Paylaşılan klasör meşgul.");
            return action();
        }
        finally
        {
            if (got)
            {
                try { mutex.ReleaseMutex(); }
                catch
                {
                    // Kilit bırakılamazsa sonraki deneme devralır.
                }
            }
        }
    }

    public static void Refresh(string path) => Notify(path);

    static void Notify(string path)
    {
        try { SHChangeNotify(0x1000, 0x0005, path, IntPtr.Zero); }
        catch
        {
            // Gezgin yenilenemezse klasör bir sonraki açılışta günceldir.
        }
    }

    static bool Exists(string path) => TryAttrs(path, out _);
    static bool IsDirectory(string path) => TryAttrs(path, out var attrs) && attrs.HasFlag(FileAttributes.Directory);
    static bool IsReparse(string path) => TryAttrs(path, out var attrs) && attrs.HasFlag(FileAttributes.ReparsePoint);

    static bool IsOurLink(string path)
    {
        if (!TryAttrs(path, out var attrs)) return false;
        if (attrs.HasFlag(FileAttributes.ReparsePoint)) return true;
        if (attrs.HasFlag(FileAttributes.Directory)) return File.Exists(Path.Combine(path, "hedef.txt"));
        return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryAttrs(string path, out FileAttributes attrs)
    {
        try
        {
            attrs = File.GetAttributes(path);
            return true;
        }
        catch
        {
            attrs = 0;
            return false;
        }
    }

    static void ClearDirReadOnly(string path)
    {
        try
        {
            if (!TryAttrs(path, out var attrs) || attrs.HasFlag(FileAttributes.ReparsePoint)) return;
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Silme denemesi kendi hatasını yazar.
        }
    }

    static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr item);
}
