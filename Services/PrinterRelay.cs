using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Printing;
using System.Windows.Xps.Packaging;

namespace OrtakAlan;

static class PrinterRelay
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    public static bool Installed(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return false;
        try
        {
            foreach (string name in PrinterSettings.InstalledPrinters)
            {
                if (string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return false;
    }

    public static async Task<string?> PrintAsync(string printerName, string path)
    {
        if (!File.Exists(path)) return "Dosya bulunamadı";
        if (!Installed(printerName)) return "Yazıcı bu bilgisayarda yok";
        var ext = Path.GetExtension(path);
        try
        {
            if (Images.Contains(ext)) return await Sta(() => PrintImage(printerName, path));
            if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".log", StringComparison.OrdinalIgnoreCase))
                return await Sta(() => PrintText(printerName, path));
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                try { return await PrintPdf(printerName, path); }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    return await Sta(() => PrintWithShell(printerName, path));
                }
            }
            if (ext.Equals(".xps", StringComparison.OrdinalIgnoreCase) || ext.Equals(".oxps", StringComparison.OrdinalIgnoreCase))
                return await Sta(() => PrintXps(printerName, path));
            return await Sta(() => PrintWithShell(printerName, path));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "Yazdırılamadı";
        }
    }

    static async Task<string?> PrintPdf(string printer, string path)
    {
        await using var file = File.OpenRead(path);
        using var input = file.AsRandomAccessStream();
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input);
        if (pdf.PageCount == 0) return "Dosya boş";
        var job = Path.GetFileName(path);
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            using var page = pdf.GetPage(i);
            var width = page.Size.Width * 300 / 96;
            var height = page.Size.Height * 300 / 96;
            const double max = 4960;
            if (width > max || height > max)
            {
                var fit = Math.Min(max / Math.Max(width, 1), max / Math.Max(height, 1));
                width *= fit;
                height *= fit;
            }
            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height))
            };
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, options);
            ras.Seek(0);
            using var net = ras.AsStreamForRead();
            using var copy = new MemoryStream();
            net.CopyTo(copy);
            copy.Position = 0;
            using var raw = new Bitmap(copy);
            using var bitmap = new Bitmap(raw);
            var name = pdf.PageCount == 1 ? job : job + " (" + (i + 1) + ")";
            var error = await Sta(() => PrintOne(printer, name, bitmap, true));
            if (error != null) return error;
        }
        return null;
    }

    static string? PrintXps(string printer, string path)
    {
        try
        {
            using var server = new LocalPrintServer();
            var queue = server.GetPrintQueue(printer);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            using var doc = new XpsDocument(path, FileAccess.Read);
            writer.Write(doc.GetFixedDocumentSequence());
            return null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return PrintWithShell(printer, path);
        }
    }

    static string? PrintImage(string printer, string path)
    {
        using var loaded = new Bitmap(path);
        using var copy = new Bitmap(loaded);
        return PrintOne(printer, Path.GetFileName(path), copy);
    }

    static string? PrintOne(string printer, string jobName, Bitmap bitmap, bool fillPage = false)
    {
        using var doc = new PrintDocument();
        if (!Prepare(doc, printer, jobName)) return "Yazıcı kullanılamıyor";
        doc.DefaultPageSettings.Landscape = bitmap.Width > bitmap.Height;
        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null) return;
            var bounds = fillPage ? e.PageBounds : e.MarginBounds;
            var scale = Math.Min(bounds.Width / (float)bitmap.Width, bounds.Height / (float)bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            var x = bounds.Left + (bounds.Width - w) / 2f;
            var y = bounds.Top + (bounds.Height - h) / 2f;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(bitmap, x, y, w, h);
            e.HasMorePages = false;
        };
        doc.Print();
        return null;
    }

    static string? PrintText(string printer, string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length == 0) return "Dosya boş";
        using var doc = new PrintDocument();
        if (!Prepare(doc, printer, Path.GetFileName(path))) return "Yazıcı kullanılamıyor";
        var index = 0;
        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null) return;
            using var font = new Font("Segoe UI", 11f);
            var bounds = e.MarginBounds;
            var remaining = text[index..];
            var layout = new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            e.Graphics.MeasureString(remaining, font, bounds.Size, StringFormat.GenericTypographic, out int chars, out int _);
            if (chars <= 0) chars = Math.Min(1, remaining.Length);
            e.Graphics.DrawString(remaining[..chars], font, Brushes.Black, layout);
            index += chars;
            e.HasMorePages = index < text.Length;
        };
        doc.Print();
        return null;
    }

    static string? PrintWithShell(string printer, string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Verb = "printto",
                Arguments = "\"" + printer.Replace("\"", "") + "\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            });
            return null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "Bu dosya türü yazdırılamadı";
        }
    }

    static bool Prepare(PrintDocument doc, string printer, string job)
    {
        doc.PrinterSettings.PrinterName = printer;
        doc.DocumentName = job.Length == 0 ? "Ortak Alan" : job;
        doc.PrintController = new StandardPrintController();
        return doc.PrinterSettings.IsValid;
    }

    static Task<string?> Sta(Func<string?> work)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex)
            {
                Log.Write(ex);
                done.SetResult("Yazdırılamadı");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }
}
