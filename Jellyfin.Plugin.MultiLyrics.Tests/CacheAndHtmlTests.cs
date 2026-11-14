using System;
using System.IO;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Sources;
using Jellyfin.Plugin.MultiLyrics.Storage;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

public class CacheRetryTests : IDisposable
{
    private readonly string _path;

    public CacheRetryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "mlcache-" + Guid.NewGuid().ToString("N"), "cache.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, true);
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

    [Fact]
    public void MissesEscalateThePerSourceRetryWindow()
    {
        var cache = new ResultCache(_path);
        var entry = cache.GetOrAdd(Query());

        Assert.Equal(0, entry.AttemptCount);
        Assert.Null(entry.RetryAfterUtc);

        cache.RecordAttempt(entry, Failed(Constants.SourceNames.LrcLib));
        Assert.True(entry.RetryAfterBySource[Constants.SourceNames.LrcLib] > DateTime.UtcNow.AddDays(0.5));
        Assert.True(entry.RetryAfterBySource[Constants.SourceNames.LrcLib] <= DateTime.UtcNow.AddDays(1.1));

        cache.RecordAttempt(entry, Failed(Constants.SourceNames.LrcLib));
        Assert.Equal(2, entry.MissCountBySource[Constants.SourceNames.LrcLib]);
        Assert.True(entry.RetryAfterBySource[Constants.SourceNames.LrcLib] > DateTime.UtcNow.AddDays(2.5));

        cache.RecordAttempt(entry, Failed(Constants.SourceNames.LrcLib));
        Assert.True(entry.RetryAfterBySource[Constants.SourceNames.LrcLib] > DateTime.UtcNow.AddDays(6.5));

        cache.RecordAttempt(entry, Failed(Constants.SourceNames.LrcLib));
        Assert.True(entry.RetryAfterBySource[Constants.SourceNames.LrcLib] > DateTime.UtcNow.AddDays(29.5));

        // 5th miss still capped at 30 days.
        cache.RecordAttempt(entry, Failed(Constants.SourceNames.LrcLib));
        Assert.True(entry.RetryAfterBySource[Constants.SourceNames.LrcLib] <= DateTime.UtcNow.AddDays(30.1));
    }

    [Fact]
    public void ATransientFailureRetriesWithinMinutes()
    {
        var cache = new ResultCache(_path);
        var entry = cache.GetOrAdd(Query());

        var attempt = Failed(Constants.SourceNames.Genius);
        attempt.Transient = true;
        cache.RecordAttempt(entry, attempt);

        var due = entry.RetryAfterBySource[Constants.SourceNames.Genius];
        Assert.True(due > DateTime.UtcNow.AddMinutes(5));
        Assert.True(due <= DateTime.UtcNow.Add(Constants.TransientRetryDelay).AddMinutes(1));
    }

    [Fact]
    public void ShouldSkipIsPerSource()
    {
        var cache = new ResultCache(_path);
        var entry = cache.GetOrAdd(Query());

        cache.RecordAttempt(entry, Failed(Constants.SourceNames.LrcLib));

        Assert.True(cache.ShouldSkip(entry, Constants.SourceNames.LrcLib));

        // One source missing a track must never stop the others from being queried.
        Assert.False(cache.ShouldSkip(entry, Constants.SourceNames.Genius));

        // After the window expires the source may run again.
        entry.RetryAfterBySource[Constants.SourceNames.LrcLib] = DateTime.UtcNow.AddSeconds(-1);
        Assert.False(cache.ShouldSkip(entry, Constants.SourceNames.LrcLib));
    }

    [Fact]
    public void ARecordedWinDoesNotBlockAFutureFetch()
    {
        // Lyric text is ephemeral: "this track had lyrics once" must not survive the in-memory
        // window, otherwise every track solved in an earlier session is never fetched again.
        var cache = new ResultCache(_path);
        var entry = cache.GetOrAdd(Query());
        cache.RecordWin(
            entry,
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:01.00]line" },
            aligned: false);

        Assert.Equal(LyricKind.Synced, entry.Kind);
        Assert.False(cache.ShouldSkip(entry, Constants.SourceNames.LrcLib));
        Assert.False(cache.ShouldSkip(entry, Constants.SourceNames.Genius));
    }

    [Fact]
    public void RetryWindowsSurviveANewInstance()
    {
        var first = new ResultCache(_path);
        var entry = first.GetOrAdd(Query());
        first.RecordAttempt(entry, Failed(Constants.SourceNames.Genius));

        var second = new ResultCache(_path);
        var reloaded = second.Get(entry.Key);

        Assert.NotNull(reloaded);
        Assert.True(second.ShouldSkip(reloaded!, Constants.SourceNames.Genius));
        Assert.False(second.ShouldSkip(reloaded!, Constants.SourceNames.LrcLib));
    }

    [Fact]
    public void ChangingTheSourceConfigurationClearsTheWindows()
    {
        var cache = new ResultCache(_path);
        var entry = cache.GetOrAdd(Query());
        cache.RecordAttempt(entry, Failed(Constants.SourceNames.Genius));
        Assert.True(cache.ShouldSkip(entry, Constants.SourceNames.Genius));

        Assert.True(cache.SourcesSettingsChanged("v2|order|toggles"));

        // Newly enabled source: the miss recorded under the previous configuration is gone.
        Assert.False(cache.ShouldSkip(entry, Constants.SourceNames.Genius));
        Assert.Empty(entry.RetryAfterBySource);
        Assert.Equal(0, entry.AttemptCount);
    }

    private static SourceAttempt Failed(string source) => new()
    {
        Source = source,
        Success = false,
        Kind = LyricKind.None,
        Message = "no match"
    };

    [Fact]
    public void WinIsCachedAndPersistedAcrossInstances()
    {
        var first = new ResultCache(_path);
        var entry = first.GetOrAdd(Query());
        var win = new LyricResult
        {
            SourceName = Constants.SourceNames.LrcLib,
            SyncedLrc = "[00:10.00]Synthetic line",
            PlainText = null
        };
        first.RecordWin(entry, win, aligned: false);

        // New instance reads the same file.
        var second = new ResultCache(_path);
        var fetched = second.Get(entry.Key);
        Assert.NotNull(fetched);
        Assert.Equal(LyricKind.Synced, fetched!.Kind);
        Assert.Equal(Constants.SourceNames.LrcLib, fetched.WinningSource);
        Assert.False(fetched.Aligned);

        // Lyric text is never persisted: the cache only remembers what was found.
        Assert.DoesNotContain("Synthetic line", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintEntriesAreDeterministic()
    {
        var cache = new ResultCache(_path);
        var q = Query();
        q.ItemId = Guid.Empty;

        var e1 = cache.GetOrAdd(q);
        var e2 = cache.GetOrAdd(new TrackQuery
        {
            ItemId = Guid.Empty,
            Artist = "Synthetic Artist",
            Album = "Synthetic Album",
            Title = "Synthetic Song",
            DurationSec = 200
        });

        Assert.Equal(e1.Key, e2.Key);
    }

    [Fact]
    public void MetadataChangeResetsAttempts()
    {
        var cache = new ResultCache(_path);
        var q = Query();
        var entry = cache.GetOrAdd(q);
        cache.RecordMiss(entry);
        cache.RecordMiss(entry);
        Assert.Equal(2, entry.AttemptCount);

        q.Title = "Different Song";
        var updated = cache.GetOrAdd(q);
        Assert.Equal(0, updated.AttemptCount);
        Assert.Null(updated.RetryAfterUtc);
    }

    [Fact]
    public void CorruptCacheFileFallsBackToEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json ");

        var cache = new ResultCache(_path);
        var entry = cache.GetOrAdd(Query());
        Assert.NotNull(entry);

        // Backup of the corrupt file was made.
        Assert.Contains(".corrupt-", string.Join(',', Directory.GetFiles(Path.GetDirectoryName(_path)!)));
    }
}

public class HtmlExtractionTests
{
    // Synthetic fixtures with fictional text only - no real lyric text is bundled in this repo.

    private const string GeniusPageHtml = """
        <html><body>
        <div data-lyrics-container="true">Alpha line<br>Beta line<br><br>Gamma line</div>
        <div data-lyrics-container="true">Second block<br>More</div>
        </body></html>
        """;

    private const string TekstowoPageHtml = """
        <html><body>
        <div class="song-text"><div class="inner-text">Erste Zeile<br>Zweite Zeile</div></div>
        </body></html>
        """;

    private const string CloudflareHtml = """
        <html><head><title>Just a moment...</title></head>
        <body><div id="cf-chl-widget-123"></div></body></html>
        """;

    // Mirrors the live page: the contributor banner sits inside the first lyrics container but is
    // marked for exclusion. Reading it verbatim prefixed every payload with the credit block.
    private const string GeniusPageWithHeaderHtml = """
        <html><body>
        <div data-lyrics-container="true">
          <div data-exclude-from-selection="true" class="LyricsHeader__Container">
            <button><span>4 Contributors</span></button>
            <h1>Synthetic Song Lyrics</h1>
          </div>
          Alpha line<br>Beta line
        </div>
        <div data-lyrics-container="true"></div>
        <div data-lyrics-container="true">Gamma line<br>Delta line</div>
        </body></html>
        """;

    [Fact]
    public void GeniusExtractorSkipsTheHeaderBanner()
    {
        var text = GeniusSource.ExtractLyrics(GeniusPageWithHeaderHtml);

        Assert.NotNull(text);
        Assert.DoesNotContain("Contributors", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic Song Lyrics", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Alpha line", "Beta line", "Gamma line", "Delta line" }, LrcParser.PlainLines(text!));
    }

    [Fact]
    public void GeniusExtractorReadsAllContainers()
    {
        var text = GeniusSource.ExtractLyrics(GeniusPageHtml);
        Assert.NotNull(text);
        Assert.Contains("Alpha line", text);
        Assert.Contains("Second block", text);
        Assert.Equal(5, LrcParser.PlainLines(text!).Count);
    }

    [Fact]
    public void GeniusExtractorReturnsNullWithoutContainers()
    {
        Assert.Null(GeniusSource.ExtractLyrics("<html><body><p>no lyrics here</p></body></html>"));
    }

    [Fact]
    public void CloudflareChallengeIsDetected()
    {
        Assert.True(GeniusSource.IsCloudflareChallenge(403, "", null));
        Assert.True(GeniusSource.IsCloudflareChallenge(503, "anything", null));
        Assert.True(GeniusSource.IsCloudflareChallenge(200, CloudflareHtml, null));
        Assert.False(GeniusSource.IsCloudflareChallenge(200, "<html><body>normal page</body></html>", null));
    }

    [Fact]
    public void TekstowoExtractorReadsInnerTextView()
    {
        var text = TekstowoSource.ExtractLyrics(TekstowoPageHtml);
        Assert.NotNull(text);
        Assert.Equal(new[] { "Erste Zeile", "Zweite Zeile" }, LrcParser.PlainLines(text!));
    }

    [Theory]
    [InlineData("Rick Astley", "rick-astley")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("P!nk", "p-nk")]
    [InlineData("AC/DC", "ac-dc")]
    [InlineData("  Multiple   Spaces  ", "multiple-spaces")]
    [InlineData("Żółć Ędward", "zolc-edward")]
    public void TekstowoSlugsAreCanonical(string input, string expected)
    {
        Assert.Equal(expected, TekstowoSource.Slug(input));
    }
}
