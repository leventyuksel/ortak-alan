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
            Write(Path.Combine(desktop, "Ortak Alan.lnk"), exe, "", work, exe + ",0", "Ortak Alan");
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
        var exe = Exe();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var label = Clean(person) + " - " + Clean(folderName);
        if (exe.Length == 0) return label;
        var folder = SharedFolder(desktop);
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
        return label;
    }

    public static void Write(string linkPath, string target, string? arguments, string? workingDirectory, string? icon, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments ?? "";
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            shortcut.WorkingDirectory = workingDirectory;
        if (!string.IsNullOrWhiteSpace(icon))
            shortcut.IconLocation = icon;
        shortcut.Description = description ?? "";
        shortcut.Save();
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
            File.WriteAllText(ini, text, new UnicodeEncoding(false, true));
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.System | FileAttributes.ReadOnly);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    static (string Args, string Icon, string Description)? Read(string linkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(linkPath);
            return ((string)shortcut.Arguments, (string)shortcut.IconLocation, (string)shortcut.Description);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
    }

    static string Exe()
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current)) return current;
        var installed = AppInfo.InstalledExe;
        return File.Exists(installed) ? installed : current ?? "";
    }

    static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return text.Length == 0 ? "Paylaşım" : text;
    }
}
