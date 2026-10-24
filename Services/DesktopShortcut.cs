using System.Runtime.InteropServices;
using System.Text;

namespace OrtakAlan;

static class DesktopShortcut
{
    const string FolderName = "Paylaşılan Dosyalar";

    public static void Ensure(bool refresh)
    {
        _ = refresh;
        try
        {
            var exe = Exe();
            if (exe.Length == 0) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var work = Path.GetDirectoryName(exe);
            Write(Path.Combine(desktop, "Ortak Alan.lnk"), exe, "", work, AppIcon(work, exe), "Ortak Alan");
            var folder = SharedFolder(desktop);
            RemoveOldLink(desktop);
            TakeConnected(desktop, folder, exe);
            Dress(folder);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    public static string CreateFolder(string person, string folderName, string peerId, string shareId)
    {
        var label = Place(person, folderName, peerId, shareId);
        Remember(peerId, shareId, person, folderName, label);
        return label;
    }

    public static void Maintain(IReadOnlyList<PeerSnapshot> peers)
    {
        List<KeptFolder> kept;
        try { kept = Load(); }
        catch (Exception ex)
        {
            Log.Write(ex);
            return;
        }

        var changed = false;
        foreach (var item in kept.ToList())
        {
            var peer = peers.FirstOrDefault(p => p.Id == item.PeerId);
            if (peer is not { Online: true }) continue;
            var open = peer.Shares.Any(share => share.Kind == "folder" && share.Id == item.ShareId);
            if (open)
            {
                var path = Path.Combine(SharedFolder(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), item.Label + ".lnk");
                if (!File.Exists(path))
                    Place(item.Person, item.Folder, item.PeerId, item.ShareId);
                continue;
            }

            RemoveLink(item.Label);
            kept.Remove(item);
            changed = true;
        }

        if (changed) Save(kept);
    }

    static string Place(string person, string folderName, string peerId, string shareId)
    {
        var exe = Exe();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var label = Clean(person) + " - " + Clean(folderName);
        if (exe.Length == 0) return label;
        var folder = SharedFolder(desktop);
        var folderAttrs = File.GetAttributes(folder);
        if (folderAttrs.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(folder, folderAttrs & ~FileAttributes.ReadOnly);
        var icon = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",4";
        Write(
            Path.Combine(folder, label + ".lnk"),
            exe,
            "--klasor \"" + peerId + "\" \"" + shareId + "\"",
            Path.GetDirectoryName(exe),
            icon,
            person + " / " + folderName);
        var stray = Path.Combine(desktop, label + ".lnk");
        if (File.Exists(stray)) File.Delete(stray);
        Dress(folder);
        SharedFiles.Refresh(folder);
        return label;
    }

    public static void Write(string linkPath, string target, string? arguments, string? workingDirectory, string? icon, string description)
    {
        var directory = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lnk");
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetArguments(arguments ?? "");
        link.SetShowCmd(1);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            link.SetWorkingDirectory(workingDirectory);
        if (!string.IsNullOrWhiteSpace(description))
            link.SetDescription(description);
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var split = icon.LastIndexOf(',');
            var file = split > 1 ? icon[..split] : icon;
            var index = 0;
            if (split > 1) int.TryParse(icon[(split + 1)..], out index);
            link.SetIconLocation(file, index);
        }
        ((IPersistFile)link).Save(temp, true);
        if (File.Exists(linkPath))
        {
            File.SetAttributes(linkPath, FileAttributes.Normal);
            File.Delete(linkPath);
        }
        File.Move(temp, linkPath);
        try { SHChangeNotify(0x2000, 0x0005, linkPath, IntPtr.Zero); }
        catch { /* simge yenilenemezse kısayol yine durur */ }
    }

    static string AppIcon(string? work, string exe)
    {
        if (!string.IsNullOrEmpty(work))
        {
            var file = Path.Combine(work, "ikon.ico");
            if (File.Exists(file)) return file;
        }
        return exe + ",0";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr item);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string file, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    static string SharedFolder(string desktop)
    {
        var folder = Path.Combine(desktop, FolderName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    static void RemoveOldLink(string desktop)
    {
        foreach (var name in new[] { "Paylaşılan Dosyalar.lnk", "Paylasilan Dosyalar.lnk" })
        {
            var path = Path.Combine(desktop, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static void TakeConnected(string desktop, string folder, string exe)
    {
        foreach (var link in Directory.EnumerateFiles(desktop, "*.lnk").ToList())
        {
            var info = Read(link);
            if (info == null || info.Value.Args.IndexOf("--klasor", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var dest = Path.Combine(folder, Path.GetFileName(link));
            if (!string.Equals(link, dest, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(link, dest);
            }
            Write(dest, exe, info.Value.Args, Path.GetDirectoryName(exe), info.Value.Icon, info.Value.Description);
        }

        if (!Directory.Exists(folder)) return;
        foreach (var link in Directory.EnumerateFiles(folder, "*.lnk").ToList())
        {
            var info = Read(link);
            if (info == null || info.Value.Args.IndexOf("--klasor", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var icon = string.IsNullOrWhiteSpace(info.Value.Icon)
                ? Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",4"
                : info.Value.Icon;
            Write(link, exe, info.Value.Args, Path.GetDirectoryName(exe), icon, info.Value.Description);
        }
    }

    static void Dress(string folder)
    {
        try
        {
            var ini = Path.Combine(folder, "desktop.ini");
            var icon = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",4";
            var text = "[.ShellClassInfo]\r\nIconResource=" + icon + "\r\nInfoTip=Bağlanan paylaşılan klasörler\r\n";
            if (File.Exists(ini))
                File.SetAttributes(ini, FileAttributes.Normal);
            File.WriteAllText(ini, text, new UnicodeEncoding(false, true));
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            var attrs = File.GetAttributes(folder);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(folder, attrs & ~FileAttributes.ReadOnly);
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.System);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    static void RemoveLink(string label)
    {
        if (label.Length == 0) return;
        var path = Path.Combine(SharedFolder(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), label + ".lnk");
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            SharedFiles.Refresh(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    static void Remember(string peerId, string shareId, string person, string folder, string label)
    {
        var kept = Load();
        var found = kept.FirstOrDefault(item => item.PeerId == peerId && item.ShareId == shareId);
        if (found == null)
            kept.Add(new KeptFolder { PeerId = peerId, ShareId = shareId, Person = person, Folder = folder, Label = label });
        else
        {
            found.Person = person;
            found.Folder = folder;
            found.Label = label;
        }
        Save(kept);
    }

    static List<KeptFolder> Load()
    {
        var path = StorePath();
        if (!File.Exists(path)) return new();
        return System.Text.Json.JsonSerializer.Deserialize<List<KeptFolder>>(File.ReadAllText(path), SettingsService.JsonOptions) ?? new();
    }

    static void Save(List<KeptFolder> kept)
    {
        var path = StorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(kept, SettingsService.JsonOptions));
    }

    static string StorePath() => Path.Combine(App.Me.Store.DirectoryPath, "baglantilar.json");

    static (string Args, string Icon, string Description)? Read(string linkPath)
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            File.Copy(linkPath, temp, true);
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(temp);
            return ((string)shortcut.Arguments, (string)shortcut.IconLocation, (string)shortcut.Description);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* geçici */ }
        }
    }

    static string Exe()
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current)) return current;
        var installed = AppInfo.InstalledExe;
        return File.Exists(installed) ? installed : current ?? "";
    }

    sealed class KeptFolder
    {
        public string PeerId { get; set; } = "";
        public string ShareId { get; set; } = "";
        public string Person { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Label { get; set; } = "";
    }

    static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return text.Length == 0 ? "Paylaşım" : text;
    }
}
