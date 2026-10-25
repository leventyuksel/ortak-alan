using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OrtakAlan;

public static class MachineGuard
{
    const string FileName = "makine.damga";

    public static bool Bind(string dataDir, string? exePath, SettingsService store)
    {
        var here = Fingerprint();
        var dataFile = Path.Combine(dataDir, FileName);
        var exeFile = ExeFile(exePath);
        var found = new[] { Read(dataFile), exeFile == null ? "" : Read(exeFile), store.Current.MachineStamp.Trim() };
        if (found.Any(value => value.Length > 0 && !string.Equals(value, here, StringComparison.OrdinalIgnoreCase)))
            return false;

        Directory.CreateDirectory(dataDir);
        Write(dataFile, here);
        if (exeFile != null)
        {
            try { Write(exeFile, here); }
            catch (Exception ex) { Log.Write(ex); }
        }

        if (!string.Equals(store.Current.MachineStamp, here, StringComparison.OrdinalIgnoreCase))
        {
            store.Current.MachineStamp = here;
            store.Save();
        }
        return true;
    }

    public static string Fingerprint()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Trim().Length > 0)
                return guid.Trim();
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        uint serial = 0;
        try { GetVolumeInformation(root, null, 0, out serial, out _, out _, null, 0); }
        catch { /* yedek kimlik */ }
        return Environment.MachineName + "-" + serial.ToString("X");
    }

    static string? ExeFile(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var dir = Path.GetDirectoryName(exePath);
        return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, FileName);
    }

    static string Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return "";
        }
    }

    static void Write(string path, string value)
    {
        var current = Read(path);
        if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase)) return;
        File.WriteAllText(path, value);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(
        string root,
        string? volumeName,
        int nameSize,
        out uint serial,
        out uint maxComponent,
        out uint flags,
        string? fileSystem,
        int fileSystemSize);
}
