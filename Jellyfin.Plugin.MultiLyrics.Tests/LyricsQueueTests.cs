using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Configuration;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Lyrics;
using Jellyfin.Plugin.MultiLyrics.Sources;
using Jellyfin.Plugin.MultiLyrics.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

/// <summary>
/// Tests the priority queue behaviour with fake sources (no network) and a real on-disk cache.
/// </summary>
public sealed class LyricsQueueTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cachePath;

    public LyricsQueueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mlqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cachePath = Path.Combine(_dir, "cache.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static TrackQuery Query() => new()
    {
        ItemId = Guid.NewGuid(),
        Artist = "Synthetic Artist",
        Album = "Synthetic Album",
        Title = "Synthetic Song",
        DurationSec = 200
    };

    private LyricsSearchService NewQueue(IResultCache cache, params ILyricSource[] sources)
        => new(sources, cache, NullLogger<LyricsSearchService>.Instance);

    [Fact]
    public void InstrumentalWinKeepsTheInstrumentalKindInTheCache()
    {
        // The persistent cache remembers that a track is instrumental (metadata only, no text)
        // so a later sweep can tell it apart from a plain miss.
        var cache = new ResultCache(_cachePath);
        var query = Query();
        var entry = cache.GetOrAdd(query);
        cache.RecordWin(
            entry,
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, IsInstrumental = true, Message = "instrumental" },
            aligned: false);

        var stored = cache.Get(entry.Key);

        Assert.NotNull(stored);
        Assert.Equal(LyricKind.Instrumental, stored!.Kind);
        Assert.Equal(Constants.SourceNames.LrcLib, stored.WinningSource);
    }

    [Fact]
    public async Task MissFallsThroughToNextSource()
    {
        var cache = new ResultCache(_cachePath);
        var lrcLib = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(null));
        var ovh = new FakeSource(Constants.SourceNames.LyricsOvh, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LyricsOvh, SyncedLrc = "[00:01.00]x" }));

        var result = await NewQueue(cache, lrcLib, ovh).SearchAsync(Query(), null, CancellationToken.None);

        Assert.NotNull(result.Best);
        Assert.Equal(Constants.SourceNames.LyricsOvh, result.Best!.SourceName);
        Assert.Equal(1, lrcLib.Calls);
        Assert.Equal(1, ovh.Calls);

        // The miss was recorded against the track.
        var entry = cache.Get(result.Entry.Key);
        Assert.NotNull(entry);
        Assert.Contains(entry!.Attempts, a => a.Source == Constants.SourceNames.LrcLib && !a.Success);
    }

    [Fact]
    public async Task PlainIsRememberedWhileQueueLooksForSynced()
    {
        var cache = new ResultCache(_cachePath);
        var lrcLib = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, PlainText = "plain body" }));
        var ovh = new FakeSource(Constants.SourceNames.LyricsOvh, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LyricsOvh, SyncedLrc = "[00:02.00]synced" }));

        var result = await NewQueue(cache, lrcLib, ovh).SearchAsync(Query(), null, CancellationToken.None);

        Assert.NotNull(result.Best);
        Assert.Equal(LyricKind.Synced, result.Best!.Kind);
        Assert.Equal(Constants.SourceNames.LyricsOvh, result.Best.SourceName);
        Assert.Equal(1, lrcLib.Calls);
        Assert.Equal(1, ovh.Calls);
    }

    [Fact]
    public async Task PlainOnlySweepReturnsPlainFallback()
    {
        var cache = new ResultCache(_cachePath);
        var lrcLib = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(null));
        var ovh = new FakeSource(Constants.SourceNames.LyricsOvh, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LyricsOvh, PlainText = "plain body" }));

        var result = await NewQueue(cache, lrcLib, ovh).SearchAsync(Query(), null, CancellationToken.None);

        Assert.NotNull(result.Best);
        Assert.Equal(LyricKind.Plain, result.Best!.Kind);
        Assert.Equal(Constants.SourceNames.LyricsOvh, result.Best.SourceName);
    }

    [Fact]
    public async Task LiteralOrderLetsAPlainSourceWin()
    {
        // PreferSyncedLyrics = false: the first source with any lyrics wins. This is the only way
        // a plain-only source (Genius scrape, Tekstowo) can be seen while LRCLIB sits in the order.
        var cache = new ResultCache(_cachePath);
        var plain = new FakeSource(Constants.SourceNames.Tekstowo, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.Tekstowo, PlainText = "plain body" }));
        var synced = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:01.00]lrc" }));

        var cfg = new PluginConfiguration
        {
            SourceOrder = Constants.SourceNames.Tekstowo + "," + Constants.SourceNames.LrcLib,
            PreferSyncedLyrics = false
        };

        var service = new LyricsSearchService(
            [plain, synced],
            cache,
            NullLogger<LyricsSearchService>.Instance,
            () => cfg);

        var result = await service.SearchAsync(Query(), null, CancellationToken.None);

        Assert.NotNull(result.Best);
        Assert.Equal(Constants.SourceNames.Tekstowo, result.Best!.SourceName);
        Assert.Equal(LyricKind.Plain, result.Best.Kind);
        Assert.Equal(0, synced.Calls);
    }

    [Fact]
    public async Task SyncedStillWinsWhenPreferenceIsOn()
    {
        var cache = new ResultCache(_cachePath);
        var plain = new FakeSource(Constants.SourceNames.Tekstowo, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.Tekstowo, PlainText = "plain body" }));
        var synced = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:01.00]lrc" }));

        var cfg = new PluginConfiguration
        {
            SourceOrder = Constants.SourceNames.Tekstowo + "," + Constants.SourceNames.LrcLib,
            PreferSyncedLyrics = true
        };

        var service = new LyricsSearchService(
            [plain, synced],
            cache,
            NullLogger<LyricsSearchService>.Instance,
            () => cfg);

        var result = await service.SearchAsync(Query(), null, CancellationToken.None);

        Assert.Equal(Constants.SourceNames.LrcLib, result.Best!.SourceName);
        Assert.Equal(LyricKind.Synced, result.Best.Kind);
    }

    [Fact]
    public async Task TimeoutInOneSourceDoesNotAbortTheQueue()
    {
        var cache = new ResultCache(_cachePath);
        // A per-request HttpClient timeout surfaces as TaskCanceledException while the
        // caller's token is untouched.
        var lrcLib = new FakeSource(Constants.SourceNames.LrcLib, (_, _) =>
            Task.FromException<LyricResult?>(new TaskCanceledException("timeout")));
        var ovh = new FakeSource(Constants.SourceNames.LyricsOvh, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LyricsOvh, SyncedLrc = "[00:03.00]synced" }));

        var result = await NewQueue(cache, lrcLib, ovh).SearchAsync(Query(), null, CancellationToken.None);

        Assert.NotNull(result.Best);
        Assert.Equal(Constants.SourceNames.LyricsOvh, result.Best!.SourceName);
        Assert.Equal(1, ovh.Calls);
    }

    [Fact]
    public async Task ChallengeMissIsRecordedAndNothingIsStored()
    {
        var cache = new ResultCache(_cachePath);
        // Genius scrape mode returning null is what a Cloudflare challenge produces.
        var genius = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(null));

        var result = await NewQueue(cache, genius).SearchAsync(Query(), Constants.SourceNames.LrcLib, CancellationToken.None);

        Assert.Null(result.Best);
        var entry = cache.Get(result.Entry.Key);
        Assert.NotNull(entry);
        Assert.Contains(entry!.Attempts, a => a.Source == Constants.SourceNames.LrcLib && !a.Success);

        // Nothing is written anywhere for a challenge page: the cache holds no lyric text, so no
        // LRC timestamp marker can appear in the persisted file.
        Assert.DoesNotContain("[00:", File.ReadAllText(_cachePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualOverrideQueriesOnlyTheNamedSource()
    {
        var cache = new ResultCache(_cachePath);
        var lrcLib = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:01.00]x" }));
        var ovh = new FakeSource(Constants.SourceNames.LyricsOvh, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LyricsOvh, SyncedLrc = "[00:01.00]y" }));

        var result = await NewQueue(cache, lrcLib, ovh)
            .SearchAsync(Query(), Constants.SourceNames.LyricsOvh, CancellationToken.None);

        Assert.Equal(Constants.SourceNames.LyricsOvh, result.Best!.SourceName);
        Assert.Equal(0, lrcLib.Calls);
        Assert.Equal(1, ovh.Calls);
    }

    [Fact]
    public async Task ASourceRetryWindowDoesNotBlockTheOthers()
    {
        var cache = new ResultCache(_cachePath);

        // Warm-up sweep for track A: LRCLIB misses, which opens its own window for that track.
        var query = Query();
        var lrcLibMiss = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(null));
        await NewQueue(cache, lrcLibMiss).SearchAsync(query, null, CancellationToken.None);
        Assert.Equal(1, lrcLibMiss.Calls);

        // Track A is played again with a second source available. LRCLIB is inside its window,
        // so it must be skipped - but the other source has to be queried and can still win.
        var lrcLib = new FakeSource(Constants.SourceNames.LrcLib, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:01.00]x" }));
        var ovh = new FakeSource(Constants.SourceNames.LyricsOvh, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.LyricsOvh, SyncedLrc = "[00:02.00]y" }));

        var result = await NewQueue(cache, lrcLib, ovh)
            .SearchAsync(query, null, CancellationToken.None);

        Assert.NotNull(result.Best);
        Assert.Equal(Constants.SourceNames.LyricsOvh, result.Best!.SourceName);
        Assert.Equal(0, lrcLib.Calls);
        Assert.Equal(1, ovh.Calls);
    }

    [Fact]
    public async Task TheReasonOfAMissIsKeptForTheStatusView()
    {
        // A source that explains its miss must not be reduced to "no lyrics found": that is how
        // a rejected token looked exactly like a track Genius does not have.
        var cache = new ResultCache(_cachePath);
        var genius = new FakeSource(Constants.SourceNames.Genius, (_, _) => Task.FromResult<LyricResult?>(
            new LyricResult { SourceName = Constants.SourceNames.Genius, Message = "Genius API 401 (token rejected)" }));

        var result = await NewQueue(cache, genius)
            .SearchAsync(Query(), Constants.SourceNames.Genius, CancellationToken.None);

        Assert.Null(result.Best);
        Assert.Contains(
            result.Entry.Attempts,
            a => a.Source == Constants.SourceNames.Genius && a.Message == "Genius API 401 (token rejected)");
    }

    private sealed class FakeSource : ILyricSource
    {
        private readonly Func<TrackQuery, CancellationToken, Task<LyricResult?>> _find;

        public FakeSource(string name, Func<TrackQuery, CancellationToken, Task<LyricResult?>> find)
        {
            Name = name;
            _find = find;
        }

        public string Name { get; }

        public bool SupportsSynced => true;

        public bool IsEnabled => true;

        public int MinDelayMs => 0;

        public int Calls { get; private set; }

        public Task<LyricResult?> FindAsync(TrackQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return _find(query, cancellationToken);
        }
    }
}

public class TrackMatcherTests
{
    [Theory]
    [InlineData("Beyoncé", "Beyonce", true)]
    [InlineData("Rick Astley", "Rick Astley feat. Someone", true)]
    [InlineData("AC/DC", "ACDC", true)]
    [InlineData("Rick Astley", "New Order", false)]
    [InlineData("", "Anything", true)]
    [InlineData("Something", "", false)]
    public void ArtistMatching(string track, string result, bool expected)
    {
        Assert.Equal(expected, TrackMatcher.ArtistMatches(track, result));
    }

    [Theory]
    [InlineData("Blue Monday", "Blue Monday (Remastered)", true)]
    [InlineData("Yellow", "Yellow", true)]
    [InlineData("Yellow", "Fix You", false)]
    [InlineData("Song", "", false)]
    public void TitleMatching(string track, string result, bool expected)
    {
        Assert.Equal(expected, TrackMatcher.TitleMatches(track, result));
    }

    [Fact]
    public void NormalizeFoldsDiacriticsAndPunctuation()
    {
        Assert.Equal("zolc edward", TrackMatcher.Normalize("  Żółć Ędward  "));
        Assert.Equal("ac dc", TrackMatcher.Normalize("AC/DC"));
        Assert.Equal(string.Empty, TrackMatcher.Normalize(null));
    }
}