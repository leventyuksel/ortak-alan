using System.Runtime.InteropServices;

namespace OrtakAlan;

static class WindowsShareService
{
    public static (bool Ok, string? ShareName, string? Error) TryShareFolder(string path, string displayName, string id)
    {
        try
        {
            if (!Directory.Exists(path))
                return (false, null, "Klasör bulunamadı");

            var share = SafeName(displayName, id);
            var info = new ShareInfo2
            {
                NetName = share,
                Type = 0,
                Remark = "Ortak Alan",
                Permissions = 0,
                MaxUses = uint.MaxValue,
                Path = path,
                Password = ""
            };
            var result = NetShareAdd(null, 2, ref info, out _);
            if (result == 0 || result == 2118)
                return (true, share, null);
            if (result == 5)
                return (false, null, "Yönetici izni gerekir");
            return (false, null, "Windows paylaşımı açılamadı");
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return (false, null, "Windows paylaşımı açılamadı");
        }
    }

    public static void TryUnshareFolder(string shareName)
    {
        try { NetShareDel(null, shareName, 0); }
        catch (Exception ex) { Log.Write(ex); }
    }

    public static (bool Ok, string? ShareName, string? Error) TrySharePrinter(string printerName, string id)
    {
        var share = SafeName(printerName, id);
        if (!TrySetPrinterShared(printerName, share, true))
            return (false, null, "Yönetici izni gerekir");
        return (true, share, null);
    }

    public static void TryUnsharePrinter(string printerName)
    {
        TrySetPrinterShared(printerName, printerName, false);
    }

    static bool TrySetPrinterShared(string printerName, string shareName, bool shared)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            return false;
        try
        {
            GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return false;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(handle, 2, buffer, needed, out _))
                    return false;

                var sharePtr = Marshal.StringToHGlobalUni(shareName);
                try
                {
                    // PRINTER_INFO_2 içinde Attributes, 13 işaretçiden sonra gelir.
                    var attributeOffset = 13 * IntPtr.Size;
                    var attributes = (uint)Marshal.ReadInt32(buffer, attributeOffset);
                    attributes = shared ? attributes | 0x8u : attributes & ~0x8u;
                    Marshal.WriteInt32(buffer, attributeOffset, unchecked((int)attributes));
                    Marshal.WriteIntPtr(buffer, 2 * IntPtr.Size, sharePtr);
                    return SetPrinter(handle, 2, buffer, 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(sharePtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return false;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    static string SafeName(string name, string id)
    {
        var chars = name.Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9').ToArray();
        var text = new string(chars);
        if (text.Length == 0) text = "Ortak";
        if (text.Length > 10) text = text[..10];
        var suffix = id.Length >= 4 ? id[..4] : "0000";
        return text + suffix;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShareInfo2
    {
        public string NetName;
        public uint Type;
        public string Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public string Path;
        public string Password;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "NetShareAdd")]
    static extern int NetShareAdd(string? server, int level, ref ShareInfo2 info, out int parmErr);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "NetShareDel")]
    static extern int NetShareDel(string? server, string netName, int reserved);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetPrinter(IntPtr handle, int level, IntPtr buffer, int size, out int needed);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetPrinter(IntPtr handle, int level, IntPtr buffer, int command);
}
