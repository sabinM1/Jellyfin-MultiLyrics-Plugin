using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Jellyfin.Plugin.MultiLyrics.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Sources;

/// <summary>
/// Genius. The official API (api.genius.com, bearer token) returns metadata and song URLs
/// only - never lyric text and never timestamps. Text requires scraping the song page, which
/// violates Genius' Terms of Service, so this source is OFF by default and requires an
/// explicit acknowledgement. Enabling it always means scraping: the former metadata-only mode
/// is gone. The client access token is optional - without one the public genius.com search
/// endpoint is used to find the song page.
/// Cloudflare challenge responses are treated as a miss and never persisted as lyrics.
/// Tokens and lyric text are never logged.
/// <para>
/// Every miss returns a <see cref="LyricResult"/> whose <see cref="LyricResult.Kind"/> is
/// <see cref="LyricKind.None"/> and whose <see cref="LyricResult.Message"/> says why (no hits,
/// hits that did not match, rejected token, challenge). The queue logs that
/// reason, which is the only way to tell a broken integration from a track Genius lacks.
/// </para>
/// </summary>
public sealed class GeniusSource : ILyricSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeniusSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeniusSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public GeniusSource(IHttpClientFactory httpClientFactory, ILogger<GeniusSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Constants.SourceNames.Genius;

    /// <inheritdoc />
    public bool SupportsSynced => false;

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            var cfg = MultiLyricsPlugin.Config;
            return cfg.EnableGenius && cfg.GeniusToSAcknowledged;
        }
    }

    /// <inheritdoc />
    public int MinDelayMs => 1000;

    /// <inheritdoc />
    public async Task<LyricResult?> FindAsync(TrackQuery query, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return Miss(
                "not configured (needs the source enabled and the Terms-of-Service acknowledgement)",
                transient: false);
        }

        if (string.IsNullOrWhiteSpace(query.Title))
        {
            return Miss("no title to search", transient: false);
        }

        // Genius search is full-text and is thrown off by release qualifiers, so query the
        // cleaned title ("Carnage (feat. Little Simz) (Original Mix)" -> "Carnage") and the
        // primary act ("Chase & Status/ Stefflon Don" -> "Chase & Status").
        var searchTitle = TrackMatcher.SearchTitle(query.Title);
        var searchArtist = TrackMatcher.SearchArtist(query.Artist);

        // The artist narrows the result set; a featured/collab page is often only reachable by
        // its title, so a search without the artist is tried once when the first pass misses.
        var firstTerm = string.IsNullOrWhiteSpace(searchArtist) ? searchTitle : $"{searchArtist} {searchTitle}";
        var result = await SearchOnceAsync(firstTerm, query, cancellationToken).ConfigureAwait(false);

        if (result.Url is null
            && result.ApiAnswered
            && !string.IsNullOrWhiteSpace(searchArtist)
            && !string.Equals(firstTerm, searchTitle, StringComparison.Ordinal))
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var retry = await SearchOnceAsync(searchTitle, query, cancellationToken).ConfigureAwait(false);
            result = result with
            {
                Url = retry.Url,
                HitCount = retry.HitCount,
                ApiAnswered = retry.ApiAnswered,
                Transient = retry.Transient,
                Reason = retry.Url is null
                    ? $"{result.Reason}; title-only search: {retry.Reason}"
                    : retry.Reason
            };
        }

        if (result.Url is null)
        {
            return Miss(result.Reason, result.Transient);
        }

        // Scraping. Explicitly acknowledged. Cloudflare challenges are a miss, never lyrics.
        var (text, reason, transient) = await ScrapeSongPageAsync(result.Url, cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
        {
            return Miss($"page {reason}", transient);
        }

        return new LyricResult
        {
            SourceName = Name,
            PlainText = text,
            Message = "scraped - ToS risk, personal use only"
        };
    }

    /// <summary>
    /// Picks the URL of the first acceptable hit in a Genius /search payload. Internal for unit
    /// tests with synthetic metadata fixtures.
    /// </summary>
    /// <param name="payload">Raw JSON body of GET /search.</param>
    /// <param name="query">Track being resolved.</param>
    /// <param name="hitCount">Number of usable hits the payload contained.</param>
    /// <returns>Song page URL, or null when no hit matches.</returns>
    internal static string? SelectHitUrl(byte[] payload, TrackQuery query, out int hitCount)
    {
        hitCount = 0;

        GeniusSearchResponse? dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<GeniusSearchResponse>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        var results = dto?.Response?.Hits?
            .Select(h => h.Result)
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Url))
            .ToList();

        if (results is null || results.Count == 0)
        {
            return null;
        }

        hitCount = results.Count;

        // Genius orders hits by relevance, but a cover or a remix of the same title can rank
        // above the real song, so the first hit that passes matching is used.
        var accepted = results.FirstOrDefault(IsAcceptableHit(query));
        return accepted?.Url;
    }

    /// <summary>
    /// Picks the URL of the first acceptable hit in a public genius.com "api/search/multi" payload,
    /// which is what the source uses when no client access token is configured. Same matching
    /// rules as <see cref="SelectHitUrl"/>; only the envelope differs (sections of hits instead of
    /// one hit list). Internal for unit tests with synthetic fixtures.
    /// </summary>
    /// <param name="payload">Raw JSON body of GET https://genius.com/api/search/multi.</param>
    /// <param name="query">Track being resolved.</param>
    /// <param name="hitCount">Number of usable hits the payload contained.</param>
    /// <returns>Song page URL, or null when no hit matches.</returns>
    internal static string? SelectPublicHitUrl(byte[] payload, TrackQuery query, out int hitCount)
    {
        hitCount = 0;

        GeniusPublicSearchResponse? dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<GeniusPublicSearchResponse>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        var results = dto?.Response?.Sections?
            .SelectMany(s => s.Hits ?? new List<GeniusHit>())
            .Select(h => h.Result)
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Url))
            .ToList();

        if (results is null || results.Count == 0)
        {
            return null;
        }

        hitCount = results.Count;
        return results.FirstOrDefault(IsAcceptableHit(query))?.Url;
    }

    private static Func<GeniusHitResult?, bool> IsAcceptableHit(TrackQuery query)
    {
        // Both the raw title and the cleaned search title are compared: Genius stores the guest
        // credit in the title ("Carnage (feat. Little Simz)") while Jellyfin adds release
        // qualifiers on top ("... (Original Mix)"), so neither side is a subset of the other.
        var candidateTitles = new[] { query.Title, TrackMatcher.SearchTitle(query.Title) }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        return r =>
        {
            if (r is null)
            {
                return false;
            }

            var titleMatches = candidateTitles.Any(queryTitle =>
                TrackMatcher.TitleMatches(queryTitle, r.Title)
                || TrackMatcher.TitleMatches(queryTitle, r.FullTitle));

            if (!titleMatches)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(query.Artist))
            {
                return true;
            }

            // primary_artist covers the common case; artist_names covers collab pages where the
            // track artist is a featured act rather than the page owner.
            if (TrackMatcher.ArtistMatches(query.Artist, r.PrimaryArtist?.Name))
            {
                return true;
            }

            return ArtistNameValues(r).Any(name => TrackMatcher.ArtistMatches(query.Artist, name));
        };
    }

    /// <summary>
    /// Reads the artist names of a hit. 
    /// <para>
    /// api.genius.com documents <c>artist_names</c> as a single joined string
    /// ("A &amp; B"), while some responses and third-party mirrors send an array. Both shapes are
    /// accepted: deserializing the wrong one would throw and turn every search into "0 hits".
    /// </para>
    /// </summary>
    /// <param name="hit">Hit to read.</param>
    /// <returns>Every artist name that can be recovered.</returns>
    private static IEnumerable<string> ArtistNameValues(GeniusHitResult hit)
    {
        if (hit.PrimaryArtist?.Name is { Length: > 0 } primary)
        {
            yield return primary;
        }

        switch (hit.ArtistNames)
        {
            case JsonValue value when value.TryGetValue<string>(out var joined):
                yield return joined;
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue element && element.TryGetValue<string>(out var single))
                    {
                        yield return single;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// One search call. Never throws for an HTTP-level failure: the outcome carries the reason
    /// so the sweep summary can name it.
    /// <para>
    /// With a client access token the official api.genius.com endpoint is used. Without one the
    /// public genius.com search endpoint is queried instead: it needs no credentials and returns
    /// the same song pages, which is what makes the token optional.
    /// </para>
    /// </summary>
    /// <param name="term">Search term.</param>
    /// <param name="query">Track being resolved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The search outcome.</returns>
    private async Task<SearchOutcome> SearchOnceAsync(string term, TrackQuery query, CancellationToken cancellationToken)
    {
        var cfg = MultiLyricsPlugin.Config;
        var hasToken = !string.IsNullOrWhiteSpace(cfg.GeniusToken);

        var client = _httpClientFactory.CreateClient(
            hasToken ? Constants.HttpClientNames.Genius : Constants.HttpClientNames.GeniusPage);

        var url = hasToken
            ? $"search?q={Uri.EscapeDataString(term)}"
            : $"https://genius.com/api/search/multi?q={Uri.EscapeDataString(term)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (hasToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.GeniusToken);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var rejected = status is 401 or 403;
            return new SearchOutcome(
                null,
                0,
                rejected
                    ? (hasToken ? $"API {status} (token rejected)" : $"search blocked (HTTP {status})")
                    : $"API {status}",
                Transient: !rejected,
                ApiAnswered: false);
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > Constants.MaxResponseBytes)
        {
            return new SearchOutcome(null, 0, "search response too large", Transient: true, ApiAnswered: true);
        }

        int hitCount;
        var hitUrl = hasToken
            ? SelectHitUrl(payload, query, out hitCount)
            : SelectPublicHitUrl(payload, query, out hitCount);

        // Search terms and hit counts are metadata, never tokens or lyric text.
        _logger.LogDebug(
            "Genius search {Term} ({Mode}): {Hits} hit(s), {Outcome}",
            term,
            hasToken ? "api" : "public",
            hitCount,
            hitUrl is null ? "none matched" : "page selected");

        if (hasToken && hitUrl is null && hitCount == 0)
        {
            // Genius reports some failures inside a 200 body ("meta.status": 401). Without this
            // check a rejected token would read as "0 hits" - exactly the silent failure this
            // source used to have.
            var apiStatus = ReadMetaStatus(payload);
            if (apiStatus is not null and not 200)
            {
                var rejected = apiStatus is 401 or 403;
                return new SearchOutcome(
                    null,
                    0,
                    $"API {apiStatus} ({(rejected ? "token rejected" : "request refused")})",
                    Transient: !rejected,
                    ApiAnswered: false);
            }
        }

        if (hitUrl is not null)
        {
            return new SearchOutcome(hitUrl, hitCount, string.Empty, Transient: false, ApiAnswered: true);
        }

        return new SearchOutcome(
            null,
            hitCount,
            hitCount == 0 ? "0 hits" : $"{hitCount} hit(s), none matched",
            Transient: false,
            ApiAnswered: true);
    }

    private async Task<(string? Text, string Reason, bool Transient)> ScrapeSongPageAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.GeniusPage);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > Constants.MaxResponseBytes)
        {
            return (null, "exceeds size cap", false);
        }

        var html = Encoding.UTF8.GetString(payload);
        if (IsCloudflareChallenge((int)response.StatusCode, html, response.Headers))
        {
            // Strong signal: the server IP is being blocked, so Genius cannot work here at all.
            return (null, "Cloudflare challenge (server IP blocked by Genius)", true);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            return (null, $"returned {status}", status >= 500);
        }

        var text = ExtractLyrics(html);
        return text is null
            ? (null, "has no lyrics container (layout changed or page is a stub)", false)
            : (text, string.Empty, false);
    }

    /// <summary>
    /// Detects Cloudflare challenge/interstitial pages. Internal for unit tests.
    /// </summary>
    /// <param name="statusCode">HTTP status.</param>
    /// <param name="body">Response body (or a small sample).</param>
    /// <param name="headers">Response headers.</param>
    /// <returns>True when the response is a challenge, not a song page.</returns>
    internal static bool IsCloudflareChallenge(int statusCode, string body, System.Net.Http.Headers.HttpResponseHeaders? headers)
    {
        if (statusCode == 403 || statusCode == 503 || statusCode == 429)
        {
            return true;
        }

        if (headers is not null && headers.Contains("cf-mitigated"))
        {
            return true;
        }

        return body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
            || body.Contains("_cf_chl", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || body.Contains("cf_challenge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts lyric text from a Genius song page: concatenates all
    /// &lt;div data-lyrics-container="true"&gt; blocks. Internal for unit tests with
    /// synthetic fixtures (no real lyric text is bundled in this repository).
    /// <para>
    /// Genius wraps the "N Contributors / Song Lyrics" banner in the same container as the
    /// first verse but marks it <c>data-exclude-from-selection="true"</c>. Reading it verbatim
    /// put the contributor count and the song title at the top of every lyric payload.
    /// </para>
    /// </summary>
    /// <param name="html">Page HTML.</param>
    /// <returns>Lyric text, or null when none found.</returns>
    internal static string? ExtractLyrics(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var banner in doc.DocumentNode.SelectNodes("//*[@data-exclude-from-selection='true']")
                     ?? Enumerable.Empty<HtmlNode>())
        {
            banner.Remove();
        }

        var containers = doc.DocumentNode.SelectNodes("//div[@data-lyrics-container='true']");
        if (containers is null || containers.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var node in containers)
        {
            // Replace <br> elements with newlines before reading text content.
            foreach (var br in node.SelectNodes(".//br") ?? Enumerable.Empty<HtmlNode>())
            {
                br.ParentNode.ReplaceChild(doc.CreateTextNode("\n"), br);
            }

            sb.Append(WebUtility.HtmlDecode(node.InnerText));
            sb.Append('\n');
        }

        var lines = LrcParser.PlainLines(sb.ToString());
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>
    /// Reads <c>meta.status</c> from a search payload, or null when it cannot be read.
    /// </summary>
    /// <param name="payload">Raw JSON body.</param>
    /// <returns>The status reported by Genius, if any.</returns>
    private static int? ReadMetaStatus(byte[] payload)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<GeniusSearchResponse>(payload)?.Meta?.Status;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private LyricResult Miss(string reason, bool transient) => new()
    {
        SourceName = Name,
        Transient = transient,
        Message = reason
    };

    /// <summary>
    /// Result of one search call: the matched page (or null) plus a human-readable reason.
    /// </summary>
    /// <param name="Url">Matched song page URL.</param>
    /// <param name="HitCount">Number of usable hits.</param>
    /// <param name="Reason">Why no page was matched.</param>
    /// <param name="Transient">True when retrying soon may help.</param>
    /// <param name="ApiAnswered">True when the API answered with a usable payload.</param>
    private readonly record struct SearchOutcome(
        string? Url,
        int HitCount,
        string Reason,
        bool Transient,
        bool ApiAnswered);

    // Shape of api.genius.com/search responses. The names are explicit: Genius uses lower-case
    // keys and snake_case for primary_artist, which no naming convention derives from the
    // property names below.
    private sealed class GeniusPublicSearchResponse
    {
        [JsonPropertyName("response")]
        public GeniusPublicResponse? Response { get; set; }
    }

    private sealed class GeniusPublicResponse
    {
        [JsonPropertyName("sections")]
        public List<GeniusPublicSection>? Sections { get; set; }
    }

    private sealed class GeniusPublicSection
    {
        [JsonPropertyName("hits")]
        public List<GeniusHit>? Hits { get; set; }
    }

    private sealed class GeniusSearchResponse
    {
        [JsonPropertyName("meta")]
        public GeniusMeta? Meta { get; set; }

        [JsonPropertyName("response")]
        public GeniusResponseSection? Response { get; set; }
    }

    private sealed class GeniusMeta
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }
    }

    private sealed class GeniusResponseSection
    {
        [JsonPropertyName("hits")]
        public List<GeniusHit>? Hits { get; set; }
    }

    private sealed class GeniusHit
    {
        [JsonPropertyName("result")]
        public GeniusHitResult? Result { get; set; }
    }

    private sealed class GeniusHitResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("full_title")]
        public string? FullTitle { get; set; }

        [JsonPropertyName("primary_artist")]
        public GeniusArtist? PrimaryArtist { get; set; }

        [JsonPropertyName("artist_names")]
        public JsonNode? ArtistNames { get; set; }
    }

    private sealed class GeniusArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
