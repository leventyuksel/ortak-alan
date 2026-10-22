using Microsoft.Win32;

namespace OrtakAlan;

static class AutostartService
{
    const string ValueName = "OrtakAlan";

    public static void Enable()
    {
        var exe = AppInfo.PreferredExe();
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue(ValueName, $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}
