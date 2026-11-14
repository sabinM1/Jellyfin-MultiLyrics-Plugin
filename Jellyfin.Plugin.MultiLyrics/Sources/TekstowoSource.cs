using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Jellyfin.Plugin.MultiLyrics.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Sources;

/// <summary>
/// Tekstowo.pl (Polish lyrics site) - plain text only, unofficial scrape. The site's
/// robots.txt permits song pages (it only disallows login/search-admin pages); still treat
/// this as best-effort and stay at one request per track.
/// Verified live (2026-11): song pages live at /{artist-slug}/{title-slug}; the old
/// /piosenka,{artist},{title}.html form 301-redirects there. Lyrics sit inside
/// &lt;div class="song-text"&gt; &gt; &lt;div class="inner-text"&gt;.
/// </summary>
public sealed class TekstowoSource : ILyricSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TekstowoSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TekstowoSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TekstowoSource(IHttpClientFactory httpClientFactory, ILogger<TekstowoSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Constants.SourceNames.Tekstowo;

    /// <inheritdoc />
    public bool SupportsSynced => false;

    /// <inheritdoc />
    public bool IsEnabled => MultiLyricsPlugin.Config.EnableTekstowo;

    /// <inheritdoc />
    public int MinDelayMs => 1500;

    /// <inheritdoc />
    public async Task<LyricResult?> FindAsync(TrackQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.Tekstowo);

        // Tekstowo addresses pages by slug, so the URL must be built from the clean title.
        var url = $"{"https"}://www.tekstowo.pl/{Slug(TrackMatcher.SearchArtist(query.Artist))}/{Slug(TrackMatcher.SearchTitle(query.Title))}";

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Tekstowo returned {StatusCode} for a song page", (int)response.StatusCode);
            }

            return null;
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > Constants.MaxResponseBytes)
        {
            _logger.LogWarning("Tekstowo page exceeds size cap, ignoring");
            return null;
        }

        var html = Encoding.UTF8.GetString(payload);
        if (IsAntiBot(html))
        {
            _logger.LogInformation("Tekstowo served an anti-bot challenge, treating as miss");
            return null;
        }

        var text = ExtractLyrics(html);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new LyricResult
        {
            SourceName = Name,
            PlainText = text
        };
    }

    /// <summary>
    /// Builds a Tekstowo URL slug: lowercase, ASCII-folded, non-alphanumerics collapsed to '-'.
    /// Exposed as internal for unit tests.
    /// </summary>
    /// <param name="input">Artist or title.</param>
    /// <returns>The slug.</returns>
    internal static string Slug(string input)
    {
        var folded = FoldDiacritics(input.Trim().ToLowerInvariant());
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var c in folded)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Extracts the lyric text from a Tekstowo song page. Internal for unit tests with
    /// synthetic fixtures (no real lyric text is bundled in this repository).
    /// </summary>
    /// <param name="html">Page HTML.</param>
    /// <returns>Lyric text, or null when the page has none.</returns>
    internal static string? ExtractLyrics(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var node = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'song-text')]//div[contains(@class,'inner-text')]");
        if (node is null)
        {
            return null;
        }

        // Replace <br> elements with newlines before reading text content.
        foreach (var br in node.SelectNodes(".//br") ?? Enumerable.Empty<HtmlNode>())
        {
            br.ParentNode.ReplaceChild(doc.CreateTextNode("\n"), br);
        }

        var lines = LrcParser.PlainLines(WebUtility.HtmlDecode(node.InnerText));
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static bool IsAntiBot(string html)
    {
        return html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
            || html.Contains("_cf_chl", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("DDoS protection", StringComparison.OrdinalIgnoreCase);
    }

    private static string FoldDiacritics(string input)
    {
        // Polish and general Latin diacritics folded to ASCII.
        var map = new (string From, string To)[]
        {
            ("ą", "a"), ("ć", "c"), ("ę", "e"), ("ł", "l"), ("ń", "n"), ("ó", "o"),
            ("ś", "s"), ("ź", "z"), ("ż", "z"), ("ä", "a"), ("ö", "o"), ("ü", "u"),
            ("ß", "ss"), ("é", "e"), ("è", "e"), ("ê", "e"), ("à", "a"), ("â", "â"),
            ("ï", "i"), ("î", "i"), ("û", "u"), ("ô", "o"), ("ç", "c")
        };

        var sb = new StringBuilder(input);
        foreach (var (from, to) in map)
        {
            sb.Replace(from, to);
        }

        // Fold remaining accented characters via Unicode normalization: drop combining marks
        // only, keep every other character (slugs, punctuation and separators are handled by
        // Slug()).
        var normalized = sb.ToString().Normalize(NormalizationForm.FormD);
        var keep = new StringBuilder();
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                keep.Append(c);
            }
        }

        return keep.ToString();
    }
}
