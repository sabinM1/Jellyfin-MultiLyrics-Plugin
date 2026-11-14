using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Sources;

/// <summary>
/// lyrics.ovh - plain text only, no key. Verified live:
/// GET /v1/{artist}/{title} returns {"lyrics": "..."} and 404 on miss.
/// </summary>
public sealed class LyricsOvhSource : ILyricSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LyricsOvhSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LyricsOvhSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public LyricsOvhSource(IHttpClientFactory httpClientFactory, ILogger<LyricsOvhSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Constants.SourceNames.LyricsOvh;

    /// <inheritdoc />
    public bool SupportsSynced => false;

    /// <inheritdoc />
    public bool IsEnabled => MultiLyricsPlugin.Config.EnableLyricsOvh;

    /// <inheritdoc />
    public int MinDelayMs => 500;

    /// <inheritdoc />
    public async Task<LyricResult?> FindAsync(TrackQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.LyricsOvh);

        // The path is a slug lookup on the provider side, so release qualifiers only hurt.
        var url = $"v1/{Uri.EscapeDataString(TrackMatcher.SearchArtist(query.Artist))}/{Uri.EscapeDataString(TrackMatcher.SearchTitle(query.Title))}";

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("lyrics.ovh returned {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > Constants.MaxResponseBytes)
        {
            _logger.LogWarning("lyrics.ovh response exceeds size cap, ignoring");
            return null;
        }

        var body = System.Text.Encoding.UTF8.GetString(payload);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var dto = System.Text.Json.JsonSerializer.Deserialize<LyricsOvhResponse>(body);
        var text = dto?.Lyrics?.Replace("\r\n", "\n").Trim();
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

    private sealed class LyricsOvhResponse
    {
        public string? Lyrics { get; set; }
    }
}
