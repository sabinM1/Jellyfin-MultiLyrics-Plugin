using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.MultiLyrics.Core;

/// <summary>
/// Normalization and matching helpers for artist/title comparisons. Used to apply strict
/// search and to reject covers/live versions that only share a loose substring.
/// </summary>
public static class TrackMatcher
{
    /// <summary>
    /// Normalizes a title or artist: lowercase, diacritics folded to ASCII, punctuation and
    /// bracketed qualifiers removed, whitespace collapsed.
    /// </summary>
    /// <param name="value">Raw value.</param>
    /// <returns>Normalized comparison key (empty for null/blank input).</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Letters with a stroke/ligature have no canonical decomposition- fold them first.
        var folded = value.ToLowerInvariant();
        foreach (var (from, to) in SpecialFolds)
        {
            folded = folded.Replace(from, to, StringComparison.Ordinal);
        }

        var decomposed = folded.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static readonly (string From, string To)[] SpecialFolds =
    [
        ("\u0142", "l"), // ł
        ("\u00f8", "o"), // ø
        ("\u0111", "d"), // đ
        ("\u00f0", "d"), // ð
        ("\u00fe", "th"), // þ
        ("\u00df", "ss"), // ß
        ("\u00e6", "ae"), // æ
        ("\u0153", "oe"), // œ
    ];

    /// <summary>
    /// Qualifiers that describe the release, not the song. They are dropped from search terms so
    /// "Carnage (feat. Little Simz) (Original Mix)" is searched as "Carnage". Qualifiers that
    /// change the recording (live, acoustic, remix, demo) are deliberately kept.
    /// </summary>
    private static readonly string[] TechnicalQualifiers =
    [
        "remaster",
        "original mix",
        "extended mix",
        "radio edit",
        "album version",
        "single version",
        "original version",
        "explicit",
        "clean version",
        "deluxe",
        "bonus track",
        "reissue",
        "anniversary edition",
        "mono",
        "stereo",
    ];

    private static readonly string[] GuestMarkers = [" feat. ", " feat ", " ft. ", " ft "];

    /// <summary>
    /// Builds the title used for a source query: bracketed release qualifiers and guest credits
    /// are removed, everything else is preserved. Falls back to the raw title when nothing is
    /// left, so a title made only of qualifiers still searches for something.
    /// </summary>
    /// <param name="title">Raw track title from Jellyfin.</param>
    /// <returns>Title to send to a lyric source.</returns>
    public static string SearchTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = StripQualifiers(title);
        var dash = cleaned.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && ContainsTechnicalQualifier(cleaned[(dash + 3)..]))
        {
            cleaned = cleaned[..dash];
        }

        cleaned = CollapseSpaces(cleaned);
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    /// <summary>
    /// Builds the artist used for a source query. Multi-artist values ("A/ B", "A; B") are
    /// reduced to the first act; "AC/DC" has no space after the slash and is left intact.
    /// </summary>
    /// <param name="artist">Raw artist value from Jellyfin.</param>
    /// <returns>Artist to send to a lyric source.</returns>
    public static string SearchArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        var primary = artist.Trim();
        var cut = primary.Length;

        var slash = primary.IndexOf("/ ", StringComparison.Ordinal);
        if (slash > 0)
        {
            cut = Math.Min(cut, slash);
        }

        var semicolon = primary.IndexOf(';');
        if (semicolon > 0)
        {
            cut = Math.Min(cut, semicolon);
        }

        if (cut < primary.Length)
        {
            primary = primary[..cut].Trim();
        }

        var cleaned = CollapseSpaces(StripQualifiers(primary));
        return cleaned.Length == 0 ? artist.Trim() : cleaned;
    }

    private static string StripQualifiers(string value)
    {
        var sb = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c is '(' or '[' or '{')
            {
                var close = c == '(' ? ')' : c == '[' ? ']' : '}';
                var end = value.IndexOf(close, i + 1);
                if (end < 0)
                {
                    // Unbalanced bracket: keep the rest as-is rather than guessing.
                    sb.Append(value[i..]);
                    break;
                }

                if (!IsRemovableQualifier(value[(i + 1)..end]))
                {
                    // Keep the group as written: "Song (Live)" is a better search term than
                    // "Song Live", and the brackets tell the provider it is a variant.
                    sb.Append(' ').Append(c).Append(value[(i + 1)..end]).Append(close).Append(' ');
                }

                i = end + 1;
                continue;
            }

            sb.Append(c);
            i++;
        }

        var result = sb.ToString();
        foreach (var marker in GuestMarkers)
        {
            var idx = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                result = result[..idx];
                break;
            }
        }

        return result;
    }

    private static bool IsRemovableQualifier(string inner)
    {
        var value = inner.Trim();
        if (value.Length == 0)
        {
            return true;
        }

        if (value.Length == 4 && value.All(char.IsAsciiDigit))
        {
            return true;
        }

        if (value.StartsWith("feat", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ft.", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ft ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("with ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsTechnicalQualifier(value);
    }

    private static bool ContainsTechnicalQualifier(string value)
    {
        foreach (var keyword in TechnicalQualifiers)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CollapseSpaces(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    private static bool ContainsEither(string a, string b)
    {
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return true;
        }

        // Compare again without spaces to catch "AC/DC" vs "ACDC".
        var compactA = a.Replace(" ", string.Empty, StringComparison.Ordinal);
        var compactB = b.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compactA.Length > 0 && compactB.Length > 0
            && (compactA.Contains(compactB, StringComparison.Ordinal)
                || compactB.Contains(compactA, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true when the result artist matches the track artist under normalization.
    /// A blank query artist matches anything; a blank result artist never matches.
    /// </summary>
    /// <param name="queryArtist">Track artist.</param>
    /// <param name="resultArtist">Remote result artist.</param>
    /// <returns>True when the artists are considered the same act.</returns>
    public static bool ArtistMatches(string? queryArtist, string? resultArtist)
    {
        var query = Normalize(queryArtist);
        if (query.Length == 0)
        {
            return true;
        }

        var result = Normalize(resultArtist);
        if (result.Length == 0)
        {
            return false;
        }

        return ContainsEither(result, query);
    }

    /// <summary>
    /// Returns true when the result title matches the track title under normalization.
    /// </summary>
    /// <param name="queryTitle">Track title.</param>
    /// <param name="resultTitle">Remote result title.</param>
    /// <returns>True when the titles are considered the same song.</returns>
    public static bool TitleMatches(string? queryTitle, string? resultTitle)
    {
        var query = Normalize(queryTitle);
        if (query.Length == 0)
        {
            return false;
        }

        var result = Normalize(resultTitle);
        if (result.Length == 0)
        {
            return false;
        }

        return ContainsEither(result, query);
    }
}