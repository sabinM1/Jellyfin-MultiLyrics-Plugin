using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Lyrics;
using Jellyfin.Plugin.MultiLyrics.Sources;
using Jellyfin.Plugin.MultiLyrics.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

/// <summary>
/// Covers the ephemeral model: lyrics are fetched on demand, held in memory and never written
/// to the library or metadata folder.
/// </summary>
public sealed class EphemeralLyricsTests : IDisposable
{
    private readonly string _dir;

    public EphemeralLyricsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mltest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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

    [Fact]
    public void StoreDropsTheLeastRecentlyUsedTrack()
    {
        var store = new EphemeralLyricsStore();
        for (var i = 0; i < EphemeralLyricsStore.Capacity + 1; i++)
        {
            store.Set(Key(i), new CachedLyric(LyricKind.Plain, null, "line", "test"));
        }

        Assert.Equal(EphemeralLyricsStore.Capacity, store.Count);
        Assert.False(store.Contains(Key(0)));
        Assert.True(store.Contains(Key(EphemeralLyricsStore.Capacity)));
    }

    [Fact]
    public void StoreKeepsRecentlyUsedEntriesInsideTheWindow()
    {
        var store = new EphemeralLyricsStore();
        store.Set(Key(1), new CachedLyric(LyricKind.Plain, null, "line", "test"));
        Assert.True(store.TryGet(Key(1), out _)); // promote to most recently used

        for (var i = 2; i <= EphemeralLyricsStore.Capacity; i++)
        {
            store.Set(Key(i), new CachedLyric(LyricKind.Plain, null, "line", "test"));
        }

        Assert.True(store.Contains(Key(1)));
    }

    [Fact]
    public void StoreExpiresEntriesAfterTheTtl()
    {
        var store = new EphemeralLyricsStore(TimeSpan.FromMilliseconds(1));
        store.Set(Key(1), new CachedLyric(LyricKind.Plain, null, "line", "test"));
        Thread.Sleep(30);

        Assert.False(store.Contains(Key(1)));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task FetchIsStoredInMemoryAndServedWithoutAnotherQuery()
    {
        var audio = NewAudio("song.flac");
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var store = new EphemeralLyricsStore();
        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]synthetic line" }
        };

        var manager = NewManager(null, queue, cache, store);

        var first = await manager.GetLyricsAsync(audio, CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(first!.Lyrics.Count > 0);
        Assert.Equal(1, queue.Calls);

        var second = await manager.GetLyricsAsync(audio, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(1, queue.Calls); // served from memory, no second network sweep
        Assert.Empty(Directory.GetFiles(_dir, "*.lrc", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_dir, "*.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstrumentalTrackIsRememberedAndServesNothing()
    {
        var audio = NewAudio("instrumental.flac");
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var store = new EphemeralLyricsStore();
        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, IsInstrumental = true }
        };

        var manager = NewManager(null, queue, cache, store);

        Assert.Null(await manager.GetLyricsAsync(audio, CancellationToken.None));
        Assert.Null(await manager.GetLyricsAsync(audio, CancellationToken.None));

        // The marker stops a second sweep for the same track.
        Assert.Equal(1, queue.Calls);
        Assert.True(store.Contains(audio.Id));
    }

    [Fact]
    public async Task AFullMissDoesNotBlockTheNextPlayback()
    {
        // Lyrics are ephemeral, so the next playback has to be fetched again. The per-source
        // retry windows live in the queue; the manager must not short-circuit on them.
        var audio = NewAudio("retry.flac");
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var store = new EphemeralLyricsStore();
        var entry = cache.GetOrAdd(new TrackQuery { ItemId = audio.Id, Title = audio.Name });
        cache.RecordMiss(entry);

        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]found later" }
        };

        var dto = await NewManager(null, queue, cache, store).GetLyricsAsync(audio, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(1, queue.Calls);
    }

    [Fact]
    public async Task ATrackSolvedEarlierIsFetchedAgainOnceItsTextLeftMemory()
    {
        // Regression: the cache remembers the win, the text is gone. The old code treated the
        // remembered win as "already have lyrics" and skipped every source forever.
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var first = NewAudio("one.flac");
        var store = new EphemeralLyricsStore();
        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]first" }
        };
        var manager = NewManager(null, queue, cache, store);

        Assert.NotNull(await manager.GetLyricsAsync(first, CancellationToken.None));

        // Another track evicts the payload, then the first track is played again.
        queue.Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]second" };
        Assert.NotNull(await manager.GetLyricsAsync(NewAudio("two.flac"), CancellationToken.None));
        Assert.False(store.Contains(first.Id));

        queue.Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]first again" };
        Assert.NotNull(await manager.GetLyricsAsync(first, CancellationToken.None));
        Assert.Equal(3, queue.Calls);
    }

    [Fact]
    public async Task HostLyricsWinOverTheQueue()
    {
        var audio = NewAudio("host.flac");
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var store = new EphemeralLyricsStore();
        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]queue line" }
        };

        var inner = new HostFakeManager
        {
            Current = new LyricDto
            {
                Metadata = new LyricMetadata { IsSynced = false },
                Lyrics = [new LyricLine("from the host")]
            }
        };

        var dto = await NewManager(inner, queue, cache, store).GetLyricsAsync(audio, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal("from the host", dto!.Lyrics[0].Text);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public void PersistentCacheNeverStoresLyricText()
    {
        var path = Path.Combine(_dir, "cache.json");
        var cache = new ResultCache(path);
        var entry = cache.GetOrAdd(new TrackQuery { ItemId = Guid.NewGuid(), Title = "Synthetic Song" });

        cache.RecordWin(
            entry,
            new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:01.00]SECRET SYNTHETIC LINE" },
            aligned: false);

        Assert.Equal(LyricKind.Synced, cache.Get(entry.Key)!.Kind);

        var onDisk = File.ReadAllText(path);
        Assert.DoesNotContain("SECRET SYNTHETIC LINE", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderServesHeldPayloadAndRefusesEvictedOnes()
    {
        var key = Guid.NewGuid();
        var store = new EphemeralLyricsStore();
        var provider = new MultiLyricProvider(new FakeQueue(), store, NullLogger<MultiLyricProvider>.Instance);

        var id = MultiLyricProvider.BuildRemoteId(Constants.SourceNames.LrcLib, key, LyricKind.Synced);
        Assert.Null(await provider.GetLyricsAsync(id, CancellationToken.None));

        store.Set(key, new CachedLyric(LyricKind.Synced, "[00:01.00]held line", null, Constants.SourceNames.LrcLib));
        var response = await provider.GetLyricsAsync(id, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("lrc", response!.Format);
    }

    [Fact]
    public async Task NextTrackReplacesThePreviousLyrics()
    {
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var store = new EphemeralLyricsStore();
        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]first track" }
        };

        var manager = NewManager(null, queue, cache, store);

        var first = NewAudio("one.flac");
        Assert.NotNull(await manager.GetLyricsAsync(first, CancellationToken.None));
        Assert.True(store.Contains(first.Id));

        queue.Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]second track" };
        var second = NewAudio("two.flac");
        Assert.NotNull(await manager.GetLyricsAsync(second, CancellationToken.None));

        Assert.False(store.Contains(first.Id));
        Assert.True(store.Contains(second.Id));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task MissOnTheNextTrackDropsThePreviousLyrics()
    {
        var cache = new ResultCache(Path.Combine(_dir, "cache.json"));
        var store = new EphemeralLyricsStore();
        var queue = new FakeQueue
        {
            Result = new LyricResult { SourceName = Constants.SourceNames.LrcLib, SyncedLrc = "[00:06.00]first track" }
        };

        var manager = NewManager(null, queue, cache, store);

        var first = NewAudio("one.flac");
        Assert.NotNull(await manager.GetLyricsAsync(first, CancellationToken.None));

        // The next track has no lyrics: nothing from the previous track may survive it.
        queue.Result = null;
        var second = NewAudio("two.flac");
        Assert.Null(await manager.GetLyricsAsync(second, CancellationToken.None));

        Assert.Equal(0, store.Count);
        Assert.False(store.Contains(first.Id));
    }

    private static Guid Key(int n) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)n);

    private static Audio NewAudio(string fileName)
        => new() { Path = Path.Combine(Path.GetTempPath(), "mlmedia", fileName), Name = "Synthetic Song", Id = Guid.NewGuid() };

    private static PluginLyricManager NewManager(ILyricManager? inner, ILyricsQueue queue, IResultCache cache, EphemeralLyricsStore store)
        => new(inner, queue, cache, store, NullLogger<PluginLyricManager>.Instance);

    private sealed class FakeQueue : ILyricsQueue
    {
        public int Calls { get; private set; }

        public LyricResult? Result { get; set; }

        public IReadOnlyList<ILyricSource> AllSources() => [];

        public Task<QueueResult> SearchAsync(TrackQuery query, string? forceSource, CancellationToken cancellationToken)
        {
            Calls++;
            var entry = new CacheEntry { Key = query.ItemId };
            return Task.FromResult(new QueueResult { Best = Result, Entry = entry });
        }
    }

    private sealed class HostFakeManager : ILyricManager
    {
        public event EventHandler<LyricDownloadFailureEventArgs>? LyricDownloadFailure
        {
            add { }
            remove { }
        }

        public LyricDto? Current { get; set; }

        public Task<LyricDto?> GetLyricsAsync(Audio audio, CancellationToken cancellationToken)
            => Task.FromResult(Current);

        public Task<IReadOnlyList<RemoteLyricInfoDto>> SearchLyricsAsync(Audio audio, bool isAutomated, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteLyricInfoDto>>([]);

        public Task<IReadOnlyList<RemoteLyricInfoDto>> SearchLyricsAsync(LyricSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteLyricInfoDto>>([]);

        public Task<LyricDto?> DownloadLyricsAsync(Audio audio, string lyricId, CancellationToken cancellationToken)
            => Task.FromResult<LyricDto?>(null);

        public Task<LyricDto?> DownloadLyricsAsync(Audio audio, LibraryOptions libraryOptions, string lyricId, CancellationToken cancellationToken)
            => Task.FromResult<LyricDto?>(null);

        public Task<LyricDto?> SaveLyricAsync(Audio audio, string format, string lyrics)
            => Task.FromResult<LyricDto?>(null);

        public Task<LyricDto?> SaveLyricAsync(Audio audio, string format, Stream lyrics)
            => Task.FromResult<LyricDto?>(null);

        public Task<LyricDto?> GetRemoteLyricsAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult<LyricDto?>(null);

        public Task DeleteLyricsAsync(Audio audio) => Task.CompletedTask;

        public IReadOnlyList<LyricProviderInfo> GetSupportedProviders(BaseItem item) => [];
    }
}
