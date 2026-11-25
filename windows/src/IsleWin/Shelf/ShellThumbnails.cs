using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IsleWin.Shelf;

/// What the shelf draws for a file (ShelfPreviews.swift): its icon at once, then Explorer's own thumbnail when
/// the shell has one (images, PDFs, video frames, Office files). Folders get an icon only, and so do files on
/// network drives (a thumbnail would read the file over the network). All shell work is on one STA worker.
public static class ShellThumbnails
{
    const int CacheLimit = 20; // 2 x the shelf's capacity, like Isle

    sealed record Job(string Path, bool IsFolder, int Px, Action<BitmapSource, bool> Done, Func<bool> Cancelled);
    static readonly BlockingCollection<Job> Queue = new();
    static readonly Dictionary<string, BitmapSource> Icons = new(), Thumbs = new();   // keyed by path (icons) and path|stamp (thumbnails)
    static readonly List<string> IconOrder = new(), ThumbOrder = new();
    static Thread? _worker;

    /// Asks for the icon, then the thumbnail of `path`, at `side` DIPs (`dpi` = the screen's scale). `ready` runs
    /// on `dispatcher` once for the icon (`isThumbnail` false) and again if there is a thumbnail. `cancelled`
    /// lets a view that has moved on skip the work.
    public static void Load(string path, bool isFolder, double side, double dpi, System.Windows.Threading.Dispatcher dispatcher, Action<BitmapSource, bool> ready, Func<bool>? cancelled = null)
    {
        var px = (int)Math.Ceiling(Math.Max(side, 64) * Math.Max(dpi, 1));
        Action<BitmapSource, bool> post = (s, t) => dispatcher.BeginInvoke(() => ready(s, t));
        EnsureWorker();
        Queue.Add(new Job(path, isFolder, px, post, cancelled ?? (() => false)));
    }

    static void EnsureWorker()
    {
        if (_worker != null) return;
        lock (Queue)
        {
            if (_worker != null) return;
            _worker = new Thread(Run) { IsBackground = true, Name = "Isle shelf thumbnails" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    static void Run()
    {
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (job.Cancelled()) continue;
                var local = IsLocal(job.Path);
                string stamp;
                try { stamp = job.IsFolder ? "" : $"{File.GetLastWriteTimeUtc(job.Path).Ticks}|{new FileInfo(job.Path).Length}"; } catch { stamp = ""; }

                BitmapSource? icon; lock (Icons) Icons.TryGetValue(job.Path, out icon);
                if (icon == null)
                {
                    icon = Fetch(job.Path, job.Px, SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK);
                    if (icon != null) Remember(Icons, IconOrder, job.Path, icon);
                }
                if (icon != null) job.Done(icon, false);
                if (job.IsFolder || !local || job.Cancelled()) continue;

                var key = job.Path + "|" + stamp;
                BitmapSource? thumb; lock (Thumbs) Thumbs.TryGetValue(key, out thumb);
                if (thumb == null)
                {
                    thumb = Fetch(job.Path, job.Px, SIIGBF.THUMBNAILONLY | SIIGBF.RESIZETOFIT);
                    if (thumb != null) Remember(Thumbs, ThumbOrder, key, thumb);
                }
                if (thumb != null) job.Done(thumb, true);
            }
            catch { }
        }
    }

    static void Remember(Dictionary<string, BitmapSource> map, List<string> order, string key, BitmapSource v)
    {
        lock (map)
        {
            if (map.TryAdd(key, v)) order.Add(key);
            while (order.Count > CacheLimit) { map.Remove(order[0]); order.RemoveAt(0); }
        }
    }

    static bool IsLocal(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return false;
            var root = System.IO.Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) || GetDriveType(root) != 4; // DRIVE_REMOTE
        }
        catch { return false; }
    }

    static BitmapSource? Fetch(string path, int px, SIIGBF flags)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = px, cy = px }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return ToBitmap(hbm);
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); Marshal.ReleaseComObject(factory); }
    }

    // 32 bpp top-down copy that keeps the alpha channel (CreateBitmapSourceFromHBitmap drops it for some thumbnails).
    static BitmapSource? ToBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight, stride = w * 4;
        var bits = new byte[stride * h];
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, bits, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        var anyAlpha = false;
        for (var i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (var i = 3; i < bits.Length; i += 4) bits[i] = 255;
        var src = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, bits, stride);
        src.Freeze();
        return src;
    }

    [Flags] enum SIIGBF { RESIZETOFIT = 0, BIGGERSIZEOK = 1, ICONONLY = 4, THUMBNAILONLY = 8 }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveType(string root);
}
