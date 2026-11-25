using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleWin.Core;

public sealed record SpotifyPlayback(string? TrackId, string Title, string Artist, string Album, string? ArtUrl,
    TimeSpan Duration, TimeSpan Progress, DateTimeOffset FetchedAt, bool IsPlaying, bool Shuffle, string Repeat /* off|context|track */,
    string? DeviceId, string DeviceName, string DeviceType /* Computer, Smartphone, Speaker, TV, ... */);

public sealed record SpotifyToken(string Access, string? Refresh, DateTimeOffset Expires);

public static class SpotifyApi
{
    public const string RedirectUri = "http://127.0.0.1:43827/callback";
    public const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing";

    static string? Str(JsonNode? n)
    {
        try { return n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; } catch { return null; }
    }
    static long Num(JsonNode? n)
    {
        try
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d)) return (long)d;
            }
        }
        catch { }
        return 0;
    }
    static bool Bool(JsonNode? n) { try { return n is JsonValue v && v.TryGetValue<bool>(out var b) && b; } catch { return false; } }

    static string? LargestImage(JsonNode? images)
    {
        if (images is not JsonArray arr) return null;
        string? best = null; long bestW = -1;
        foreach (var im in arr)
        {
            var url = Str(im?["url"]);
            if (url is null) continue;
            var w = Num(im?["width"]);
            if (w > bestW) { bestW = w; best = url; }
        }
        return best;
    }

    /// null when there's no item (204 body, ad, unknown type).
    public static SpotifyPlayback? ParsePlayer(string json, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch { return null; }
        if (root is not JsonObject o) return null;
        if (o["item"] is not JsonObject item) return null;

        string title, artist, album; string? art;
        var type = Str(item["type"]);
        if (type == "track")
        {
            title = Str(item["name"]) ?? "";
            var names = new List<string>();
            if (item["artists"] is JsonArray ar)
                foreach (var a in ar) { var n = Str(a?["name"]); if (!string.IsNullOrEmpty(n)) names.Add(n); }
            artist = string.Join(", ", names);
            album = Str(item["album"]?["name"]) ?? "";
            art = LargestImage(item["album"]?["images"]);
        }
        else if (type == "episode")
        {
            title = Str(item["name"]) ?? "";
            var show = Str(item["show"]?["name"]) ?? "";
            artist = show; album = show;
            art = LargestImage(item["images"]) ?? LargestImage(item["show"]?["images"]);
        }
        else return null;

        var dev = o["device"];
        var repeat = Str(o["repeat_state"]) ?? "off";
        if (repeat is not ("off" or "context" or "track")) repeat = "off";
        return new SpotifyPlayback(
            Str(item["id"]), title, artist, album, art,
            TimeSpan.FromMilliseconds(Num(item["duration_ms"])),
            TimeSpan.FromMilliseconds(Num(o["progress_ms"])),
            fetchedAt, Bool(o["is_playing"]), Bool(o["shuffle_state"]), repeat,
            Str(dev?["id"]), Str(dev?["name"]) ?? "", Str(dev?["type"]) ?? "");
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (string Verifier, string Challenge) NewPkce()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var sb = new StringBuilder(64);
        for (var i = 0; i < 64; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        var v = sb.ToString();
        return (v, B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(v))));
    }

    public static string AuthorizeUrl(string clientId, string challenge, string state) =>
        "https://accounts.spotify.com/authorize"
        + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
        + "&scope=" + Uri.EscapeDataString(Scopes)
        + "&code_challenge_method=S256"
        + "&code_challenge=" + Uri.EscapeDataString(challenge)
        + "&state=" + Uri.EscapeDataString(state);

    /// null on error JSON.
    public static SpotifyToken? ParseToken(string json, DateTimeOffset now)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var access = Str(o["access_token"]);
            if (string.IsNullOrEmpty(access)) return null;
            var secs = Num(o["expires_in"]);
            if (secs <= 0) secs = 3600;
            var refresh = Str(o["refresh_token"]);
            return new SpotifyToken(access, string.IsNullOrEmpty(refresh) ? null : refresh, now.AddSeconds(secs));
        }
        catch (JsonException) { return null; }
    }
}
