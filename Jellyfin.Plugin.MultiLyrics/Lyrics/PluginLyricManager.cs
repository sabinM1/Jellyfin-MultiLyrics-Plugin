using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Lyrics;

/// <summary>
/// Decorator around Jellyfin's own lyric manager, registered by the plugin so that
/// GET /Audio/{id}/Lyrics returns lyrics for tracks Jellyfin has no lyric stream for.
/// <para>
/// Everything is ephemeral: a track is fetched on demand, kept in
/// <see cref="EphemeralLyricsStore"/> (memory only) and served from there until it leaves the
/// small LRU window. No sidecar file is written to the library or to the metadata folder, so a
/// library scan never picks up plugin output and nothing has to be cleaned up afterwards.
/// </para>
/// </summary>
public sealed class PluginLyricManager : ILyricManager
{
    /// <summary>
    /// How long the lyrics endpoint waits for the priority sweep before answering "none".
    /// Sized for the worst case of four sources, each bounded by its own client timeout
    /// (8 + 8 + 10 + 10 seconds plus the inter-source delays).
    /// </summary>
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Caps concurrent on-demand fetches so a client that prefetches a whole album cannot
    /// hammer the sources. Excess requests queue instead of failing.
    /// </summary>
    private static readonly SemaphoreSlim FetchGate = new(2, 2);

    private static readonly Task<IReadOnlyList<RemoteLyricInfoDto>> NoRemoteResults =
        Task.FromResult<IReadOnlyList<RemoteLyricInfoDto>>(Array.Empty<RemoteLyricInfoDto>());

    private readonly ILyricManager? _inner;
    private readonly Func<ILyricsQueue> _queue;
    private readonly Func<IResultCache> _cache;
    private readonly Func<EphemeralLyricsStore> _store;
    private readonly ILogger<PluginLyricManager> _logger;

    /// <summary>
    /// Service provider used to reach the host session manager once, on the first lyric request.
    /// Null when the manager was built from explicit services (tests). Resolved lazily on purpose:
    /// touching host services in the constructor is what used to deadlock the host's container.
    /// </summary>
    private readonly IServiceProvider? _services;

    private int _stopHookInstalled;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLyricManager"/> class.
    /// </summary>
    /// <param name="inner">The host manager this instance decorates, or null when unavailable.</param>
    /// <param name="queue">Priority queue used for on-demand fetches.</param>
    /// <param name="cache">Result cache (attempts and retry windows, no lyric text).</param>
    /// <param name="store">In-memory lyric store.</param>
    /// <param name="logger">Logger.</param>
    public PluginLyricManager(
        ILyricManager? inner,
        ILyricsQueue queue,
        IResultCache cache,
        EphemeralLyricsStore store,
        ILogger<PluginLyricManager> logger)
        : this(inner, () => queue, () => cache, () => store, logger, services: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLyricManager"/> class that resolves the
    /// plugin services on first use instead of in the constructor.
    /// <para>
    /// Jellyfin builds <c>ProviderManager</c> with this manager as a constructor dependency and the
    /// plugin's services are registered with it. Resolving them eagerly closed that cycle and
    /// blocked the host while it created the <c>IProviderManager</c> singleton.
    /// </para>
    /// </summary>
    /// <param name="inner">The host manager this instance decorates, or null when unavailable.</param>
    /// <param name="services">Service provider the plugin services are resolved from on demand.</param>
    /// <param name="logger">Logger.</param>
    public PluginLyricManager(
        ILyricManager? inner,
        IServiceProvider services,
        ILogger<PluginLyricManager> logger)
        : this(
            inner,
            () => services.GetRequiredService<ILyricsQueue>(),
            () => services.GetRequiredService<IResultCache>(),
            () => services.GetRequiredService<EphemeralLyricsStore>(),
            logger,
            services)
    {
    }

    private PluginLyricManager(
        ILyricManager? inner,
        Func<ILyricsQueue> queue,
        Func<IResultCache> cache,
        Func<EphemeralLyricsStore> store,
        ILogger<PluginLyricManager> logger,
        IServiceProvider? services)
    {
        _inner = inner;
        _queue = queue;
        _cache = cache;
        _store = store;
        _logger = logger;
        _services = services;
    }

    /// <inheritdoc />
    public event EventHandler<LyricDownloadFailureEventArgs> LyricDownloadFailure
    {
        add
        {
            if (_inner is not null)
            {
                _inner.LyricDownloadFailure += value;
            }
        }

        remove
        {
            if (_inner is not null)
            {
                _inner.LyricDownloadFailure -= value;
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteLyricInfoDto>> SearchLyricsAsync(Audio audio, bool isAutomated, CancellationToken cancellationToken)
        => _inner?.SearchLyricsAsync(audio, isAutomated, cancellationToken) ?? NoRemoteResults;

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteLyricInfoDto>> SearchLyricsAsync(LyricSearchRequest request, CancellationToken cancellationToken)
        => _inner?.SearchLyricsAsync(request, cancellationToken) ?? NoRemoteResults;

    /// <inheritdoc />
    public Task<LyricDto?> DownloadLyricsAsync(Audio audio, string lyricId, CancellationToken cancellationToken)
        => _inner?.DownloadLyricsAsync(audio, lyricId, cancellationToken) ?? Task.FromResult<LyricDto?>(null);

    /// <inheritdoc />
    public Task<LyricDto?> DownloadLyricsAsync(Audio audio, LibraryOptions libraryOptions, string lyricId, CancellationToken cancellationToken)
        => _inner?.DownloadLyricsAsync(audio, libraryOptions, lyricId, cancellationToken) ?? Task.FromResult<LyricDto?>(null);

    /// <inheritdoc />
    public Task<LyricDto?> SaveLyricAsync(Audio audio, string format, string lyrics)
        => _inner?.SaveLyricAsync(audio, format, lyrics) ?? Task.FromResult<LyricDto?>(null);

    /// <inheritdoc />
    public Task<LyricDto?> SaveLyricAsync(Audio audio, string format, System.IO.Stream lyrics)
        => _inner?.SaveLyricAsync(audio, format, lyrics) ?? Task.FromResult<LyricDto?>(null);

    /// <inheritdoc />
    public Task<LyricDto?> GetRemoteLyricsAsync(string id, CancellationToken cancellationToken)
        => _inner?.GetRemoteLyricsAsync(id, cancellationToken) ?? Task.FromResult<LyricDto?>(null);

    /// <inheritdoc />
    public Task DeleteLyricsAsync(Audio audio)
        => _inner?.DeleteLyricsAsync(audio) ?? Task.CompletedTask;

    /// <inheritdoc />
    public IReadOnlyList<LyricProviderInfo> GetSupportedProviders(BaseItem item)
        => _inner?.GetSupportedProviders(item) ?? Array.Empty<LyricProviderInfo>();

    /// <inheritdoc />
    public async Task<LyricDto?> GetLyricsAsync(Audio audio, CancellationToken cancellationToken)
    {
        if (audio is null)
        {
            return null;
        }

        InstallStopHook();

        // Lyrics already known to the host (sidecars from Jellyfin's own downloader, plugin
        // versions before 1.1, or the user's own files) win: they are the user's data.
        if (_inner is not null)
        {
            try
            {
                var existing = await _inner.GetLyricsAsync(audio, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Jellyfin keeps the lyric stream in its database after the file is deleted, so
                // opening it throws. That used to surface as a 500 on GET /Audio/{id}/Lyrics.
                // Fall through and serve fresh lyrics instead.
                _logger.LogWarning(
                    "Multi-Lyrics: the lyrics Jellyfin has for \"{Title}\" could not be read ({Reason}); fetching from the sources instead. Rescan the library to drop the stale lyric stream",
                    audio.Name,
                    ex is FileNotFoundException ? "the lyric file no longer exists" : ex.Message);
            }
        }

        try
        {
            // Fast path: still inside the in-memory window.
            if (audio.Id != Guid.Empty && TryServe(audio.Id, audio.Name, out var cached))
            {
                return cached;
            }

            return await FetchOnDemandAsync(audio, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The lyrics endpoint must degrade to "no lyrics", never to a 500.
            _logger.LogWarning(ex, "Multi-Lyrics: could not resolve lyrics for \"{Title}\"", audio.Name);
            return null;
        }
    }

    /// <summary>
    /// Serves a track from the in-memory store. Returns true when the track was found, even when
    /// the result is null (instrumental tracks are remembered so they are not re-fetched).
    /// </summary>
    /// <param name="key">Track key.</param>
    /// <param name="title">Track title, for the log line.</param>
    /// <param name="dto">Lyrics to return, null for an instrumental track.</param>
    /// <returns>True when the track is held in memory.</returns>
    private bool TryServe(Guid key, string? title, out LyricDto? dto)
    {
        dto = null;
        if (!_store().TryGet(key, out var lyric))
        {
            return false;
        }

        if (lyric.Kind == LyricKind.Instrumental)
        {
            _logger.LogDebug("Multi-Lyrics: \"{Title}\" is instrumental (cached in memory)", title);
            return true;
        }

        dto = LyricFiles.BuildDto(lyric.Kind, lyric.SyncedLrc ?? lyric.PlainText);
        if (dto is null)
        {
            return false;
        }

        _logger.LogInformation(
            "Multi-Lyrics: served {Kind} lyrics for \"{Title}\" from memory (source {Source})",
            lyric.Kind,
            title,
            lyric.SourceName ?? "unknown");
        return true;
    }

    /// <summary>
    /// Subscribes to the host's playback-stop signal the first time lyrics are requested.
    /// <para>
    /// Lyrics are meant to live for the track being played only. Dropping the store here means
    /// they are gone as soon as playback stops instead of lingering until the TTL expires; the
    /// library is never involved, so a restart clears everything as well.
    /// </para>
    /// </summary>
    private void InstallStopHook()
    {
        if (_services is null || Interlocked.Exchange(ref _stopHookInstalled, 1) != 0)
        {
            return;
        }

        try
        {
            if (_services.GetService<ISessionManager>() is { } sessions)
            {
                // The event's argument type differs between the Jellyfin lines this plugin
                // targets, so it is inferred rather than named.
                sessions.PlaybackStopped += (_, _) => DropLyrics();
                return;
            }
        }
        catch (Exception ex)
        {
            // A missing or unresolvable session manager is not an error: the store then relies on
            // the track-window eviction and the TTL, exactly as before.
            _logger.LogDebug(ex, "Multi-Lyrics: could not watch playback stops");
        }
    }

    private void DropLyrics()
    {
        try
        {
            _store().Clear();
            _logger.LogDebug("Multi-Lyrics: playback stopped; in-memory lyrics dropped");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Multi-Lyrics: could not drop lyrics after playback stopped");
        }
    }

    private async Task<LyricDto?> FetchOnDemandAsync(Audio audio, CancellationToken cancellationToken)
    {
        if (!MultiLyricsPlugin.Config.FetchOnDemand)
        {
            _logger.LogDebug("Multi-Lyrics: no lyrics for \"{Title}\"; fetch on demand is off", audio.Name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(audio.Path) || string.IsNullOrWhiteSpace(audio.Name))
        {
            return null;
        }

        var query = new TrackQuery
        {
            ItemId = audio.Id,
            Artist = audio.Artists?.FirstOrDefault() ?? audio.AlbumArtists?.FirstOrDefault() ?? string.Empty,
            Album = audio.Album,
            Title = audio.Name,
            DurationSec = audio.RunTimeTicks is > 0 ? audio.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : null,
            AudioPath = audio.Path
        };

        var entry = _cache().GetOrAdd(query);

        // A fingerprint-keyed track (no item id) can only be looked up after the entry exists.
        if (audio.Id == Guid.Empty && TryServe(entry.Key, audio.Name, out var fingerprintHit))
        {
            return fingerprintHit;
        }

        // No entry-level retry gate here: the lyric text is never persisted, so a track that a
        // source solved before must be fetched again. The queue applies the per-source windows.

        await FetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FetchBudget);

            QueueResult outcome;
            try
            {
                outcome = await _queue().SearchAsync(query, forceSource: null, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Nothing is known for this track, so do not keep the previous track's lyrics.
                _store().Clear();
                _logger.LogWarning(
                    "Multi-Lyrics: on-demand fetch for \"{Title}\" timed out after {Seconds}s",
                    audio.Name,
                    FetchBudget.TotalSeconds);
                return null;
            }

            var best = outcome.Best;
            if (best is null)
            {
                // A miss must also drop what the previous track left behind: lyrics are only
                // meant to live for the track currently being resolved.
                _store().Clear();
                _logger.LogInformation("Multi-Lyrics: on-demand fetch found nothing for \"{Title}\"", audio.Name);
                return null;
            }

            if (best.Kind == LyricKind.Instrumental)
            {
                _store().Set(entry.Key, new CachedLyric(LyricKind.Instrumental, null, null, best.SourceName));
                _cache().RecordWin(outcome.Entry, best, aligned: false);
                _logger.LogInformation("Multi-Lyrics: \"{Title}\" is instrumental - no lyrics to serve", audio.Name);
                return null;
            }

            _store().Set(entry.Key, new CachedLyric(best.Kind, best.SyncedLrc, best.PlainText, best.SourceName));
            _cache().RecordWin(outcome.Entry, best, aligned: false);
            _logger.LogInformation(
                "Multi-Lyrics: on-demand {Kind} via {Source} for \"{Title}\" (kept in memory)",
                best.Kind,
                best.SourceName,
                audio.Name);

            return LyricFiles.BuildDto(best.Kind, best.BodyForSidecar());
        }
        finally
        {
            FetchGate.Release();
        }
    }
}
