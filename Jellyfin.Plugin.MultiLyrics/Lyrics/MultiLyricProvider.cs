using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Storage;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Lyrics;

/// <summary>
/// Jellyfin lyric provider integration. Lets Jellyfin's own remote-lyrics UI (item lyrics
/// tab) reach the same priority queue used by the plugin. Lyric text is served from the
/// in-memory store only; nothing is written to the library or the metadata folder.
/// </summary>
public sealed class MultiLyricProvider : ILyricProvider
{
    private const string IdPrefixSynced = "k:synced";
    private const string IdPrefixPlain = "k:plain";

    private readonly ILyricsQueue _queue;
    private readonly EphemeralLyricsStore _lyrics;
    private readonly ILogger<MultiLyricProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiLyricProvider"/> class.
    /// </summary>
    /// <param name="queue">Priority queue.</param>
    /// <param name="lyrics">In-memory lyric store.</param>
    /// <param name="logger">Logger.</param>
    public MultiLyricProvider(
        ILyricsQueue queue,
        EphemeralLyricsStore lyrics,
        ILogger<MultiLyricProvider> logger)
    {
        _queue = queue;
        _lyrics = lyrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Constants.PluginName;

    /// <summary>
    /// Builds the remote search id for a cached/queued result. Must not contain '_':
    /// Jellyfin splits the surfaced id at the first underscore to find the provider.
    /// </summary>
    /// <param name="source">Source name.</param>
    /// <param name="entryKey">Cache entry key.</param>
    /// <param name="kind">Result kind.</param>
    /// <returns>Id like "src:LrcLib|item:...|k:synced".</returns>
    public static string BuildRemoteId(string source, Guid entryKey, LyricKind kind)
        => $"src:{source}|key:{entryKey:N}|{(kind == LyricKind.Synced ? IdPrefixSynced : IdPrefixPlain)}";

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteLyricInfo>> SearchAsync(LyricSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MultiLyricsPlugin.Config.FetchOnDemand)
        {
            // Jellyfin calls providers from the remote-lyrics search and its own download
            // task; silent here was indistinguishable from "provider never invoked".
            _logger.LogDebug("Multi-Lyrics: on-demand search skipped (fetch on demand is off)");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        var query = ToTrackQuery(request);
        if (string.IsNullOrWhiteSpace(query.Title))
        {
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        _logger.LogDebug(
            "Multi-Lyrics: search request for \"{Title}\" by {Artist} (automated: {Automated})",
            query.Title,
            query.Artist,
            request.IsAutomated);

        try
        {
            // Manual remote search (user opened the lyrics tab): fresh search across all
            // enabled sources so the admin can pick. Automated path: use cache + queue.
            var allSources = !request.IsAutomated;
            var collected = new List<RemoteLyricInfo>();

            if (allSources)
            {
                // Manual remote search in the Jellyfin UI: one fresh result per enabled source.
                foreach (var source in _queue.AllSources())
                {
                    if (!source.IsEnabled)
                    {
                        continue;
                    }

                    var forced = await _queue.SearchAsync(query, source.Name, cancellationToken)
                        .ConfigureAwait(false);
                    if (forced.Best is not null)
                    {
                        collected.AddRange(ToRemoteLyricInfos(forced.Best, forced.Entry));
                    }
                }
            }
            else
            {
                var result = await _queue.SearchAsync(query, null, cancellationToken).ConfigureAwait(false);
                if (result.Best is not null)
                {
                    collected.AddRange(ToRemoteLyricInfos(result.Best, result.Entry));
                }
            }

            if (collected.Count > 0)
            {
                _logger.LogInformation(
                    "Multi-Lyrics: returned {Count} remote lyric result(s) for \"{Title}\"",
                    collected.Count,
                    query.Title);
            }
            else
            {
                _logger.LogDebug("Multi-Lyrics: no remote lyrics found for \"{Title}\"", query.Title);
            }

            return collected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Multi-Lyrics search failed");
            return Enumerable.Empty<RemoteLyricInfo>();
        }
    }

    /// <inheritdoc />
    public Task<LyricResponse?> GetLyricsAsync(string id, CancellationToken cancellationToken)
    {
        // id is our BuildRemoteId output; Jellyfin already stripped its "<md5>_" prefix.
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<LyricResponse?>(null);
        }

        var source = ParseTag(id, "src:");
        var key = ParseTag(id, "key:");
        var kindTag = id.Contains(IdPrefixSynced, StringComparison.Ordinal) ? LyricKind.Synced : LyricKind.Plain;

        if (source is null || !Guid.TryParseExact(key, "N", out var entryKey))
        {
            _logger.LogDebug("Unrecognized Multi-Lyrics lyric id format");
            return Task.FromResult<LyricResponse?>(null);
        }

        // Lyric text is never persisted, so an id from an earlier session (or from a track
        // outside the in-memory window) is refused rather than guessed.
        if (!_lyrics.TryGet(entryKey, out var cached))
        {
            return Task.FromResult<LyricResponse?>(null);
        }

        var payload = kindTag == LyricKind.Synced ? cached.SyncedLrc : cached.PlainText;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Task.FromResult<LyricResponse?>(null);
        }

        var format = kindTag == LyricKind.Synced && LrcParser.LooksSynced(payload) ? "lrc" : "txt";
        LyricResponse response = new()
        {
            Format = format,
            Stream = new MemoryStream(Encoding.UTF8.GetBytes(payload))
        };
        return Task.FromResult<LyricResponse?>(response);
    }

    private static string? ParseTag(string id, string prefix)
    {
        var idx = id.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var rest = id[(idx + prefix.Length)..];
        var end = rest.IndexOf('|', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    private static TrackQuery ToTrackQuery(LyricSearchRequest request)
    {
        return new TrackQuery
        {
            ItemId = Guid.Empty,
            Artist = request.ArtistNames?.FirstOrDefault() ?? request.AlbumArtistsNames?.FirstOrDefault() ?? string.Empty,
            Album = request.AlbumName,
            Title = request.SongName ?? string.Empty,
            DurationSec = request.Duration is > 0 ? request.Duration.Value / (double)TimeSpan.TicksPerSecond : null,
            AudioPath = request.MediaPath,
            Language = null
        };
    }

    private static IEnumerable<RemoteLyricInfo> ToRemoteLyricInfos(LyricResult result, CacheEntry entry)
    {
        if (result.Kind == LyricKind.Synced && !string.IsNullOrWhiteSpace(result.SyncedLrc))
        {
            yield return new RemoteLyricInfo
            {
                Id = BuildRemoteId(result.SourceName, entry.Key, LyricKind.Synced),
                ProviderName = Constants.PluginName,
                Metadata = BuildMetadata(entry, isSynced: true),
                Lyrics = new LyricResponse
                {
                    Format = "lrc",
                    Stream = new MemoryStream(Encoding.UTF8.GetBytes(result.SyncedLrc))
                }
            };
        }

        if (result.Kind == LyricKind.Plain && !string.IsNullOrWhiteSpace(result.PlainText))
        {
            yield return new RemoteLyricInfo
            {
                Id = BuildRemoteId(result.SourceName, entry.Key, LyricKind.Plain),
                ProviderName = Constants.PluginName,
                Metadata = BuildMetadata(entry, isSynced: false),
                Lyrics = new LyricResponse
                {
                    Format = "txt",
                    Stream = new MemoryStream(Encoding.UTF8.GetBytes(result.PlainText))
                }
            };
        }
    }

    private static LyricMetadata BuildMetadata(CacheEntry entry, bool isSynced)
    {
        return new LyricMetadata
        {
            Artist = entry.Artist,
            Title = entry.Title,
            By = Constants.PluginName,
            IsSynced = isSynced
        };
    }
}
