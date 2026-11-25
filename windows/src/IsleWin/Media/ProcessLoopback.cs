using System.Runtime.InteropServices;

namespace IsleWin.Media;

/// Windows 10 2004+ process loopback capture: the audio of one process tree only (like Isle's process-scoped Core Audio tap).
/// Raw COM interop; NAudio has no wrapper for ActivateAudioInterfaceAsync with VAD\Process_Loopback.
internal sealed class ProcessLoopback : IDisposable
{
    public const int SampleRate = 48000;
    IAudioClient? _client; IAudioCaptureClient? _cap;
    readonly AutoResetEvent _event = new(false);

    /// Throws on failure (process has no audio yet, unsupported OS, ...).
    public static ProcessLoopback Open(int pid)
    {
        var pl = new ProcessLoopback();
        try { pl.Init(pid); return pl; } catch { pl.Dispose(); throw; }
    }

    void Init(int pid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type = PROCESS_LOOPBACK(1), pid, mode = INCLUDE_TARGET_PROCESS_TREE(0) } inside a VT_BLOB PROPVARIANT
        IntPtr pars = Marshal.AllocHGlobal(12), pv = Marshal.AllocHGlobal(24), fmt = Marshal.AllocHGlobal(18);
        try
        {
            Marshal.WriteInt32(pars, 0, 1); Marshal.WriteInt32(pars, 4, pid); Marshal.WriteInt32(pars, 8, 0);
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65); // VT_BLOB
            Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, 16, pars);

            var handler = new Handler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out var op);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("process loopback activation");
            op.GetActivateResult(out int hr, out object o);
            Marshal.ThrowExceptionForHR(hr);
            _client = (IAudioClient)o;

            // 32-bit float stereo 48 kHz (GetMixFormat is not supported on the process loopback device)
            Marshal.WriteInt16(fmt, 0, 3); Marshal.WriteInt16(fmt, 2, 2);
            Marshal.WriteInt32(fmt, 4, SampleRate); Marshal.WriteInt32(fmt, 8, SampleRate * 8);
            Marshal.WriteInt16(fmt, 12, 8); Marshal.WriteInt16(fmt, 14, 32); Marshal.WriteInt16(fmt, 16, 0);
            const uint flags = 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000; // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
            _client.Initialize(0, flags, 0, 0, fmt, IntPtr.Zero);
            _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
            var gid = typeof(IAudioCaptureClient).GUID;
            _cap = (IAudioCaptureClient)_client.GetService(ref gid);
            _client.Start();
        }
        finally { Marshal.FreeHGlobal(pars); Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(fmt); }
    }

    /// Waits up to timeoutMs for data, then drains every packet into onData (interleaved stereo float, frame count, silent flag).
    /// Returns the number of packets read.
    public int Pump(int timeoutMs, Action<float[], int, bool> onData)
    {
        if (_cap == null) return 0;
        _event.WaitOne(timeoutMs);
        int packets = 0;
        float[] tmp = new float[4800 * 2];
        while (_cap.GetNextPacketSize(out uint n) == 0 && n > 0)
        {
            if (_cap.GetBuffer(out IntPtr data, out uint frames, out uint fl, out _, out _) != 0) break;
            try
            {
                int cnt = (int)Math.Min(frames, 4800);
                bool silent = (fl & 2) != 0;
                if (!silent) Marshal.Copy(data, tmp, 0, cnt * 2);
                onData(tmp, cnt, silent);
                packets++;
            }
            finally { _cap.ReleaseBuffer(frames); }
        }
        return packets;
    }

    public void Dispose()
    {
        try { _client?.Stop(); } catch { }
        if (_cap != null) { try { Marshal.ReleaseComObject(_cap); } catch { } _cap = null; }
        if (_client != null) { try { Marshal.ReleaseComObject(_client); } catch { } _client = null; }
        _event.Dispose();
    }

    /// PID of the top-level process (parent is not the same program) named `name`, or 0. Include-tree capture then covers its audio child.
    public static int FindTopLevel(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0; // no target: the player is on another device
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == (IntPtr)(-1)) return 0;
        try
        {
            var all = new List<(int pid, int ppid, string exe)>();
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e)) all.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            string want = name + ".exe";
            var same = all.Where(p => p.exe.Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
            var pids = new HashSet<int>(same.Select(p => p.pid));
            var top = same.Where(p => !pids.Contains(p.ppid)).Select(p => p.pid).ToList();
            return top.Count > 0 ? top.Min() : 0;
        }
        finally { CloseHandle(snap); }
    }

    [ComVisible(true)]
    sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr pars,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long def, out long min);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr h);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetService(ref Guid riid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID; public UIntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr h, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr h, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
