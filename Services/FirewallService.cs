using System.Diagnostics;

namespace OrtakAlan;

public static class FirewallService
{
    public static bool Apply(bool remove)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        var program = "program=\"" + exe.Replace("\"", "") + "\"";
        var deleted = Run("advfirewall firewall delete rule name=\"Ortak Alan\" " + program);
        if (remove) return deleted is 0 or 1;
        var description = "description=\"Diğer bilgisayarların bu bilgisayarı görmesi için\"";
        var inbound = Run("advfirewall firewall add rule name=\"Ortak Alan\" dir=in action=allow " + program + " " + description + " enable=yes profile=any");
        var outbound = Run("advfirewall firewall add rule name=\"Ortak Alan\" dir=out action=allow " + program + " " + description + " enable=yes profile=any");
        return inbound == 0 && outbound == 0;
    }

    static int Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null || !process.WaitForExit(15000)) return -1;
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
