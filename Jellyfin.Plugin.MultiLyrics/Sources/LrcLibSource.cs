using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Sources;

/// <summary>
/// LRCLIB (lrclib.net) - synced + plain lyrics, no key required. Primary source.
/// Verified live: GET /api/get returns a single best match (404 JSON body on miss);
/// GET /api/search returns an array; fields: id, trackName, artistName, albumName,
/// duration (seconds), instrumental, hasWordSync, plainLyrics, syncedLyrics.
/// </summary>
public sealed class LrcLibSource : ILyricSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LrcLibSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LrcLibSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public LrcLibSource(IHttpClientFactory httpClientFactory, ILogger<LrcLibSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Constants.SourceNames.LrcLib;

    /// <inheritdoc />
    public bool SupportsSynced => true;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public int MinDelayMs => 500;

    private static bool StrictSearch => MultiLyricsPlugin.Config.StrictSearch;

    private static int ToleranceSeconds => MultiLyricsPlugin.Config.DurationToleranceSeconds;

    /// <inheritdoc />
    public async Task<LyricResult?> FindAsync(TrackQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Title))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.LrcLib);

        // Query with the search form of the title/artist: LRCLIB indexes the song title, not the
        // release qualifiers ("(feat. X) (Original Mix)"). Matching still uses the raw values.
        var searchTitle = TrackMatcher.SearchTitle(query.Title);
        var searchArtist = TrackMatcher.SearchArtist(query.Artist);

        // 1) Exact lookup. LRCLIB recommends passing the duration for better matching.
        var getQuery = new List<string>
        {
            $"track_name={WebUtility.UrlEncode(searchTitle)}"
        };
        if (!string.IsNullOrWhiteSpace(searchArtist))
        {
            getQuery.Add($"artist_name={WebUtility.UrlEncode(searchArtist)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Album))
        {
            getQuery.Add($"album_name={WebUtility.UrlEncode(query.Album)}");
        }

        if (query.DurationSec is { } dur && dur > 0)
        {
            getQuery.Add($"duration={Math.Round(dur).ToString(CultureInfo.InvariantCulture)}");
        }

        using var response = await client.GetAsync(
            $"api/get?{string.Join("&", getQuery)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        LrcLibTrack? direct = null;
        string getReason;
        if (response.IsSuccessStatusCode)
        {
            direct = await response.Content.ReadFromJsonAsync<LrcLibTrack>(cancellationToken).ConfigureAwait(false);
            getReason = "no match";
        }
        else
        {
            getReason = $"get {(int)response.StatusCode}";
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                _logger.LogWarning("LRCLIB /api/get returned {StatusCode}", (int)response.StatusCode);
            }
        }

        if (direct is not null && Acceptable(query, direct))
        {
            return Map(direct);
        }

        if (direct is not null)
        {
            // The exact-lookup hit was rejected by the duration/title filter; say so, because
            // "LRCLIB has the song but the match was refused" is a different problem.
            getReason = "exact hit rejected (duration/title filter)";
        }

        // 2) Search fallback, filtered by duration and artist.
        var searchQuery = new List<string>
        {
            $"track_name={WebUtility.UrlEncode(searchTitle)}"
        };
        if (!string.IsNullOrWhiteSpace(searchArtist))
        {
            searchQuery.Add($"artist_name={WebUtility.UrlEncode(searchArtist)}");
        }

        using var searchResponse = await client.GetAsync(
            $"api/search?{string.Join("&", searchQuery)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!searchResponse.IsSuccessStatusCode)
        {
            var status = (int)searchResponse.StatusCode;
            if (searchResponse.StatusCode != HttpStatusCode.NotFound)
            {
                _logger.LogWarning("LRCLIB /api/search returned {StatusCode}", status);
            }

            return Miss($"{getReason}, search {status}", transient: status == 429 || status >= 500);
        }

        var candidates = await searchResponse.Content
            .ReadFromJsonAsync<List<LrcLibTrack>>(cancellationToken)
            .ConfigureAwait(false);

        if (candidates is null || candidates.Count == 0)
        {
            return Miss($"{getReason}, search returned no candidate", transient: false);
        }

        // Prefer synced, then closest duration.
        var best = candidates
            .Where(c => Acceptable(query, c))
            .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.SyncedLyrics))
            .ThenBy(c => query.DurationSec is { } t && c.Duration is { } d ? Math.Abs(d - t) : double.MaxValue)
            .FirstOrDefault();

        return best is null
            ? Miss($"{candidates.Count} candidate(s), none matched (duration/title filter)", transient: false)
            : Map(best);
    }

    private LyricResult Miss(string reason, bool transient) => new()
    {
        SourceName = Constants.SourceNames.LrcLib,
        Transient = transient,
        Message = reason
    };

    private static bool Acceptable(TrackQuery query, LrcLibTrack track)
    {
        var hasContent = !string.IsNullOrWhiteSpace(track.SyncedLyrics)
            || !string.IsNullOrWhiteSpace(track.PlainLyrics)
            || track.Instrumental == true;
        if (!hasContent)
        {
            return false;
        }

        if (StrictSearch && !string.IsNullOrWhiteSpace(query.Artist)
            && !TrackMatcher.ArtistMatches(query.Artist, track.ArtistName))
        {
            return false;
        }

        // The title must match the track title when the remote result supplies one
        // (rejects unrelated same-artist hits and covers with a different title).
        if (!string.IsNullOrWhiteSpace(track.TrackName)
            && !TrackMatcher.TitleMatches(query.Title, track.TrackName))
        {
            return false;
        }

        return DurationFilter.IsWithinTolerance(track.Duration, query.DurationSec, ToleranceSeconds);
    }

    private static LyricResult Map(LrcLibTrack track) => new()
    {
        SourceName = Constants.SourceNames.LrcLib,
        SyncedLrc = string.IsNullOrWhiteSpace(track.SyncedLyrics) ? null : track.SyncedLyrics,
        PlainText = string.IsNullOrWhiteSpace(track.PlainLyrics) ? null : track.PlainLyrics,
        IsInstrumental = track.Instrumental == true,
        Message = track.HasWordSync == true ? "word-level synced" : null
    };

    // Shape of lrclib.net/api/get and /api/search entries (verified live 2026-11). The names are
    // explicit so the mapping does not depend on the serializer's case-sensitivity defaults.
    private sealed class LrcLibTrack
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("trackName")]
        public string TrackName { get; set; } = string.Empty;

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; } = string.Empty;

        [JsonPropertyName("albumName")]
        public string AlbumName { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("instrumental")]
        public bool? Instrumental { get; set; }

        [JsonPropertyName("hasWordSync")]
        public bool? HasWordSync { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
