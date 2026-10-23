namespace OrtakAlan;

static class DesktopShortcut
{
    public static void Ensure(bool refresh)
    {
        _ = refresh;
        try
        {
            var exe = AppInfo.PreferredExe();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var work = Path.GetDirectoryName(exe);
            Write(Path.Combine(desktop, "Ortak Alan.lnk"), exe, "", work, exe + ",0", "Ortak Alan");
            var icon = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",4";
            Write(
                Path.Combine(desktop, "Paylaşılan Dosyalar.lnk"),
                exe,
                "--paylasilan",
                work,
                icon,
                "Ağdaki paylaşılan dosyalar");
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    public static string CreateFolder(string person, string folder, string peerId, string shareId)
    {
        var exe = Environment.ProcessPath ?? "";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var label = Clean(person) + " - " + Clean(folder);
        if (exe.Length == 0) return label;
        Write(
            Path.Combine(desktop, label + ".lnk"),
            exe,
            "--klasor \"" + peerId + "\" \"" + shareId + "\"",
            Path.GetDirectoryName(exe),
            exe + ",0",
            person + " / " + folder);
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

    static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return text.Length == 0 ? "Paylaşım" : text;
    }
}
