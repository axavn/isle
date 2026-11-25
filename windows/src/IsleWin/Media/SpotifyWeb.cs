using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IsleWin.Core;
using Windows.Security.Credentials;

namespace IsleWin.Media;

/// <summary>Spotify Web API client: shows and controls playback on OTHER devices (local playback comes from SMTC).</summary>
public sealed class SpotifyWeb
{
    const string VaultResource = "Isle.Spotify";
    const string PremiumMsg = "Spotify Premium is needed to control other devices";
    const string ExpiredMsg = "Spotify sign-in expired — connect again from the tray menu";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    sealed class AuthRevokedException : Exception { }

    readonly object _gate = new();
    readonly SemaphoreSlim _tokenLock = new(1, 1);
    readonly SemaphoreSlim _wake = new(0, 1);

    string? _clientId;
    string? _refresh;
    string? _access;
    DateTimeOffset _accessExpires;

    CancellationTokenSource? _loopCts;
    volatile bool _suspended;
    volatile bool _open;
    volatile bool _connecting;
    bool _started;
    SpotifyPlayback? _last;
    DateTimeOffset _lastPremiumWarn;

    public string? ClientId { get { lock (_gate) return _clientId; } }
    public bool IsConnected { get { lock (_gate) return _clientId != null && _refresh != null; } }

    public event Action<SpotifyPlayback?>? Changed;
    public event Action<string>? Problem;

    static string ConfigPath => Path.Combine(IslePaths.Default.Root, "spotify.json");

    // ---------- lifecycle ----------

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        try
        {
            string? cid = null;
            try
            {
                if (File.Exists(ConfigPath))
                    cid = JsonNode.Parse(File.ReadAllText(ConfigPath))?["client_id"]?.GetValue<string>();
            }
            catch (Exception ex) { App.Log(ex); }
            if (string.IsNullOrWhiteSpace(cid)) cid = null;

            var cred = FindCredential(cid);
            lock (_gate)
            {
                _clientId = cid ?? cred?.UserName;
                _refresh = cred?.Password;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        if (IsConnected) StartLoop();
    }

    static PasswordCredential? FindCredential(string? clientId)
    {
        try
        {
            var vault = new PasswordVault();
            var all = vault.FindAllByResource(VaultResource);
            PasswordCredential? pick = null;
            foreach (var c in all)
                if (clientId == null || c.UserName == clientId) { pick = c; break; }
            pick?.RetrievePassword();
            return string.IsNullOrEmpty(pick?.Password) ? null : pick;
        }
        catch { return null; } // no entry => COMException
    }

    static void ClearVault()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var c in vault.FindAllByResource(VaultResource).ToList()) vault.Remove(c);
        }
        catch { }
    }

    void SaveRefresh(string clientId, string refresh)
    {
        lock (_gate) _refresh = refresh;
        try
        {
            ClearVault();
            new PasswordVault().Add(new PasswordCredential(VaultResource, clientId, refresh));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public async Task<bool> ConnectAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || _connecting) return false;
        _connecting = true;
        try { return await Task.Run(() => ConnectCore(clientId.Trim())).ConfigureAwait(false); }
        catch (Exception ex) { App.Log(ex); return false; }
        finally { _connecting = false; }
    }

    async Task<bool> ConnectCore(string clientId)
    {
        var (verifier, challenge) = SpotifyApi.NewPkce();
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:43827/callback/");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { listener.Start(); }
        catch (Exception ex) { App.Log(ex); return false; }
        using var reg = cts.Token.Register(() => { try { listener.Close(); } catch { } });

        try
        {
            Process.Start(new ProcessStartInfo(SpotifyApi.AuthorizeUrl(clientId, challenge, state)) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex); return false; }

        string? code = null;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync().ConfigureAwait(false);
                var q = ctx.Request.QueryString;
                var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
                if (!path.EndsWith("/callback", StringComparison.Ordinal) || (q["code"] == null && q["error"] == null))
                {
                    ctx.Response.StatusCode = 404; ctx.Response.Close(); continue;
                }
                var ok = q["state"] == state && q["error"] == null && !string.IsNullOrEmpty(q["code"]);
                await Respond(ctx.Response, ok).ConfigureAwait(false);
                if (ok) code = q["code"];
                break;
            }
        }
        catch (Exception ex) { if (!cts.IsCancellationRequested) App.Log(ex); return false; }
        if (code == null) return false;

        try
        {
            using var resp = await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = SpotifyApi.RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }, CancellationToken.None).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var tok = resp.IsSuccessStatusCode ? SpotifyApi.ParseToken(body, DateTimeOffset.UtcNow) : null;
            if (tok?.Refresh == null) { App.Log("Spotify token exchange failed: " + (int)resp.StatusCode); return false; }

            try
            {
                Directory.CreateDirectory(IslePaths.Default.Root);
                File.WriteAllText(ConfigPath, new JsonObject { ["client_id"] = clientId }.ToJsonString());
            }
            catch (Exception ex) { App.Log(ex); }
            lock (_gate) { _clientId = clientId; _access = tok.Access; _accessExpires = tok.Expires; }
            SaveRefresh(clientId, tok.Refresh);
            StartLoop();
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    static async Task Respond(HttpListenerResponse r, bool ok)
    {
        try
        {
            var html = ok
                ? "<!doctype html><meta charset=utf-8><title>Isle</title><body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;text-align:center;padding-top:20vh\"><h2>Connected — you can close this tab</h2>"
                : "<!doctype html><meta charset=utf-8><title>Isle</title><body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;text-align:center;padding-top:20vh\"><h2>Spotify sign-in failed</h2><p>Go back to Isle and try again.</p>";
            var bytes = Encoding.UTF8.GetBytes(html);
            r.ContentType = "text/html; charset=utf-8";
            r.ContentLength64 = bytes.Length;
            await r.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch { }
        finally { try { r.Close(); } catch { } }
    }

    public void Disconnect()
    {
        StopLoop();
        ClearVault();
        try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); } catch (Exception ex) { App.Log(ex); }
        lock (_gate) { _clientId = null; _refresh = null; _access = null; _last = null; }
        Raise(null);
    }

    // ---------- control surface ----------

    public void SetLocalPlaying(bool playing)
    {
        if (_suspended == playing) return; // MediaService calls this on every SMTC refresh: act on a change only
        _suspended = playing;
        if (playing)
        {
            bool had;
            lock (_gate) { had = _last != null; _last = null; }
            if (had) Raise(null);
        }
        Wake();
    }

    public void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        Wake();
    }

    public void PlayPause()
    {
        bool playing;
        lock (_gate) playing = _last?.IsPlaying ?? false;
        Command(HttpMethod.Put, playing ? "pause" : "play");
    }
    public void Next() => Command(HttpMethod.Post, "next");
    public void Previous() => Command(HttpMethod.Post, "previous");
    public void Seek(TimeSpan to) =>
        Command(HttpMethod.Put, "seek?position_ms=" + (long)Math.Max(0, to.TotalMilliseconds));
    public void SetShuffle(bool on) => Command(HttpMethod.Put, "shuffle?state=" + (on ? "true" : "false"));
    public void SetRepeat(string state)
    {
        if (state is not ("off" or "context" or "track")) return;
        Command(HttpMethod.Put, "repeat?state=" + state);
    }

    void Command(HttpMethod method, string path)
    {
        if (!IsConnected) return;
        string? dev;
        lock (_gate) dev = _last?.DeviceId;
        if (!string.IsNullOrEmpty(dev))
            path += (path.Contains('?') ? "&" : "?") + "device_id=" + Uri.EscapeDataString(dev);
        _ = Task.Run(async () =>
        {
            try
            {
                using var r = await SendAuthed(() => new HttpRequestMessage(method, "https://api.spotify.com/v1/me/player/" + path)
                {
                    Content = method == HttpMethod.Put ? new ByteArrayContent([]) : null
                }, CancellationToken.None).ConfigureAwait(false);
                if (r.StatusCode == HttpStatusCode.Forbidden) WarnPremium();
                else if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.NotFound && r.StatusCode != HttpStatusCode.TooManyRequests)
                    App.Log("Spotify command " + path + " -> " + (int)r.StatusCode);
            }
            catch (AuthRevokedException) { HandleRevoked(); }
            catch (Exception ex) { App.Log(ex); }
            await Task.Delay(300).ConfigureAwait(false);
            Wake();
        });
    }

    void WarnPremium()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (now - _lastPremiumWarn < TimeSpan.FromMinutes(1)) return;
            _lastPremiumWarn = now;
        }
        try { Problem?.Invoke(PremiumMsg); } catch (Exception ex) { App.Log(ex); }
    }

    void HandleRevoked()
    {
        StopLoop();
        ClearVault();
        bool had;
        lock (_gate) { _refresh = null; _access = null; had = _last != null; _last = null; }
        if (had) Raise(null);
        try { Problem?.Invoke(ExpiredMsg); } catch (Exception ex) { App.Log(ex); }
    }

    void Raise(SpotifyPlayback? p)
    {
        try { Changed?.Invoke(p); } catch (Exception ex) { App.Log(ex); }
    }

    // ---------- tokens ----------

    static Task<HttpResponseMessage> PostToken(Dictionary<string, string> form, CancellationToken ct) =>
        Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);

    async Task<string> GetAccess(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? cid, refresh, access; DateTimeOffset exp;
            lock (_gate) { cid = _clientId; refresh = _refresh; access = _access; exp = _accessExpires; }
            if (cid == null || refresh == null) throw new AuthRevokedException();
            if (!forceRefresh && access != null && exp - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return access;

            using var resp = await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = cid,
            }, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new AuthRevokedException(); // invalid_grant: refresh token revoked/expired
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("Spotify token refresh " + (int)resp.StatusCode);
            var tok = SpotifyApi.ParseToken(body, DateTimeOffset.UtcNow) ?? throw new HttpRequestException("Spotify token refresh: bad body");
            lock (_gate) { _access = tok.Access; _accessExpires = tok.Expires; }
            if (tok.Refresh != null && tok.Refresh != refresh) SaveRefresh(cid, tok.Refresh);
            return tok.Access;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>Sends an authorized request; on 401 refreshes the token once and retries.</summary>
    async Task<HttpResponseMessage> SendAuthed(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccess(attempt > 0, ct).ConfigureAwait(false);
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { resp.Dispose(); continue; }
            return resp;
        }
    }

    // ---------- polling loop ----------

    void StartLoop()
    {
        lock (_gate)
        {
            if (_loopCts != null) return;
            _loopCts = new CancellationTokenSource();
            var ct = _loopCts.Token;
            _ = Task.Run(() => Loop(ct));
        }
    }

    void StopLoop()
    {
        CancellationTokenSource? c;
        lock (_gate) { c = _loopCts; _loopCts = null; }
        try { c?.Cancel(); } catch { }
    }

    void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    async Task Loop(CancellationToken ct)
    {
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TimeSpan delay;
                if (_suspended) delay = Timeout.InfiniteTimeSpan;
                else
                {
                    delay = await PollOnce(ct).ConfigureAwait(false);
                    backoff = TimeSpan.Zero;
                }
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (AuthRevokedException) { HandleRevoked(); return; }
            catch (Exception ex)
            {
                App.Log(ex);
                backoff = backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                try { await _wake.WaitAsync(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }
        }
    }

    async Task<TimeSpan> PollOnce(CancellationToken ct)
    {
        using var r = await SendAuthed(() => new HttpRequestMessage(HttpMethod.Get,
            "https://api.spotify.com/v1/me/player?additional_types=episode"), ct).ConfigureAwait(false);

        if (r.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var s = r.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5;
            return TimeSpan.FromSeconds(Math.Clamp(s, 1, 300));
        }
        if (r.StatusCode == HttpStatusCode.NoContent) { Apply(null); return TimeSpan.FromSeconds(10); }
        if (!r.IsSuccessStatusCode) throw new HttpRequestException("Spotify player " + (int)r.StatusCode);

        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var p = SpotifyApi.ParsePlayer(body, DateTimeOffset.UtcNow);
        if (_suspended) return Timeout.InfiniteTimeSpan; // local playback started while the request was in flight
        Apply(p);
        if (p is { IsPlaying: true }) return TimeSpan.FromSeconds(_open ? 1 : 2);
        return TimeSpan.FromSeconds(10);
    }

    void Apply(SpotifyPlayback? p)
    {
        SpotifyPlayback? prev;
        lock (_gate) { prev = _last; _last = p; }
        if (Differs(prev, p)) Raise(p);
    }

    static bool Differs(SpotifyPlayback? a, SpotifyPlayback? b)
    {
        if (a == null || b == null) return a != b;
        if (a.TrackId != b.TrackId || a.Title != b.Title || a.IsPlaying != b.IsPlaying || a.DeviceId != b.DeviceId
            || a.DeviceName != b.DeviceName || a.Shuffle != b.Shuffle || a.Repeat != b.Repeat) return true;
        var expected = a.Progress;
        if (a.IsPlaying) expected += b.FetchedAt - a.FetchedAt;
        if (b.Duration > TimeSpan.Zero && expected > b.Duration) expected = b.Duration;
        return (b.Progress - expected).Duration() > TimeSpan.FromSeconds(2);
    }
}
