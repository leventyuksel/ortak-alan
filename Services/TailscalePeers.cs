using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace OrtakAlan;

static class TailscalePeers
{
    static readonly object Gate = new();
    static int _logged;
    static IReadOnlyList<IPAddress> _peers = Array.Empty<IPAddress>();

    public static IPAddress? Self { get; private set; }
    public static IReadOnlyList<IPAddress> Current => _peers;

    public static void Start(CancellationToken token)
    {
        _ = Task.Run(() => Loop(token));
        _ = Task.Run(AllowProgram);
    }

    public static bool IsAddress(string text) =>
        IPAddress.TryParse(text, out var address) && IsAddress(address);

    public static bool IsAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    static async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var (self, peers) = await Read(token);
                lock (Gate)
                {
                    Self = self;
                    _peers = peers;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _logged, 1) == 0) Log.Write(ex);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            catch { break; }
        }
    }

    static async Task<(IPAddress? Self, IReadOnlyList<IPAddress> Peers)> Read(CancellationToken token)
    {
        var exe = Find();
        if (exe == null) return (null, Array.Empty<IPAddress>());
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("status");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start);
        if (process == null) return (null, Array.Empty<IPAddress>());
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(wait.Token); }
        catch
        {
            try { process.Kill(true); } catch { /* zaman aşımı */ }
            return (null, Array.Empty<IPAddress>());
        }
        _ = await error;
        return Parse(await output);
    }

    static (IPAddress? Self, IReadOnlyList<IPAddress> Peers) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        IPAddress? self = null;
        if (root.TryGetProperty("Self", out var selfEl))
            self = First(selfEl);
        var peers = new List<IPAddress>();
        if (root.TryGetProperty("Peer", out var peerEl) && peerEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in peerEl.EnumerateObject())
            {
                if (item.Value.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.False) continue;
                var address = First(item.Value);
                if (address != null) peers.Add(address);
            }
        }
        return (self, peers);
    }

    static IPAddress? First(JsonElement node)
    {
        if (!node.TryGetProperty("TailscaleIPs", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            if (IPAddress.TryParse(item.GetString(), out var address) && IsAddress(address)) return address;
        }
        return null;
    }

    static string? Find()
    {
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var path = Path.Combine(program, "Tailscale", "tailscale.exe");
        if (File.Exists(path)) return path;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailscale", "tailscale.exe");
        return File.Exists(local) ? local : null;
    }

    static void AllowProgram()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        Netsh("advfirewall firewall delete rule name=\"Ortak Alan\"");
        Netsh("advfirewall firewall add rule name=\"Ortak Alan\" dir=in action=allow program=\"" + exe + "\" enable=yes profile=any");
    }

    static void Netsh(string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            process?.WaitForExit(8000);
        }
        catch { /* yönetici izni yoksa Windows kendi sorusuna bırakılır */ }
    }
}
