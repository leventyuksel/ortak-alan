namespace OrtakAlan;

static class DesktopShortcut
{
    public static void Ensure(bool refresh)
    {
        try
        {
            var exe = AppInfo.PreferredExe();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var link = Path.Combine(desktop, "Ortak Alan.lnk");
            if (!refresh && File.Exists(link))
                refresh = true;

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "Ortak Alan";
            shortcut.Save();
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
        var link = Path.Combine(desktop, label + ".lnk");
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null || exe.Length == 0) return label;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = exe;
        shortcut.Arguments = "--klasor \"" + peerId + "\" \"" + shareId + "\"";
        shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
        shortcut.Description = person + " / " + folder;
        shortcut.Save();
        return label;
    }

    static string Clean(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return text.Length == 0 ? "Paylaşım" : text;
    }
}
