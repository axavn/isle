using NAudio.Dsp;

namespace IsleWin.Media;

/// 16-band spectrum of one player's audio (process loopback). The capture thread writes a locked float[16]; the UI calls Snapshot.
/// Tuned to feel lively: 50% overlapping FFTs (~90 updates/s), treble tilt so highs move as much as bass,
/// auto-gain so quiet and loud songs both use the full height, fast attack and a quick (but not jittery) fall.
public sealed class WaveformService
{
    public const int Bands = 16, N = 1024;
    readonly object _lock = new();
    readonly float[] _levels = new float[Bands];
    readonly float[] _window = new float[N];
    readonly float[] _buf = new float[N];
    readonly Complex[] _fft = new Complex[N];
    readonly float[] _prev = new float[Bands];
    int _fill; long _lastData;
    double _ref = -30; // auto-gain: loudest recent band, in dB

    // Capture is process-scoped (Spotify by default, or the app the island shows), never the whole output.
    // One supervisor thread owns the capture: it opens it while active, rebuilds it when the target
    // process relaunches / changes or no buffers arrive for a while (device change), and closes it when stopped.
    const int StallMs = 5000, PollMs = 2000;
    readonly object _ctl = new();
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _active, _tapOn;
    string _target = "Spotify"; // process name without .exe

    public WaveformService() { for (int i = 0; i < N; i++) _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1)))); }

    /// True while a capture is bound to the target process and delivering. False: levels are empty and the UI should use its own animation.
    public bool HasTap => _tapOn;

    /// Aim at the app the island shows, from SMTC's SourceAppUserModelId ("Spotify.exe", "SpotifyAB.SpotifyMusic_...!Spotify", "chrome.exe").
    /// null or unknown ids fall back to Spotify. "" taps nothing (Spotify playing on another device): with no tap,
    /// Snapshot gives the idle animation, as Isle's equalizer does when it has no audio.
    public void SetTarget(string? sourceAppId)
    {
        string name = sourceAppId == "" ? "" : "Spotify";
        if (!string.IsNullOrWhiteSpace(sourceAppId) && !sourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            string id = sourceAppId.Trim();
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = id[..^4];
            else if (!id.Contains('!') && !id.Contains('_')) name = id; // classic desktop ids are bare; Store ids (Pkg_hash!App) can't be mapped
        }
        lock (_ctl) { if (_target == name) return; _target = name; }
        _wake.Set();
    }

    /// Begin capturing (call while playing). Idempotent.
    public void Start()
    {
        lock (_ctl)
        {
            _active = true;
            _thread ??= new Thread(Run) { IsBackground = true, Name = "IsleWaveform" };
            if (!_thread.IsAlive && _thread.ThreadState.HasFlag(ThreadState.Unstarted)) _thread.Start();
        }
        _wake.Set();
    }

    /// Stop capturing (call while paused) and clear the levels.
    public void Stop()
    {
        _active = false; _tapOn = false;
        _wake.Set();
        lock (_lock) { Array.Clear(_levels); Array.Clear(_prev); }
    }

    void Run()
    {
        while (true)
        {
            try
            {
                if (!_active) { _tapOn = false; _wake.WaitOne(); continue; }
                string target; lock (_ctl) target = _target;
                int pid = ProcessLoopback.FindTopLevel(target);
                if (pid == 0) { _tapOn = false; _wake.WaitOne(PollMs); continue; } // not running: stay empty, no global fallback
                CaptureLoop(target, pid);
            }
            catch (Exception e) { _tapOn = false; App.Log(e); _wake.WaitOne(PollMs); }
        }
    }

    void CaptureLoop(string target, int pid)
    {
        using var pl = ProcessLoopback.Open(pid);
        _fill = 0; _ref = -30;
        long started = Environment.TickCount64, polled = started;
        _lastData = started; _tapOn = true;
        try
        {
            while (_active)
            {
                pl.Pump(100, OnData);
                long now = Environment.TickCount64;
                if (now - _lastData > StallMs) return; // stalled (device change, tap died): rebuild
                if (now - polled < PollMs) continue;
                polled = now;
                string t; lock (_ctl) t = _target;
                if (t != target || ProcessLoopback.FindTopLevel(target) != pid) return; // re-aimed, or the player relaunched
            }
        }
        finally { _tapOn = false; }
    }

    // Capture thread: no UI objects here. Interleaved stereo float at 48 kHz.
    void OnData(float[] data, int frames, bool silent)
    {
        _lastData = Environment.TickCount64;
        for (int i = 0; i < frames; i++)
        {
            _buf[_fill++] = silent ? 0 : (data[i * 2] + data[i * 2 + 1]) * 0.5f;
            if (_fill == N)
            {
                Analyze(ProcessLoopback.SampleRate);
                Array.Copy(_buf, N / 2, _buf, 0, N / 2); _fill = N / 2; // 50% overlap: twice the update rate
            }
        }
    }

    void Analyze(int rate)
    {
        for (int i = 0; i < N; i++) { _fft[i].X = _buf[i] * _window[i]; _fft[i].Y = 0; }
        FastFourierTransform.FFT(true, 10, _fft);
        var db = new double[Bands];
        double loudest = -120;
        for (int b = 0; b < Bands; b++)
        {
            // log-spaced 45 Hz .. 16 kHz
            double f0 = 45 * Math.Pow(16000.0 / 45, (double)b / Bands), f1 = 45 * Math.Pow(16000.0 / 45, (double)(b + 1) / Bands);
            int lo = Math.Max(1, (int)(f0 * N / rate)), hi = Math.Max(lo + 1, (int)Math.Ceiling(f1 * N / rate));
            double m = 0;
            for (int k = lo; k < hi && k < N / 2; k++) m = Math.Max(m, Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y));
            // x2 = Hann coherent gain; sqrt(f/200) ≈ +3 dB/octave tilt, since music has far less energy up high
            m *= 2 * Math.Sqrt(Math.Max(f0, 60) / 200);
            db[b] = 20 * Math.Log10(m + 1e-9);
            loudest = Math.Max(loudest, db[b]);
        }
        // auto-gain: jump up to new peaks, drift down slowly in quiet passages (never below a floor so silence stays flat)
        _ref = loudest > _ref ? loudest : Math.Max(-50, _ref - 0.05);
        const double range = 42;
        lock (_lock)
            for (int b = 0; b < Bands; b++)
            {
                double v = Math.Clamp((db[b] - (_ref - range)) / range, 0, 1);
                v = Math.Pow(v, 1.6); // expand: quiet bands sit low, hits jump high
                float t = (float)v;
                if (t - _prev[b] > 0.12f) t = Math.Min(1, t * 1.15f); // transient kick
                _prev[b] = (float)v;
                _levels[b] += (t - _levels[b]) * (t > _levels[b] ? 0.85f : 0.22f);
            }
    }

    /// UI thread: copies the current levels (0..1) into dest.
    public void Snapshot(float[] dest)
    {
        if (!_tapOn)
        {
            double t = Environment.TickCount64 / 1000.0;
            for (int i = 0; i < Bands; i++) dest[i] = (float)(0.35 + 0.25 * Math.Sin(t * (2.1 + i * 0.37) + i));
            return;
        }
        lock (_lock)
        {
            if (Environment.TickCount64 - _lastData > 120) for (int i = 0; i < Bands; i++) _levels[i] *= 0.85f;
            Array.Copy(_levels, dest, Bands);
        }
    }
}
