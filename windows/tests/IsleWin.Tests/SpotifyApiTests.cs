using System.Security.Cryptography;
using System.Text;
using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

public class SpotifyApiTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    const string TrackJson = """
    {"device":{"id":"dev1","name":"Pixel","type":"Smartphone"},"repeat_state":"context","shuffle_state":true,
     "progress_ms":65000,"is_playing":true,
     "item":{"type":"track","id":"t1","name":"Song","duration_ms":200000,
       "artists":[{"name":"A"},{"name":"B"}],
       "album":{"name":"Rec","images":[{"url":"small","width":64},{"url":"big","width":640},{"url":"mid","width":300}]}}}
    """;

    [Fact]
    public void ParsePlayer_Track()
    {
        var p = SpotifyApi.ParsePlayer(TrackJson, Now)!;
        Assert.Equal("t1", p.TrackId);
        Assert.Equal("Song", p.Title);
        Assert.Equal("A, B", p.Artist);
        Assert.Equal("Rec", p.Album);
        Assert.Equal("big", p.ArtUrl);
        Assert.Equal(TimeSpan.FromSeconds(200), p.Duration);
        Assert.Equal(TimeSpan.FromSeconds(65), p.Progress);
        Assert.Equal(Now, p.FetchedAt);
        Assert.True(p.IsPlaying); Assert.True(p.Shuffle);
        Assert.Equal("context", p.Repeat);
        Assert.Equal("dev1", p.DeviceId); Assert.Equal("Pixel", p.DeviceName); Assert.Equal("Smartphone", p.DeviceType);
    }

    [Fact]
    public void ParsePlayer_Episode()
    {
        var p = SpotifyApi.ParsePlayer("""
        {"is_playing":true,"progress_ms":1000,"repeat_state":"off","shuffle_state":false,
         "device":{"id":null,"name":"TV","type":"TV"},
         "item":{"type":"episode","id":"e1","name":"Ep 1","duration_ms":3600000,
           "images":[{"url":"epimg","width":640}],"show":{"name":"The Show","images":[{"url":"showimg","width":300}]}}}
        """, Now)!;
        Assert.Equal("Ep 1", p.Title);
        Assert.Equal("The Show", p.Artist);
        Assert.Equal("The Show", p.Album);
        Assert.Equal("epimg", p.ArtUrl);
        Assert.Null(p.DeviceId);
    }

    [Fact]
    public void ParsePlayer_Paused()
    {
        var p = SpotifyApi.ParsePlayer(TrackJson.Replace("\"is_playing\":true", "\"is_playing\":false"), Now)!;
        Assert.False(p.IsPlaying);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"is_playing\":true,\"item\":null}")]
    [InlineData("{\"item\":{\"type\":\"ad\"}}")]
    [InlineData("not json")]
    public void ParsePlayer_NoItem_Null(string json) => Assert.Null(SpotifyApi.ParsePlayer(json, Now));

    [Fact]
    public void NewPkce_VerifierAndChallenge()
    {
        var (v, c) = SpotifyApi.NewPkce();
        Assert.Equal(64, v.Length);
        Assert.All(v, ch => Assert.True(char.IsAsciiLetterOrDigit(ch) || "-._~".Contains(ch)));
        var expect = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(v)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expect, c);
        Assert.DoesNotContain('=', c);
        Assert.NotEqual(v, SpotifyApi.NewPkce().Verifier);
    }

    [Fact]
    public void AuthorizeUrl_HasParams()
    {
        var u = SpotifyApi.AuthorizeUrl("cid", "chal", "st");
        Assert.StartsWith("https://accounts.spotify.com/authorize?", u);
        Assert.Contains("response_type=code", u);
        Assert.Contains("client_id=cid", u);
        Assert.Contains("code_challenge_method=S256", u);
        Assert.Contains("code_challenge=chal", u);
        Assert.Contains("state=st", u);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(SpotifyApi.RedirectUri), u);
        Assert.Contains("scope=" + Uri.EscapeDataString(SpotifyApi.Scopes), u);
    }

    [Fact]
    public void ParseToken_Success()
    {
        var t = SpotifyApi.ParseToken("""{"access_token":"a","token_type":"Bearer","expires_in":3600,"refresh_token":"r"}""", Now)!;
        Assert.Equal("a", t.Access);
        Assert.Equal("r", t.Refresh);
        Assert.Equal(Now.AddSeconds(3600), t.Expires);
        Assert.Null(SpotifyApi.ParseToken("""{"access_token":"a","expires_in":10}""", Now)!.Refresh);
    }

    [Theory]
    [InlineData("""{"error":"invalid_grant","error_description":"Refresh token revoked"}""")]
    [InlineData("")]
    [InlineData("<html>")]
    public void ParseToken_Error_Null(string json) => Assert.Null(SpotifyApi.ParseToken(json, Now));
}
