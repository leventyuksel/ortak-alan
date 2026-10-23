using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace OrtakAlan;

sealed class TrayService : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.Timer _blink;
    readonly string _title;
    int _unread;
    bool _lit;

    public TrayService(string tooltip, Action toggle, Action show, Action chat, Action theme, Action exit)
    {
        _title = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _icon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = _title,
            Icon = TrayGlyph.Create(0, false)
        };

        _blink = new WinForms.Timer { Interval = 450 };
        _blink.Tick += (_, _) =>
        {
            _lit = !_lit;
            Swap(TrayGlyph.Create(_unread, _lit));
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Sohbet", null, (_, _) => chat());
        menu.Items.Add("Paylaşımlar", null, (_, _) => show());
        menu.Items.Add("Paylaşılan Dosyalar", null, (_, _) => SharedFiles.Open());
        menu.Items.Add("Ayarlar", null, (_, _) => theme());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            if (_unread > 0) chat();
            else toggle();
        };
    }

    public void SetUnread(int count)
    {
        _unread = Math.Max(0, count);
        if (_unread == 0)
        {
            _blink.Stop();
            _lit = false;
            _icon.Text = _title;
            Swap(TrayGlyph.Create(0, false));
            return;
        }

        var label = _unread == 1 ? "1 yeni mesaj" : _unread + " yeni mesaj";
        var tip = _title + " — " + label;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        Swap(TrayGlyph.Create(_unread, _lit));
        if (!_blink.Enabled) _blink.Start();
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text.Length > 180 ? text[..180] : text;
        _icon.ShowBalloonTip(4000);
    }

    void Swap(System.Drawing.Icon next)
    {
        var previous = _icon.Icon;
        _icon.Icon = next;
        previous?.Dispose();
    }

    public void Dispose()
    {
        _blink.Stop();
        _blink.Dispose();
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}

static class TrayGlyph
{
    public static System.Drawing.Icon Create(int count, bool alert)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.Clear(System.Drawing.Color.Transparent);
        if (BrandAssets.TrayImage != null)
            graphics.DrawImage(BrandAssets.TrayImage, 0, 0, 32, 32);

        var navy = System.Drawing.Color.FromArgb(11, 31, 75);
        if (count > 0)
        {
            using var badge = new System.Drawing.SolidBrush(alert ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(220, 38, 38));
            graphics.FillEllipse(badge, 15, 0, 17, 17);
            var text = count > 9 ? "9+" : count.ToString();
            using var font = new System.Drawing.Font("Segoe UI", count > 9 ? 7f : 8f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var size = graphics.MeasureString(text, font);
            using var ink = new System.Drawing.SolidBrush(alert ? navy : System.Drawing.Color.White);
            graphics.DrawString(text, font, ink, 15 + (17 - size.Width) / 2f, (17 - size.Height) / 2f - 0.4f);
        }

        return Own(bitmap);
    }

    static System.Drawing.Icon Own(System.Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);
}
