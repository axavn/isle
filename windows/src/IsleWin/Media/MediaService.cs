using System.IO;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Control;

namespace IsleWin.Media;

/// Spotify (preferred) or the current SMTC session. All state is touched on the UI thread only.
public sealed class MediaService : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _debounce;
    readonly Dictionary<string, BitmapImage> _art = new();
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _refreshing, _again;
    TimeSpan _pos; DateTimeOffset _posAt;

    public bool HasTrack { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public BitmapImage? Artwork { get; private set; }
    /// The cover's three colours (Isle's picker); the grey fallback when there is no art.
    public ArtworkPalette Palette { get; private set; } = ArtworkPalette.Fallback;
    public bool IsPlaying { get; private set; }
    /// SMTC SourceAppUserModelId of the session being shown (null when none); feed it to WaveformService.SetTarget.
    public string? SourceAppId { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool Shuffle { get; private set; }
    public bool ShuffleEnabled { get; private set; }
    public MediaPlaybackAutoRepeatMode Repeat { get; private set; }
    public bool RepeatEnabled { get; private set; }

    public MediaService()
    {
        _debounce = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Refresh(); };
    }

    public async void Start()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => Poke();
            _mgr.CurrentSessionChanged += (_, _) => Poke();
            await Refresh();
        }
        catch (Exception e) { App.Log(e); }
    }

    /// Extrapolated playback position, clamped to the duration.
    public TimeSpan GetPosition()
    {
        var p = IsPlaying ? _pos + (DateTimeOffset.Now - _posAt) : _pos;
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
    }

    void Poke() => _ui.BeginInvoke(() => { if (!_debounce.IsEnabled) _debounce.Start(); });
    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => Poke();
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => Poke();
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) => Poke();

    async Task Refresh()
    {
        if (_refreshing) { _again = true; return; }
        _refreshing = true;
        try { do { _again = false; await RefreshOnce(); } while (_again); }
        catch (Exception e) { App.Log(e); }
        finally { _refreshing = false; }
    }

    async Task RefreshOnce()
    {
        var s = _mgr?.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) ?? _mgr?.GetCurrentSession();
        if (!ReferenceEquals(s, _session))
        {
            if (_session != null) { _session.MediaPropertiesChanged -= OnProps; _session.PlaybackInfoChanged -= OnPlayback; _session.TimelinePropertiesChanged -= OnTimeline; }
            _session = s;
            SourceAppId = s?.SourceAppUserModelId;
            if (s != null) { s.MediaPropertiesChanged += OnProps; s.PlaybackInfoChanged += OnPlayback; s.TimelinePropertiesChanged += OnTimeline; }
        }
        if (s == null) { Apply(false, "", "", "", null, false, default, false, false, default, false); return; }

        var props = await s.TryGetMediaPropertiesAsync();
        var pb = s.GetPlaybackInfo();
        var tl = s.GetTimelineProperties();
        if (!ReferenceEquals(s, _session)) { _again = true; return; }

        var key = props.Title + "\u0001" + props.Artist + "\u0001" + props.AlbumTitle;
        if (!_art.TryGetValue(key, out var art) && props.Thumbnail != null)
        {
            try
            {
                using var rs = await props.Thumbnail.OpenReadAsync();
                var ms = new MemoryStream();
                await rs.AsStreamForRead().CopyToAsync(ms);
                if (ms.Length > 0)
                {
                    ms.Position = 0;
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 360; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
                    if (_art.Count > 20) _art.Clear();
                    _art[key] = art = bi;
                }
            }
            catch (Exception e) { App.Log(e); }
        }
        var has = !string.IsNullOrEmpty(props.Title) || !string.IsNullOrEmpty(props.Artist);
        var playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _pos = tl.Position; _posAt = tl.LastUpdatedTime;
        Apply(has, props.Title, props.Artist, props.AlbumTitle, art, playing, tl.EndTime - tl.StartTime,
              pb.IsShuffleActive ?? false, pb.Controls.IsShuffleEnabled, pb.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None, pb.Controls.IsRepeatEnabled);
    }

    void Apply(bool has, string title, string artist, string album, BitmapImage? art, bool playing, TimeSpan dur,
               bool shuffle, bool shuffleOn, MediaPlaybackAutoRepeatMode rep, bool repOn)
    {
        if (!ReferenceEquals(art, Artwork)) Palette = CoverColors.Palette(art);
        HasTrack = has; Title = title; Artist = artist; Album = album; Artwork = art; IsPlaying = playing; Duration = dur;
        Shuffle = shuffle; ShuffleEnabled = shuffleOn; Repeat = rep; RepeatEnabled = repOn;
        if (!has) _pos = default;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void PlayPause() { if (_session != null) _ = _session.TryTogglePlayPauseAsync(); }
    public void Next() { if (_session != null) _ = _session.TrySkipNextAsync(); }
    public void Previous() { if (_session != null) _ = _session.TrySkipPreviousAsync(); }
    public void ToggleShuffle() { if (_session != null) _ = _session.TryChangeShuffleActiveAsync(!Shuffle); }
    public void CycleRepeat()
    {
        var next = Repeat switch { MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List, MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track, _ => MediaPlaybackAutoRepeatMode.None };
        if (_session != null) _ = _session.TryChangeAutoRepeatModeAsync(next);
    }
    public void Seek(TimeSpan t)
    {
        if (_session == null) return;
        _pos = t; _posAt = DateTimeOffset.Now;
        _ = _session.TryChangePlaybackPositionAsync(t.Ticks);
    }
}
