using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MultiLyrics.Core;
using MediaBrowser.Model.Lyrics;

namespace Jellyfin.Plugin.MultiLyrics.Lyrics;

/// <summary>
/// Builds Jellyfin's lyric model from a payload held in memory. No file is read or written:
/// lyrics only exist in <see cref="Storage.EphemeralLyricsStore"/>.
/// </summary>
public static class LyricFiles
{
    /// Builds a lyric model from raw payload text, choosing the parser from the kind.
    /// </summary>
    /// <param name="kind">Payload kind.</param>
    /// <param name="payload">LRC or plain text.</param>
    /// <returns>The lyric model, or null when there is nothing to show.</returns>
    public static LyricDto? BuildDto(LyricKind kind, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        return kind == LyricKind.Synced ? BuildSyncedDto(payload) : BuildPlainDto(payload);
    }

    /// <summary>
    /// Parses LRC or enhanced LRC into a synced lyric model, including word cues.
    /// Content without timestamps falls back to plain text so a misnamed .lrc still displays.
    /// </summary>
    /// <param name="lrc">LRC content.</param>
    /// <returns>The lyric model, or null when the content is empty.</returns>
    public static LyricDto? BuildSyncedDto(string lrc)
    {
        var parsed = LrcParser.ParseWithCues(lrc);
        if (parsed.Count == 0)
        {
            return BuildPlainDto(lrc);
        }

        var lines = new List<LyricLine>(parsed.Count);
        foreach (var line in parsed)
        {
            IReadOnlyList<LyricLineCue>? cues = null;
            if (line.Cues is { Count: > 0 })
            {
                var mapped = new List<LyricLineCue>(line.Cues.Count);
                foreach (var cue in line.Cues)
                {
                    mapped.Add(new LyricLineCue(cue.Position, cue.EndPosition, cue.Start.Ticks, cue.End?.Ticks));
                }

                cues = mapped;
            }

            lines.Add(new LyricLine(line.Text, line.Time.Ticks, cues));
        }

        return new LyricDto
        {
            Metadata = new LyricMetadata { IsSynced = true },
            Lyrics = lines
        };
    }

    /// <summary>
    /// Splits plain text into an unsynced lyric model.
    /// </summary>
    /// <param name="text">Plain lyrics.</param>
    /// <returns>The lyric model, or null when there is no text.</returns>
    public static LyricDto? BuildPlainDto(string text)
    {
        var plainLines = LrcParser.PlainLines(text);
        if (plainLines.Count == 0)
        {
            return null;
        }

        var lines = new List<LyricLine>(plainLines.Count);
        foreach (var line in plainLines)
        {
            lines.Add(new LyricLine(line));
        }

        return new LyricDto
        {
            Metadata = new LyricMetadata { IsSynced = false },
            Lyrics = lines
        };
    }
}
