using System.Net;
using Jellyfin.Plugin.MultiLyrics.Alignment;
using Jellyfin.Plugin.MultiLyrics.Core;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data", false, false)]
    [InlineData("http://169.254.169.254/latest/meta-data", true, false)] // metadata: always refused
    [InlineData("http://metadata.google.internal/computeMetadata/v1", true, false)]
    [InlineData("http://metadata.goog/", true, false)]
    [InlineData("http://127.0.0.1:8787/align", false, false)]
    [InlineData("http://127.0.0.1:8787/align", true, true)] // loopback allowed with explicit toggle
    [InlineData("http://192.168.1.20:8787/align", false, false)]
    [InlineData("http://192.168.1.20:8787/align", true, true)]
    [InlineData("http://10.0.0.5:8787/align", true, true)]
    [InlineData("http://172.16.4.4:8787/align", true, true)]
    [InlineData("http://100.100.100.200/", true, false)] // Alibaba cloud metadata (CGNAT range blocked)
    [InlineData("http://8.8.8.8/", false, true)]
    [InlineData("https://alignment.example.com/", false, true)]
    [InlineData("ftp://alignment.example.com/", false, false)]
    [InlineData("file:///etc/passwd", false, false)]
    [InlineData("http://user:secret@host/", false, false)]
    [InlineData("http://[::1]:8787/", false, false)]
    [InlineData("http://[::1]:8787/", true, true)]
    [InlineData("http://[fe80::1]:8787/", false, false)]
    [InlineData("http://[fd00::1]:8787/", true, true)]
    public void CheckUrlClassifiesHosts(string url, bool allowPrivate, bool expectedAllowed)
    {
        var uri = new Uri(url);
        var decision = SsrfGuard.CheckUrl(uri, allowPrivate);
        Assert.Equal(expectedAllowed, decision.Allowed);
    }

    [Fact]
    public void CheckAddressBlocksLoopbackByDefault()
    {
        Assert.False(SsrfGuard.CheckAddress(IPAddress.Parse("127.0.0.1"), false).Allowed);
        Assert.True(SsrfGuard.CheckAddress(IPAddress.Parse("127.0.0.1"), true).Allowed);
        Assert.False(SsrfGuard.CheckAddress(IPAddress.Parse("169.254.169.254"), true).Allowed);
        Assert.True(SsrfGuard.CheckAddress(IPAddress.Parse("140.82.121.4"), false).Allowed);
    }

    [Fact]
    public async Task RejectsUnparseableUrls()
    {
        // CheckUrl requires an absolute URI; ValidateAsync refuses anything unparsable.
        var decision = await SsrfGuard.ValidateAsync("not a url", false);
        Assert.False(decision.Allowed);
    }
}

public class LrcParserTests
{
    [Fact]
    public void ParsesTimestampsAndText()
    {
        var lrc = "[00:12.34]First line\n[01:02.03]Second line\n";
        var lines = LrcParser.Parse(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(12.34), lines[0].Time);
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(62.03), lines[1].Time);
        Assert.Equal("Second line", lines[1].Text);
    }

    [Fact]
    public void StripsWordLevelTagsAndExpandsRepeatedTimestamps()
    {
        var lrc = "[00:10.00]<00:10.00>Syn <00:10.50>words [00:20.00]<00:20.00>Repeat";
        var lines = LrcParser.Parse(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Syn words", lines[0].Text);
        Assert.Equal("Repeat", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(20), lines[1].Time);
    }

    [Fact]
    public void AppliesNegativeOffsetMetadata()
    {
        var lines = LrcParser.Parse("[offset:500]\n[00:10.00]text");
        Assert.Equal(TimeSpan.FromSeconds(9.5), lines[0].Time);
    }

    [Fact]
    public void LooksSyncedDetectsTimestamps()
    {
        Assert.True(LrcParser.LooksSynced("[00:01.00]x"));
        Assert.True(LrcParser.LooksSynced("[01:00]x"));
        Assert.False(LrcParser.LooksSynced("just plain text\nmore text"));
        Assert.False(LrcParser.LooksSynced(""));
    }

    [Fact]
    public void FormatSyncedRoundTrips()
    {
        var lines = LrcParser.Parse("[00:05.00]alpha\n[00:07.50]beta");
        var formatted = LrcParser.FormatSynced(lines);
        var reparsed = LrcParser.Parse(formatted);

        Assert.Equal(2, reparsed.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), reparsed[0].Time);
        Assert.Equal("alpha", reparsed[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(7.5), reparsed[1].Time);
    }

    [Fact]
    public void PlainLinesSplitsAndTrims()
    {
        var lines = LrcParser.PlainLines("  a \r\n\r\n b\r\n\nc ");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }
}

public class LyricResultTests
{
    [Fact]
    public void KindClassification()
    {
        Assert.Equal(LyricKind.Synced, new LyricResult { SyncedLrc = "[00:01.00]x" }.Kind);
        Assert.Equal(LyricKind.Plain, new LyricResult { PlainText = "text" }.Kind);
        Assert.Equal(LyricKind.Instrumental, new LyricResult { IsInstrumental = true }.Kind);
        Assert.Equal(LyricKind.None, new LyricResult().Kind);
    }

    [Fact]
    public void InstrumentalHasNoHostBody()
    {
        var result = new LyricResult { IsInstrumental = true };
        Assert.Null(result.BodyForHost());
    }
}
