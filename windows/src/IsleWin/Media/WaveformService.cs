using NAudio.Dsp;
using NAudio.Wave;

namespace IsleWin.Media;

/// 16-band spectrum of the default output (loopback). The capture thread writes a locked float[16]; the UI calls Snapshot.
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
    WasapiLoopbackCapture? _cap;
    readonly float[] _prev = new float[Bands];
    int _fill; long _lastData; volatile bool _failed;
    double _ref = -30; // auto-gain: loudest recent band, in dB

    public WaveformService() { for (int i = 0; i < N; i++) _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1)))); }

    public void Start()
    {
        if (_cap != null) return;
        try
        {
            _failed = false; _fill = 0;
            var c = new WasapiLoopbackCapture();
            c.DataAvailable += OnData;
            c.RecordingStopped += (_, e) => { if (e.Exception != null) { App.Log(e.Exception); _failed = true; } };
            _cap = c;
            c.StartRecording();
        }
        catch (Exception e) { App.Log(e); _failed = true; Release(); }
    }

    public void Stop()
    {
        Release();
        lock (_lock) Array.Clear(_levels);
    }

    void Release()
    {
        var c = _cap; _cap = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    // Capture thread: no UI objects here.
    void OnData(object? s, WaveInEventArgs e)
    {
        try
        {
            var f = ((WasapiLoopbackCapture)s!).WaveFormat;
            int ch = Math.Max(1, f.Channels), bytes = f.BitsPerSample / 8, frames = e.BytesRecorded / (bytes * ch);
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && bytes == 4);
            if (!isFloat && bytes != 2) return;
            _lastData = Environment.TickCount64;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = (i * ch + c) * bytes;
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, o) : BitConverter.ToInt16(e.Buffer, o) / 32768f;
                }
                _buf[_fill++] = sum / ch;
                if (_fill == N)
                {
                    Analyze(f.SampleRate);
                    Array.Copy(_buf, N / 2, _buf, 0, N / 2); _fill = N / 2; // 50% overlap: twice the update rate
                }
            }
        }
        catch (Exception ex) { App.Log(ex); _failed = true; }
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
        if (_failed || _cap == null)
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
