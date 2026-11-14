using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace Jellyfin.Plugin.MultiLyrics.Core;

/// <summary>
/// A single parsed LRC line.
/// </summary>
/// <param name="Time">Line start time.</param>
/// <param name="Text">Line text (word-level &lt;...&gt; tags stripped).</param>
public sealed record LrcLine(TimeSpan Time, string Text);

/// <summary>
/// A word-level cue inside one enhanced LRC line.
/// </summary>
/// <param name="Position">Start character index inside <see cref="LrcCuedLine.Text"/>.</param>
/// <param name="EndPosition">End character index (exclusive) inside <see cref="LrcCuedLine.Text"/>.</param>
/// <param name="Start">Cue start time.</param>
/// <param name="End">Cue end time, or null for the last cue of a line.</param>
public sealed record LrcWordCue(int Position, int EndPosition, TimeSpan Start, TimeSpan? End);

/// <summary>
/// A parsed LRC line with optional word-level cues (enhanced LRC ".elrc" style).
/// </summary>
/// <param name="Time">Line start time.</param>
/// <param name="Text">Line text with word tags stripped.</param>
/// <param name="Cues">Word cues, or null for line-level content.</param>
public sealed record LrcCuedLine(TimeSpan Time, string Text, IReadOnlyList<LrcWordCue>? Cues);

/// <summary>
/// Parses and formats LRC content. Word-level enhanced LRC ("[00:12.34]&lt;00:12.34&gt;word ...")
/// is understood: the &lt;...&gt; tags are stripped for line-level display.
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimestampTag();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTag();

    [GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|au|length):(.*)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataTag();

    /// <summary>
    /// Parses LRC content into ordered lines. Repeated timestamp tags expand into separate
    /// lines; metadata tags ([ar:], [ti:], [offset:] ...) are skipped, with [offset:] applied
    /// to all timestamps.
    /// </summary>
    /// <param name="lrc">Raw LRC content.</param>
    /// <returns>Parsed lines sorted by time.</returns>
    public static IReadOnlyList<LrcLine> Parse(string lrc)
    {
        var cued = ParseWithCues(lrc);
        var lines = new List<LrcLine>(cued.Count);
        foreach (var line in cued)
        {
            lines.Add(new LrcLine(line.Time, line.Text));
        }

        return lines;
    }

    /// <summary>
    /// Parses LRC content into ordered lines plus word-level cues when the content uses
    /// enhanced LRC (&lt;mm:ss.xx&gt;word) tags.
    /// </summary>
    /// <param name="lrc">Raw LRC content.</param>
    /// <returns>Parsed lines sorted by time.</returns>
    public static IReadOnlyList<LrcCuedLine> ParseWithCues(string lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return [];
        }

        var offsetMs = 0;
        var lines = new List<LrcCuedLine>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var meta = MetadataTag().Match(raw.Trim());
            if (meta.Success)
            {
                if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(meta.Groups[2].Value.Trim(), out var off))
                {
                    // Positive LRC offset shifts lyrics earlier, i.e. subtract from timestamps.
                    offsetMs = -off;
                }

                continue;
            }

            var matches = TimestampTag().Matches(raw);
            if (matches.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var m = (Match)matches[i]!;
                // Text of this timestamp = everything up to the next timestamp tag. This keeps
                // standard repeated timestamps ([00:10][00:20]chorus) and word-level lines
                // ([00:10.00]<00:10.00>Syn <00:10.50>words) both correct.
                var start = m.Index + m.Length;
                var end = i + 1 < matches.Count ? ((Match)matches[i + 1]!).Index : raw.Length;
                var time = TimeFromTag(m) + TimeSpan.FromMilliseconds(offsetMs);
                var (text, cues) = SplitWords(raw[start..end], offsetMs);
                lines.Add(new LrcCuedLine(time, text, cues));
            }
        }

        lines.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static TimeSpan TimeFromTag(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var fracRaw = m.Groups[3].Value;
        var ms = fracRaw.Length == 0
            ? 0
            : int.Parse(fracRaw, System.Globalization.CultureInfo.InvariantCulture) * (int)Math.Pow(10, 3 - fracRaw.Length);
        return TimeSpan.FromMilliseconds((minutes * 60_000) + (seconds * 1_000) + ms);
    }

    private static (string Text, IReadOnlyList<LrcWordCue>? Cues) SplitWords(string block, int offsetMs)
    {
        var matches = WordTag().Matches(block);
        if (matches.Count == 0)
        {
            return (block.Trim(), null);
        }

        var sb = new System.Text.StringBuilder(block.Length);
        var cues = new List<LrcWordCue>(matches.Count);
        var cursor = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = (Match)matches[i]!;
            sb.Append(block, cursor, m.Index - cursor);
            var wordStart = sb.Length;
            var wordEnd = i + 1 < matches.Count ? ((Match)matches[i + 1]!).Index : block.Length;
            var wordLength = wordEnd - (m.Index + m.Length);
            sb.Append(block, m.Index + m.Length, wordLength);

            var start = TimeFromTag(m) + TimeSpan.FromMilliseconds(offsetMs);
            TimeSpan? cueEnd = i + 1 < matches.Count
                ? TimeFromTag((Match)matches[i + 1]!) + TimeSpan.FromMilliseconds(offsetMs)
                : null;

            // Cue ranges cover the word, not the separator that follows it.
            var cueStop = sb.Length;
            while (cueStop > wordStart && char.IsWhiteSpace(sb[cueStop - 1]))
            {
                cueStop--;
            }

            if (cueStop > wordStart)
            {
                cues.Add(new LrcWordCue(wordStart, cueStop, start, cueEnd));
            }

            cursor = wordEnd;
        }

        sb.Append(block, cursor, block.Length - cursor);

        // Cue indices refer to the untrimmed text; shifting them by the leading whitespace
        // keeps them valid after Trim().
        var visible = sb.ToString();
        var lead = visible.Length - visible.TrimStart().Length;
        var text = visible.Trim();
        var shifted = new List<LrcWordCue>(cues.Count);
        foreach (var cue in cues)
        {
            var pos = Math.Max(0, cue.Position - lead);
            if (pos >= text.Length)
            {
                continue;
            }

            var endPos = Math.Clamp(cue.EndPosition - lead, pos, text.Length);
            shifted.Add(cue with { Position = pos, EndPosition = endPos });
        }

        return (text, shifted.Count > 0 ? shifted : null);
    }

    /// <summary>
    /// Returns true when the content contains at least one LRC timestamp tag and is therefore
    /// treated as synced.
    /// </summary>
    /// <param name="content">Raw sidecar/content candidate.</param>
    /// <returns>True if synced-looking.</returns>
    public static bool LooksSynced(string content)
        => !string.IsNullOrWhiteSpace(content) && TimestampTag().IsMatch(content);

    /// <summary>
    /// Formats parsed lines as a plain (line-level) LRC document.
    /// </summary>
    /// <param name="lines">Lines to serialize.</param>
    /// <returns>LRC text.</returns>
    public static string FormatSynced(IReadOnlyList<LrcLine> lines)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[by:Multi-Lyrics]");
        foreach (var line in lines)
        {
            sb.Append('[').Append(FormatTime(line.Time)).Append(']').AppendLine(line.Text);
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Formats a time as mm:ss.xx (two fraction digits).
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>Formatted timestamp, e.g. "02:03.45".</returns>
    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }

    /// <summary>
    /// Splits plain text into non-empty trimmed lines.
    /// </summary>
    /// <param name="text">Plain text.</param>
    /// <returns>Trimmed non-empty lines.</returns>
    public static IReadOnlyList<string> PlainLines(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
