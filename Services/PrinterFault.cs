using System.Runtime.InteropServices;

namespace OrtakAlan;

static class PrinterFault
{
    const uint PaperJam = 0x8;
    const uint PaperOut = 0x10;
    const uint PaperProblem = 0x40;
    const uint Offline = 0x80;
    const uint OutputFull = 0x800;
    const uint NotAvailable = 0x1000;
    const uint NoToner = 0x40000;
    const uint UserAction = 0x100000;
    const uint OutOfMemory = 0x200000;
    const uint DoorOpen = 0x400000;
    const uint Error = 0x2;

    const uint JobOffline = 0x20;
    const uint JobPaperOut = 0x40;
    const uint JobBlocked = 0x200;
    const uint JobUser = 0x400;

    public static HashSet<uint> Jobs(string printer)
    {
        var ids = new HashSet<uint>();
        if (printer.Length == 0 || !OpenPrinter(printer, out var handle, IntPtr.Zero)) return ids;
        try
        {
            ReadJobs(handle, ids, null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            ClosePrinter(handle);
        }
        return ids;
    }

    public static string? Describe(string printer, HashSet<uint> before)
    {
        if (printer.Length == 0 || !OpenPrinter(printer, out var handle, IntPtr.Zero))
            return "Yazıcı bulunamadı";
        try
        {
            var job = JobProblem(handle, before);
            if (job != null) return job;
            return StatusText(ReadStatus(handle));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    static string? JobProblem(IntPtr handle, HashSet<uint> before)
    {
        var problems = new List<uint>();
        ReadJobs(handle, null, (id, status) =>
        {
            if (!before.Contains(id)) problems.Add(status);
        });
        foreach (var status in problems)
        {
            var text = JobText(status);
            if (text != null) return text;
        }
        return null;
    }

    static void ReadJobs(IntPtr handle, HashSet<uint>? ids, Action<uint, uint>? each)
    {
        EnumJobs(handle, 0, 32, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0 || needed > 1_000_000) return;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumJobs(handle, 0, 32, 1, buffer, needed, out _, out var returned) || returned == 0) return;
            var stride = Marshal.SizeOf<JobInfo1>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<JobInfo1>(IntPtr.Add(buffer, i * stride));
                ids?.Add(info.JobId);
                each?.Invoke(info.JobId, info.Status);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static uint ReadStatus(IntPtr handle)
    {
        GetPrinter(handle, 6, IntPtr.Zero, 0, out var needed);
        if (needed < 4 || needed > 256) return 0;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetPrinter(handle, 6, buffer, needed, out _)) return 0;
            return (uint)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static string? StatusText(uint status)
    {
        if ((status & PaperJam) != 0) return "Yazıcıda kâğıt sıkıştı.";
        if ((status & PaperOut) != 0 || (status & PaperProblem) != 0) return "Yazıcıda kâğıt yok.";
        if ((status & DoorOpen) != 0) return "Yazıcının kapağı açık.";
        if ((status & NoToner) != 0) return "Yazıcıda toner yok.";
        if ((status & OutputFull) != 0) return "Yazıcının çıkış gözü dolu.";
        if ((status & Offline) != 0) return "Yazıcı çevrimdışı.";
        if ((status & NotAvailable) != 0) return "Yazıcı hazır değil.";
        if ((status & UserAction) != 0) return "Yazıcı müdahale bekliyor.";
        if ((status & OutOfMemory) != 0) return "Yazıcının belleği doldu.";
        if ((status & Error) != 0) return "Yazıcı hata verdi.";
        return null;
    }

    static string? JobText(uint status)
    {
        if ((status & JobPaperOut) != 0) return "Yazıcıda kâğıt yok.";
        if ((status & JobOffline) != 0) return "Yazıcı çevrimdışı.";
        if ((status & JobUser) != 0) return "Yazıcı müdahale bekliyor.";
        if ((status & JobBlocked) != 0) return "Yazıcı işi bekletiyor.";
        if ((status & Error) != 0) return "Yazıcı hata verdi.";
        return null;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buffer, uint size, out uint needed);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumJobs(IntPtr handle, uint first, uint count, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [StructLayout(LayoutKind.Sequential)]
    struct JobInfo1
    {
        public uint JobId;
        public IntPtr PrinterName;
        public IntPtr MachineName;
        public IntPtr UserName;
        public IntPtr Document;
        public IntPtr Datatype;
        public IntPtr StatusText;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }
}
