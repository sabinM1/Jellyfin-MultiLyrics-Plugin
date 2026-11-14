using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Alignment;
using Jellyfin.Plugin.MultiLyrics.Api;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Lyrics;
using Jellyfin.Plugin.MultiLyrics.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Api;

/// <summary>
/// Admin-only manual search and status endpoints. The host discovers controllers from plugin
/// assemblies automatically and applies the standard authentication stack, so the built-in
/// "RequiresElevation" policy works here without touching Jellyfin core.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MultiLyrics")]
[Produces("application/json")]
public sealed class MultiLyricsController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILyricsQueue _queue;
    private readonly IResultCache _cache;
    private readonly AlignmentClient _alignment;
    private readonly EphemeralLyricsStore _lyrics;
    private readonly ILogger<MultiLyricsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiLyricsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="queue">Lyric priority queue.</param>
    /// <param name="cache">Result cache (attempts and retry state, no lyric text).</param>
    /// <param name="alignment">Alignment client.</param>
    /// <param name="lyrics">In-memory lyric store.</param>
    /// <param name="logger">Logger.</param>
    public MultiLyricsController(
        ILibraryManager libraryManager,
        ILyricsQueue queue,
        IResultCache cache,
        AlignmentClient alignment,
        EphemeralLyricsStore lyrics,
        ILogger<MultiLyricsController> logger)
    {
        _libraryManager = libraryManager;
        _queue = queue;
        _cache = cache;
        _alignment = alignment;
        _lyrics = lyrics;
        _logger = logger;
    }

    /// <summary>
    /// Lists all known sources with their current enable state.
    /// </summary>
    /// <returns>Source list.</returns>
    [HttpGet("Sources")]
    public ActionResult<IReadOnlyList<SourceInfoDto>> GetSources()
    {
        var cfg = MultiLyricsPlugin.Config;
        return Ok(_queue.AllSources().Select(s => new SourceInfoDto
        {
            Name = s.Name,
            Enabled = s.IsEnabled,
            SupportsSynced = s.SupportsSynced,
            Note = s.Name switch
            {
                Constants.SourceNames.LrcLib => "synced + plain, no key, primary",
                Constants.SourceNames.LyricsOvh => "plain, no key",
                Constants.SourceNames.Tekstowo => "plain, unofficial scrape",
                Constants.SourceNames.Genius => "OFF by default; scraping may violate Genius ToS",
                _ => null
            }
        }).ToList());
    }

    /// <summary>
    /// Reports the current lyric state of a track: sync status, whether the lyrics are held in
    /// memory right now, and what every source returned in the last sweep.
    /// </summary>
    /// <param name="itemId">Audio item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The track status.</returns>
    [HttpGet("Status/{itemId}")]
    public ActionResult<TrackStatusDto> GetStatus(Guid itemId, CancellationToken cancellationToken)
    {
        var audio = _libraryManager.GetItemById<Audio>(itemId);
        if (audio is null)
        {
            return NotFound();
        }

        return Ok(BuildStatus(audio, _cache.Get(itemId)));
    }

    /// <summary>
    /// Forces a lyrics search for one track. With <c>source</c> set, exactly that source is
    /// queried (manual override; bypasses enable flags and retry windows). Otherwise the
    /// configured priority queue runs. The result is held in memory for the current session;
    /// plain-only results are aligned when the companion service is configured.
    /// </summary>
    /// <param name="itemId">Audio item id.</param>
    /// <param name="source">Optional source name to force.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated track status.</returns>
    [HttpPost("Search/{itemId}")]
    public async Task<ActionResult<TrackStatusDto>> Search(Guid itemId, [FromQuery] string? source, CancellationToken cancellationToken)
    {
        var audio = _libraryManager.GetItemById<Audio>(itemId);
        if (audio is null)
        {
            return NotFound();
        }

        if (source is not null && _queue.AllSources().All(s => !string.Equals(s.Name, source, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new { error = $"Unknown source '{source}'", sources = _queue.AllSources().Select(s => s.Name).ToArray() });
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

        var outcome = await _queue.SearchAsync(query, source, cancellationToken).ConfigureAwait(false);
        var best = outcome.Best;

        if (best is not null)
        {
            var kind = best.Kind;
            var usedAlignment = false;
            if (kind == LyricKind.Plain && _alignment.IsConfigured)
            {
                var aligned = await _alignment.AlignAsync(audio.Path, best.PlainText!, null, true, cancellationToken)
                    .ConfigureAwait(false);
                if (aligned.Success)
                {
                    best = new LyricResult { SourceName = best.SourceName, SyncedLrc = aligned.Lrc, Message = "word-level aligned via companion service" };
                    kind = LyricKind.Synced;
                    usedAlignment = true;
                }
            }

            _lyrics.Set(
                outcome.Entry.Key,
                new CachedLyric(
                    kind,
                    kind == LyricKind.Synced ? best.SyncedLrc : null,
                    kind == LyricKind.Plain ? best.PlainText : null,
                    best.SourceName));
            _cache.RecordWin(outcome.Entry, best, usedAlignment);
            _logger.LogInformation("Multi-Lyrics manual search: {Kind} via {Source} for \"{Title}\" (kept in memory)", kind, best.SourceName, audio.Name);
        }

        return Ok(BuildStatus(audio, _cache.Get(itemId)));
    }

    private TrackStatusDto BuildStatus(Audio audio, CacheEntry? entry)
    {
        var dto = new TrackStatusDto
        {
            ItemId = audio.Id,
            Name = audio.Name,
            Artist = audio.Artists?.FirstOrDefault() ?? audio.AlbumArtists?.FirstOrDefault(),
            Album = audio.Album,
            AlignmentConfigured = MultiLyricsPlugin.Config.AlignmentServiceUrl.Length > 0,
            Attempts = entry?.Attempts
                .Select(a => new SourceAttemptDto
                {
                    Source = a.Source,
                    Success = a.Success,
                    Kind = a.Kind.ToString(),
                    Message = a.Message,
                    TimestampUtc = a.TimestampUtc,
                    DurationMs = a.DurationMs
                })
                .ToArray() ?? []
        };

        if (entry is not null)
        {
            dto.SyncStatus = entry.Kind.ToString();
            dto.WinningSource = entry.WinningSource;
            dto.Aligned = entry.Aligned;
            dto.LastAttemptUtc = entry.LastAttemptUtc == DateTime.MinValue ? null : entry.LastAttemptUtc;
            dto.RetryAfterUtc = entry.RetryAfterUtc;
            dto.AttemptCount = entry.AttemptCount;
        }

        if (_lyrics.TryGet(audio.Id, out var held))
        {
            dto.InMemory = true;
            dto.Instrumental = held.Kind == LyricKind.Instrumental;
            dto.SyncStatus = held.Kind.ToString();
            dto.WinningSource ??= held.SourceName;
        }

        return dto;
    }
}
