using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IsleWin.Media;

/// Isle's sounds (NotificationSound.swift): Matthew's four Material sounds (CC BY 4.0, see Sounds/ATTRIBUTION.txt),
/// played once through the default output. A sound already playing is stopped and started again, as his are;
/// no volume of its own and no rate limit. m4a is decoded by Media Foundation (MediaFoundationReader).
public static class Sounds
{
    static readonly object Gate = new();
    static WasapiOut? _out;
    static MediaFoundationReader? _reader;

    /// `name` is the file name without ".m4a", e.g. "alert_simple" (the settings store these names).
    public static void Play(string name) => Task.Run(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Sounds", name + ".m4a");
        if (!File.Exists(path)) return;
        lock (Gate)
        {
            try
            {
                StopLocked();
                _reader = new MediaFoundationReader(path);
                _out = new WasapiOut(AudioClientShareMode.Shared, 100);
                _out.Init(_reader);
                _out.Play();
            }
            catch (Exception e) { App.Log(e); StopLocked(); }
        }
    });

    static void StopLocked()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _reader?.Dispose(); _reader = null;
    }
}
