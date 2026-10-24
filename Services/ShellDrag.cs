using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrtakAlan;

sealed class ShellNode
{
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public Func<Stream>? Open { get; init; }
    public Func<IReadOnlyList<ShellNode>>? Children { get; init; }
}

static class ShellDrag
{
    const int SOk = 0;
    const int DragDropDrop = 0x00040100;
    const int DragDropCancel = 0x00040101;
    const int DragDropUseDefaultCursors = 0x00040102;
    const int DvEFormatEtc = unchecked((int)0x80040064);
    const int ENotImpl = unchecked((int)0x80004001);

    static readonly ushort DescriptorFormat = RegisterClipboardFormat("FileGroupDescriptorW");
    static readonly ushort ContentsFormat = RegisterClipboardFormat("FileContents");
    static readonly ushort PreferFormat = RegisterClipboardFormat("Preferred DropEffect");
    static readonly ushort PerformedFormat = RegisterClipboardFormat("Performed DropEffect");
    static readonly ushort PasteFormat = RegisterClipboardFormat("Paste Succeeded");

    static ShellPackage? _clipboard;

    public static string? LastError { get; private set; }

    public static int Drag(IReadOnlyList<ShellNode> nodes, Action? onMoved)
    {
        LastError = null;
        var package = new ShellPackage(nodes, preferMove: false, onMoved);
        var effect = 0;
        DoDragDrop(package, new DropSource(), 1 | 2, out effect);
        if (effect == 2) package.Moved();
        return effect;
    }

    public static uint CopyToClipboard(IReadOnlyList<ShellNode> nodes, bool move, Action? onMoved)
    {
        LastError = null;
        _clipboard = new ShellPackage(nodes, move, onMoved);
        var hr = OleSetClipboard(_clipboard);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return GetClipboardSequenceNumber();
    }

    public static uint CurrentSequence() => GetClipboardSequenceNumber();

    [DllImport("ole32.dll")]
    static extern int DoDragDrop(IDataObject data, IDropSource source, int effects, out int result);

    [DllImport("ole32.dll")]
    static extern int OleSetClipboard(IDataObject data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, int keyState)
        {
            if (escapePressed) return DragDropCancel;
            if ((keyState & 1) == 0) return DragDropDrop;
            return SOk;
        }

        public int GiveFeedback(int effect) => DragDropUseDefaultCursors;
    }

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropSource
    {
        [PreserveSig]
        int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, int keyState);

        [PreserveSig]
        int GiveFeedback(int effect);
    }

    [ComVisible(true)]
    sealed class ShellPackage : IDataObject
    {
        readonly IReadOnlyList<ShellNode> _roots;
        readonly bool _preferMove;
        readonly Action? _onMoved;
        readonly FORMATETC[] _formats;
        List<Flat>? _flat;
        int _moved;

        public ShellPackage(IReadOnlyList<ShellNode> roots, bool preferMove, Action? onMoved)
        {
            _roots = roots;
            _preferMove = preferMove;
            _onMoved = onMoved;
            _formats =
            [
                Format(DescriptorFormat, TYMED.TYMED_HGLOBAL),
                Format(ContentsFormat, TYMED.TYMED_ISTREAM),
                Format(PreferFormat, TYMED.TYMED_HGLOBAL)
            ];
        }

        public void Moved()
        {
            if (Interlocked.Exchange(ref _moved, 1) == 1) return;
            _onMoved?.Invoke();
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            if (Is(format, DescriptorFormat))
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = Bytes(Descriptor());
                return;
            }
            if (Is(format, PreferFormat))
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = Bytes(BitConverter.GetBytes(_preferMove ? 2 : 1));
                return;
            }
            if (Is(format, ContentsFormat))
            {
                var items = Items();
                var index = format.lindex < 0 ? 0 : format.lindex;
                if (index < 0 || index >= items.Count) throw new COMException("Dosya yok", DvEFormatEtc);
                var item = items[index];
                Stream stream;
                try
                {
                    stream = item.IsDirectory || item.Open == null
                        ? new MemoryStream()
                        : item.Open();
                }
                catch (Exception ex)
                {
                    LastError = ex is IOException ? ex.Message : "Dosya alınamadı";
                    Log.Write(ex);
                    throw new COMException(LastError, DvEFormatEtc);
                }
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetIUnknownForObject(new StreamMedium(stream));
                return;
            }
            throw new COMException("Biçim yok", DvEFormatEtc);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
            throw new COMException("Desteklenmiyor", ENotImpl);

        public int QueryGetData(ref FORMATETC format)
        {
            if (Is(format, DescriptorFormat) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return SOk;
            if (Is(format, ContentsFormat) && (format.tymed & TYMED.TYMED_ISTREAM) != 0) return SOk;
            if (Is(format, PreferFormat) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return SOk;
            return DvEFormatEtc;
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            return 0x00040130;
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            try
            {
                if ((Is(formatIn, PerformedFormat) || Is(formatIn, PasteFormat)) &&
                    medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero &&
                    Marshal.ReadInt32(medium.unionmember) == 2)
                    Moved();
            }
            finally
            {
                if (release) ReleaseStgMedium(ref medium);
            }
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => new FormatEnum(_formats);

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return ENotImpl;
        }

        public void DUnadvise(int connection) { }

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null!;
            return ENotImpl;
        }

        List<Flat> Items()
        {
            if (_flat != null) return _flat;
            try
            {
                var list = new List<Flat>();
                foreach (var root in _roots)
                    Walk(root, root.Name, list);
                _flat = list;
                return list;
            }
            catch (Exception ex)
            {
                LastError = ex is IOException ? ex.Message : "Klasör alınamadı.";
                if (ex is IOException) throw new COMException(ex.Message, DvEFormatEtc);
                Log.Write(ex);
                throw new COMException(LastError, DvEFormatEtc);
            }
        }

        static void Walk(ShellNode node, string relative, List<Flat> into)
        {
            relative = Clean(relative);
            if (relative.Length == 0) return;
            if (relative.Length > 240)
            {
                relative = Path.GetFileName(relative);
                if (relative.Length > 240) relative = relative[..240];
            }
            if (!node.IsDirectory)
            {
                into.Add(new Flat { Relative = relative, Size = node.Size, Open = node.Open });
                return;
            }
            into.Add(new Flat { Relative = relative, IsDirectory = true });
            var children = node.Children?.Invoke() ?? Array.Empty<ShellNode>();
            foreach (var child in children)
                Walk(child, relative + "\\" + child.Name, into);
        }

        byte[] Descriptor()
        {
            var items = Items();
            var bytes = new byte[4 + (items.Count * 592)];
            BitConverter.GetBytes((uint)items.Count).CopyTo(bytes, 0);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var offset = 4 + (i * 592);
                var flags = item.IsDirectory ? 0x4004u : 0x4044u;
                BitConverter.GetBytes(flags).CopyTo(bytes, offset);
                BitConverter.GetBytes(item.IsDirectory ? 0x10u : 0x80u).CopyTo(bytes, offset + 36);
                if (!item.IsDirectory)
                {
                    BitConverter.GetBytes((uint)(item.Size >> 32)).CopyTo(bytes, offset + 64);
                    BitConverter.GetBytes((uint)item.Size).CopyTo(bytes, offset + 68);
                }
                var chars = System.Text.Encoding.Unicode.GetBytes(item.Relative);
                if (chars.Length > 518) chars = chars[..518];
                chars.CopyTo(bytes, offset + 72);
            }
            return bytes;
        }

        static IntPtr Bytes(byte[] data)
        {
            var memory = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, memory, data.Length);
            return memory;
        }

        static string Clean(string path)
        {
            var parts = path.Split('\\', '/');
            var kept = new List<string>();
            foreach (var part in parts)
            {
                var name = part.Trim();
                if (name.Length == 0 || name is "." or "..") continue;
                foreach (var bad in Path.GetInvalidFileNameChars())
                    name = name.Replace(bad, '_');
                if (name.Length == 0) continue;
                if (Reserved(name)) name = "_" + name;
                kept.Add(name);
            }
            return string.Join("\\", kept);
        }

        static bool Reserved(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                return true;
            if (stem.Length != 4) return false;
            var head = stem[..3];
            var tail = stem[3];
            return (head.Equals("COM", StringComparison.OrdinalIgnoreCase) || head.Equals("LPT", StringComparison.OrdinalIgnoreCase)) && tail is >= '1' and <= '9';
        }

        static bool Is(FORMATETC format, ushort id) => unchecked((ushort)format.cfFormat) == id;

        static FORMATETC Format(ushort id, TYMED medium) => new()
        {
            cfFormat = (short)id,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = medium
        };
    }

    sealed class Flat
    {
        public string Relative = "";
        public bool IsDirectory;
        public long Size;
        public Func<Stream>? Open;
    }

    sealed class FormatEnum : IEnumFORMATETC
    {
        readonly FORMATETC[] _formats;
        int _index;

        public FormatEnum(FORMATETC[] formats, int index = 0)
        {
            _formats = formats;
            _index = index;
        }

        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            var fetched = 0;
            while (fetched < celt && _index < _formats.Length && fetched < rgelt.Length)
                rgelt[fetched++] = _formats[_index++];
            if (pceltFetched != null && pceltFetched.Length > 0) pceltFetched[0] = fetched;
            return fetched == celt ? SOk : 1;
        }

        public int Skip(int celt)
        {
            _index = Math.Min(_formats.Length, _index + celt);
            return _index < _formats.Length ? SOk : 1;
        }

        public int Reset()
        {
            _index = 0;
            return SOk;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnum(_formats, _index);
    }

    sealed class StreamMedium : IStream
    {
        readonly Stream _stream;

        public StreamMedium(Stream stream) => _stream = stream;

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            var read = _stream.Read(pv, 0, Math.Min(cb, pv.Length));
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) =>
            throw new NotSupportedException();

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            var position = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
        }

        public void SetSize(long libNewSize) { }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            var buffer = new byte[80 * 1024];
            long readTotal = 0;
            long writeTotal = 0;
            var left = cb < 0 ? long.MaxValue : cb;
            var written = Marshal.AllocHGlobal(4);
            try
            {
                while (left > 0)
                {
                    var want = (int)Math.Min(buffer.Length, left);
                    var read = _stream.Read(buffer, 0, want);
                    if (read <= 0) break;
                    pstm.Write(buffer, read, written);
                    var count = Marshal.ReadInt32(written);
                    if (count <= 0) count = read;
                    readTotal += read;
                    writeTotal += count;
                    if (left != long.MaxValue) left -= read;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(written);
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, readTotal);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, writeTotal);
        }

        public void Commit(int grfCommitFlags) { }

        public void Revert() { }

        public void LockRegion(long libOffset, long cb, int dwLockType) { }

        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG
            {
                type = 2,
                cbSize = _stream.CanSeek ? _stream.Length : 0
            };
        }

        public void Clone(out IStream ppstm)
        {
            if (_stream is FileStream file)
            {
                var copy = new FileStream(file.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                ppstm = new StreamMedium(copy);
                return;
            }
            if (_stream is MemoryStream memory)
            {
                ppstm = new StreamMedium(new MemoryStream(memory.ToArray(), writable: false));
                return;
            }
            throw new NotSupportedException();
        }

        ~StreamMedium() => _stream.Dispose();
    }
}

static class ExplorerIcons
{
    const uint IconFlag = 0x100;
    const uint SmallFlag = 0x1;
    const uint UseAttributes = 0x10;
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string name, bool directory, bool computer)
    {
        var key = computer ? "pc" : directory ? "dir" : Path.GetExtension(name);
        if (key.Length == 0) key = ".file";
        if (Cache.TryGetValue(key, out var found)) return found;
        var image = computer ? Stock(94) : FileIcon(directory ? "klasör" : "dosya" + key, directory);
        if (image == null) return null;
        Cache[key] = image;
        return image;
    }

    static ImageSource? FileIcon(string name, bool directory)
    {
        var info = new ShFileInfo();
        SHGetFileInfo(name, directory ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), IconFlag | SmallFlag | UseAttributes);
        return FromIcon(info.hIcon);
    }

    static ImageSource? Stock(int id)
    {
        var info = new ShStockIconInfo { cbSize = (uint)Marshal.SizeOf<ShStockIconInfo>() };
        if (SHGetStockIconInfo(id, IconFlag | SmallFlag, ref info) != 0) return FileIcon("klasör", true);
        return FromIcon(info.hIcon);
    }

    static ImageSource? FromIcon(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll")]
    static extern int SHGetStockIconInfo(int id, uint flags, ref ShStockIconInfo info);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShStockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }
}
