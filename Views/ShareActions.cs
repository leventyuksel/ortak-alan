using System.Diagnostics;
using System.Windows;

namespace OrtakAlan.Views;

static class ShareConnect
{
    public static string OpenFolder(PeerVm peer, RemoteShareVm share, bool createShortcut)
    {
        if (!App.Me.Store.Allows(peer.Id))
        {
            LicenseService.NotifySeat();
            return LicenseService.SeatStatus;
        }

        var label = "";
        if (createShortcut)
        {
            try { label = DesktopShortcut.CreateFolder(peer.Name, share.Name, peer.Id, share.Id); }
            catch (Exception ex) { Log.Write(ex); }
        }

        App.Me.ShowFiles(peer.Id, peer.Name, share.Id, share.Name);
        return label.Length == 0
            ? peer.Name + " / " + share.Name
            : "\"" + label + "\" Paylaşılan Dosyalar içine eklendi.";
    }

    public static async Task<string> ConnectPrinter(PeerVm peer, RemoteShareVm share, Window? owner)
    {
        if (!App.Me.Store.Allows(peer.Id))
        {
            LicenseService.NotifySeat();
            return LicenseService.SeatStatus;
        }

        var result = await LocalPrinters.EnsureAsync(peer.Id, peer.Name, share.Id, share.Name, peer.Online);
        if (result.Ok)
        {
            if (result.Created)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}") { UseShellExecute = true });
                }
                catch (Exception ex) { Log.Write(ex); }
            }
            return result.Message;
        }

        if (owner == null || !peer.Online) return result.Message;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "\"" + share.Name + "\" yazıcısına gönder",
            Filter = "Belgeler|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.txt;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx|Tüm dosyalar|*.*",
            Multiselect = true
        };
        var picked = dialog.ShowDialog(owner) == true;
        if (!picked || dialog.FileNames.Length == 0) return result.Message;

        var failed = new List<string>();
        var sent = 0;
        foreach (var file in dialog.FileNames)
        {
            var error = await App.Me.Node.SendPrintAsync(peer.Id, share.Id, file);
            if (error == null) sent++;
            else failed.Add(Path.GetFileName(file) + ": " + error);
        }

        var sentText = failed.Count == 0
            ? (sent == 1 ? "Dosya yazıcıya gönderildi." : sent + " dosya yazıcıya gönderildi.")
            : string.Join(" ", failed);
        return result.Message + " " + sentText;
    }
}
