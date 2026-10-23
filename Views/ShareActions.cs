namespace OrtakAlan.Views;

static class ShareConnect
{
    public static string OpenFolder(PeerVm peer, RemoteShareVm share, bool createShortcut)
    {
        var label = "";
        if (createShortcut)
        {
            try { label = DesktopShortcut.CreateFolder(peer.Name, share.Name, peer.Id, share.Id); }
            catch (Exception ex) { Log.Write(ex); }
        }

        App.Me.ShowFiles(peer.Id, peer.Name, share.Id, share.Name);
        return label.Length == 0
            ? peer.Name + " / " + share.Name
            : "Masaüstüne \"" + label + "\" kısayolu kondu.";
    }

    public static async Task<string> ConnectPrinter(PeerVm peer, RemoteShareVm share)
    {
        if (!peer.Online) return "Çevrimdışı";
        var error = await App.Me.Node.ConnectAsync(peer.Id);
        if (error != null) return error;
        if (string.IsNullOrEmpty(share.WindowsShare))
            return "Yazıcı görünüyor. Windows paylaşımı açık değil.";

        var unc = $@"\\{peer.Ip}\{share.WindowsShare}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "rundll32.exe",
            $"printui.dll,PrintUIEntry /in /n \"{unc}\"")
        { UseShellExecute = true });
        return "Yazıcı ekleme penceresi açıldı.";
    }
}
