using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Configuration;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Sources;
using Jellyfin.Plugin.MultiLyrics.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Lyrics;

/// <summary>
/// Outcome of one queue run.
/// </summary>
public sealed class QueueResult
{
    /// <summary>Gets the winning result, or null when every source missed.</summary>
    public LyricResult? Best { get; init; }

    /// <summary>Gets the cache entry that was updated.</summary>
    public CacheEntry Entry { get; init; } = new();
}

/// <summary>
/// The multi-source priority queue.
/// </summary>
public interface ILyricsQueue
{
    /// <summary>
    /// Runs the queue for a track. When <paramref name="forceSource"/> is set, only that
    /// source is queried (manual override; bypasses enable flags and retry windows, but
    /// Genius still requires its acknowledgement + token).
    /// </summary>
    /// <param name="query">Track query.</param>
    /// <param name="forceSource">Source name or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The queue result.</returns>
    Task<QueueResult> SearchAsync(TrackQuery query, string? forceSource, CancellationToken cancellationToken);

    /// <summary>
    /// Gets all registered sources (any order).
    /// </summary>
    /// <returns>All sources.</returns>
    IReadOnlyList<ILyricSource> AllSources();
}

/// <summary>
/// Queries enabled lyric sources in configured priority order, records every attempt in the
/// result cache and applies the retry schedule on full misses.
/// </summary>
public sealed class LyricsSearchService : ILyricsQueue
{
    private static readonly string[] DefaultOrder =
    [
        Constants.SourceNames.LrcLib,
        Constants.SourceNames.LyricsOvh,
        Constants.SourceNames.Tekstowo,
        Constants.SourceNames.Genius
    ];

    private readonly IEnumerable<ILyricSource> _sources;
    private readonly IResultCache _cache;
    private readonly ILogger<LyricsSearchService> _logger;

    // Resolved per call, never in the constructor: the plugin instance (and therefore the
    // configuration) does not exist yet when the service is built at startup.
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Set once the reason Genius is excluded has been logged; the settings only change on a
    /// restart, so repeating it for every track would drown the log.
    /// </summary>
    private int _geniusExplained;

    /// <summary>
    /// Initializes a new instance of the <see cref="LyricsSearchService"/> class.
    /// </summary>
    /// <param name="sources">All registered sources.</param>
    /// <param name="cache">Result cache.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Configuration accessor; defaults to the live plugin configuration.</param>
    public LyricsSearchService(
        IEnumerable<ILyricSource> sources,
        IResultCache cache,
        ILogger<LyricsSearchService> logger,
        Func<PluginConfiguration>? config = null)
    {
        _sources = sources;
        _cache = cache;
        _logger = logger;
        _config = config ?? (() => MultiLyricsPlugin.Config);
    }

    /// <inheritdoc />
    public IReadOnlyList<ILyricSource> AllSources() => [.. _sources];

    /// <inheritdoc />
    public async Task<QueueResult> SearchAsync(
        TrackQuery query,
        string? forceSource,
        CancellationToken cancellationToken)
    {
        var entry = _cache.GetOrAdd(query);

        // A changed source configuration, or a new plugin version, clears the recorded retry
        // windows. Without this a source that was broken (or newly enabled) stays blocked by
        // misses recorded under the previous configuration, which looks like the setting is ignored.
        var settingsChanged = forceSource is null && _cache.SourcesSettingsChanged(SourceSettingsStamp());
        if (settingsChanged)
        {
            _logger.LogInformation(
                "Multi-Lyrics: source configuration changed; retry windows cleared, every source is eligible");
        }

        var ordered = ResolveOrder(forceSource);
        if (forceSource is null)
        {
            ExplainGeniusExclusionIfNeeded();
            _logger.LogDebug("Multi-Lyrics: sweep order for \"{Title}\": {Order}", query.Title, string.Join(",", ordered));
        }
        var first = true;
        LyricResult? best = null;

        // Default: a synced result beats a plain one no matter where the sources sit in the
        // order. Turning this off makes the priority order literal - the first source that has
        // any lyrics wins, which is the only way to actually see (or verify) Genius' text when
        // a synced source sits lower in the order.
        var preferSynced = _config().PreferSyncedLyrics;

        // Every source's verdict lands here and is logged once per sweep. Without it a sweep where
        // one source misses and another wins said nothing at all about the source that missed,
        // which made a misconfigured or blocked source look like it was never queried.
        var details = new List<string>();

        // A plain result does not end the sweep: it is remembered as a fallback while the
        // queue keeps looking for a synced result from a later source.
        LyricResult? plainFallback = null;

        foreach (var name in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = FindSource(name);
            if (source is null)
            {
                continue;
            }

            // A skip is possible for one source only: windows are per source and were just cleared
            // above when the configuration changed.
            if (forceSource is null && _cache.ShouldSkip(entry, name))
            {
                details.Add($"{name}: retry window");
                continue;
            }

            if (!first)
            {
                var delay = Math.Max(_config().RequestDelayMs, source.MinDelayMs);
                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            first = false;

            var attempt = new SourceAttempt { Source = name, TimestampUtc = DateTime.UtcNow };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await source.FindAsync(query, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                attempt.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                if (result is not null && result.Kind != LyricKind.None)
                {
                    attempt.Success = true;
                    attempt.Kind = result.Kind;
                    attempt.Message = result.Message;
                    _cache.RecordAttempt(entry, attempt);
                    details.Add($"{name}: {Describe(result)}");

                    if (!preferSynced)
                    {
                        // Literal order: the first source with any lyrics ends the sweep.
                        best = result;
                        break;
                    }

                    if (result.Kind is LyricKind.Synced or LyricKind.Instrumental)
                    {
                        // Synced short-circuits the queue; instrumental means there is
                        // nothing to fetch from any later source.
                        best = result;
                        break;
                    }

                    // Plain: keep it as a fallback and keep looking for synced.
                    plainFallback ??= result;
                    continue;
                }

                // Misses (or Genius SearchOnly metadata) are recorded as non-success attempts.
                attempt.Success = false;
                attempt.Kind = LyricKind.None;
                attempt.Transient = result?.Transient ?? false;
                attempt.Message = result?.Message ?? "no match";
                _cache.RecordAttempt(entry, attempt);
                details.Add($"{name}: {Clip(attempt.Message)}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Only caller cancellation aborts the sweep. A per-request HttpClient
                // timeout also throws TaskCanceledException, but with the caller token
                // untouched, so it is treated as a source failure below.
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                attempt.DurationMs = (int)stopwatch.ElapsedMilliseconds;
                attempt.Message = $"error: {ex.GetType().Name}";
                attempt.Transient = true;
                _logger.LogWarning(ex, "Lyric source {Source} failed for a track", name);
                _cache.RecordAttempt(entry, attempt);
                details.Add($"{name}: {attempt.Message}");
            }
        }

        // Plain-only sweep: use the remembered fallback.
        best ??= plainFallback;

        if (best is not null)
        {
            _cache.RecordWin(entry, best, aligned: false);
            _logger.LogInformation(
                "Multi-Lyrics: \"{Title}\" -> {Details}; winner: {Source} ({Kind})",
                query.Title,
                string.Join("; ", details),
                best.SourceName,
                best.Kind);
            return new QueueResult { Best = best, Entry = entry };
        }

        if (details.Count > 0)
        {
            _logger.LogInformation(
                "Multi-Lyrics: no lyrics for \"{Title}\" -> {Details}",
                query.Title,
                string.Join("; ", details));
            _cache.RecordMiss(entry);
        }

        return new QueueResult { Best = null, Entry = entry };
    }

    private static string Describe(LyricResult result)
    {
        var suffix = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" ({result.Message})";
        return result.Kind.ToString().ToLowerInvariant() + suffix;
    }

    /// <summary>
    /// Keeps one source's reason inside the single-line sweep summary. A source message is free
    /// text (an error type, a hit count, a song URL) and must not be able to flood the log.
    /// </summary>
    private static string Clip(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "no match";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "...";
    }

    /// <summary>
    /// Describes the effective source configuration. A change in any part of it (order, toggles,
    /// Genius mode or token presence) or in the plugin version resets the retry windows once.
    /// </summary>
    /// <returns>Stamp string stored next to the cache.</returns>
    private string SourceSettingsStamp()
    {
        var cfg = _config();
        var version = typeof(LyricsSearchService).Assembly.GetName().Version?.ToString() ?? "?";

        return string.Join(
            '|',
            "v" + version,
            cfg.SourceOrder,
            $"lrc={cfg.EnableLrcLib},ovh={cfg.EnableLyricsOvh},tekstowo={cfg.EnableTekstowo},genius={cfg.EnableGenius}",
            $"geniusMode={(cfg.EnableGenius && cfg.GeniusToSAcknowledged ? (int)GeniusMode.Scrape : (int)GeniusMode.Off)}",
            string.IsNullOrWhiteSpace(cfg.GeniusToken) ? "no-token" : "token",
            $"preferSynced={cfg.PreferSyncedLyrics}");
    }

    private List<string> ResolveOrder(string? forceSource)
    {
        if (forceSource is not null)
        {
            // Manual override: query exactly one source. Genius still guards itself via IsEnabled
            // (the acknowledgement), so an unacknowledged Genius cannot leak in.
            var match = FindSource(forceSource);
            return match is null ? [] : [forceSource];
        }

        var cfg = _config();
        var enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            // LRCLIB is the primary synced source and is permanently enabled.
            [Constants.SourceNames.LrcLib] = true,
            [Constants.SourceNames.LyricsOvh] = cfg.EnableLyricsOvh,
            [Constants.SourceNames.Tekstowo] = cfg.EnableTekstowo,

            // Same condition as GeniusSource.IsEnabled: the source only scrapes after the admin
            // acknowledged the Terms of Service. A client access token is optional.
            [Constants.SourceNames.Genius] = cfg.EnableGenius && cfg.GeniusToSAcknowledged
        };

        return [.. SourcePriority.Parse(cfg.SourceOrder, DefaultOrder, enabled)];
    }

    /// <summary>
    /// Explains, once, why Genius is in the configured order but not queried. A track that exists
    /// only on Genius otherwise fails with no hint about the missing acknowledgement.
    /// </summary>
    private void ExplainGeniusExclusionIfNeeded()
    {
        var cfg = _config();
        if (cfg.EnableGenius && cfg.GeniusToSAcknowledged)
        {
            return;
        }

        if (Interlocked.Exchange(ref _geniusExplained, 1) != 0)
        {
            return;
        }

        var reason = cfg.EnableGenius switch
        {
            false => "Genius is turned off in the settings",
            _ => "the Genius Terms-of-Service acknowledgement is missing"
        };

        _logger.LogInformation(
            "Multi-Lyrics: Genius is not queried ({Reason}). Tracks that only exist on Genius will not be found",
            reason);
    }

    private ILyricSource? FindSource(string name)
    {
        foreach (var source in _sources)
        {
            if (string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }
}
