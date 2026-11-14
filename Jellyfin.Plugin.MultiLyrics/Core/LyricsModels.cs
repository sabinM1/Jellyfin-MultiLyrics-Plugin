namespace Jellyfin.Plugin.MultiLyrics.Core;

/// <summary>
/// Sync status of a lyric result. Every result records one of these so the UI and the
/// status API can always tell synced from plain.
/// </summary>
public enum LyricKind
{
    /// <summary>No usable result.</summary>
    None = 0,

    /// <summary>Track is instrumental (nothing to display).</summary>
    Instrumental = 1,

    /// <summary>Plain text (.txt), no timestamps.</summary>
    Plain = 2,

    /// <summary>Synced LRC (.lrc / .elrc), line or word level.</summary>
    Synced = 3
}

/// <summary>
/// The information needed to search every lyric source for one track.
/// </summary>
public sealed class TrackQuery
{
    /// <summary>Gets or sets the Jellyfin item id; <see cref="Guid.Empty"/> when unknown (cache falls back to fingerprint).</summary>
    public Guid ItemId { get; set; } = Guid.Empty;

    /// <summary>Gets or sets the primary artist (album artist preferred, then track artist).</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album name, if known.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the track duration in seconds, if known.</summary>
    public double? DurationSec { get; set; }

    /// <summary>Gets or sets the language hint, if known.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the audio file path on disk, if known.</summary>
    public string? AudioPath { get; set; }

    /// <summary>
    /// Builds the stable fingerprint used as cache key when no item id is available.
    /// </summary>
    /// <returns>A string like "artist|album|title|seconds".</returns>
    public string Fingerprint()
    {
        var duration = DurationSec is null ? "-" : Math.Round(DurationSec.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{Artist.ToLowerInvariant()}|{(Album?.ToLowerInvariant() ?? "-")}|{Title.ToLowerInvariant()}|{duration}";
    }
}

/// <summary>
/// A result returned by one <c>ILyricSource</c>. Exactly one of <see cref="SyncedLrc"/> /
/// <see cref="PlainText"/> is usually set; <see cref="IsInstrumental"/> replaces both for
/// instrumental tracks. <see cref="Message"/> carries miss/failure reasons for the status API.
/// </summary>
public sealed class LyricResult
{
    /// <summary>Gets or sets the stable source name (see Constants.SourceNames).</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the synced LRC content (line or word level), or null.</summary>
    public string? SyncedLrc { get; set; }

    /// <summary>Gets or sets the plain text content, or null.</summary>
    public string? PlainText { get; set; }

    /// <summary>Gets or sets a value indicating whether the track is instrumental.</summary>
    public bool IsInstrumental { get; set; }

    /// <summary>Gets or sets an optional human-readable note (miss reason, URL for Genius search-only, ...).</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a miss was caused by a transient condition (HTTP
    /// 5xx/429, Cloudflare challenge, timeout). A transient miss is retried within minutes; a
    /// genuine "this source does not have the track" miss follows the escalating retry schedule.
    /// </summary>
    public bool Transient { get; set; }

    /// <summary>Gets a value indicating whether a synced payload is present.</summary>
    public bool IsSynced => !string.IsNullOrWhiteSpace(SyncedLrc);

    /// <summary>Gets the classified kind of this result.</summary>
    public LyricKind Kind
    {
        get
        {
            if (IsSynced)
            {
                return LyricKind.Synced;
            }

            if (IsInstrumental)
            {
                return LyricKind.Instrumental;
            }

            return string.IsNullOrWhiteSpace(PlainText) ? LyricKind.None : LyricKind.Plain;
        }
    }

    /// <summary>
    /// Gets the payload handed to the host: synced LRC if present, otherwise plain text.
    /// </summary>
    /// <returns>The body text, or null when the result is empty/instrumental.</returns>
    public string? BodyForHost()
    {
        if (IsSynced)
        {
            return SyncedLrc;
        }

        return Kind == LyricKind.Plain ? PlainText : null;
    }
}

/// <summary>
/// One recorded attempt of one source for one track, kept in the result cache and shown
/// by the status API.
/// </summary>
public sealed class SourceAttempt
{
    /// <summary>Gets or sets the stable source name.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the source returned a usable result.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the classified kind returned (None on misses).</summary>
    public LyricKind Kind { get; set; } = LyricKind.None;

    /// <summary>Gets or sets an optional note. Must never contain tokens or lyric text.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets a value indicating whether the failure was transient (see <see cref="LyricResult.Transient"/>).</summary>
    public bool Transient { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the attempt.</summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the round-trip duration in milliseconds (best effort).</summary>
    public int DurationMs { get; set; }
}
