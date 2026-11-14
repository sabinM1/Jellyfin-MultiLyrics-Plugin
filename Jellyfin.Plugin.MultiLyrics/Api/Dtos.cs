using System;
using Jellyfin.Plugin.MultiLyrics.Core;

namespace Jellyfin.Plugin.MultiLyrics.Api;

/// <summary>
/// One per-source attempt as reported by the status/manual-search API. Messages never
/// contain tokens or lyric text.
/// </summary>
public sealed class SourceAttemptDto
{
    /// <summary>Gets or sets the source name.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the source returned a usable result.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the classified result kind (None / Instrumental / Plain / Synced).</summary>
    public string Kind { get; set; } = "None";

    /// <summary>Gets or sets an optional note (miss reason, mode note, error type).</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the UTC timestamp.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets the round-trip duration in milliseconds.</summary>
    public int DurationMs { get; set; }
}

/// <summary>
/// Full status of one track, returned by GET /MultiLyrics/Status/{itemId} and by the manual
/// search endpoint.
/// </summary>
public sealed class TrackStatusDto
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the track title.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the primary artist.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the current sync status: None, Instrumental, Plain or Synced.</summary>
    public string SyncStatus { get; set; } = "None";

    /// <summary>Gets or sets the source that produced the current result.</summary>
    public string? WinningSource { get; set; }

    /// <summary>Gets or sets a value indicating whether the current result was word-level aligned.</summary>
    public bool Aligned { get; set; }

    /// <summary>Gets or sets a value indicating whether the lyrics are currently held in memory.</summary>
    public bool InMemory { get; set; }

    /// <summary>Gets or sets a value indicating whether the track is known to be instrumental.</summary>
    public bool Instrumental { get; set; }

    /// <summary>Gets or sets the UTC time of the last queue sweep, when one ran.</summary>
    public DateTime? LastAttemptUtc { get; set; }

    /// <summary>Gets or sets the UTC time after which the queue may retry, when in a retry window.</summary>
    public DateTime? RetryAfterUtc { get; set; }

    /// <summary>Gets or sets the number of completed miss sweeps.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets whether the alignment service is configured.</summary>
    public bool AlignmentConfigured { get; set; }

    /// <summary>Gets or sets the per-source attempts of the last sweep.</summary>
    public SourceAttemptDto[] Attempts { get; set; } = [];
}

/// <summary>
/// One registered source as reported by GET /MultiLyrics/Sources.
/// </summary>
public sealed class SourceInfoDto
{
    /// <summary>Gets or sets the stable source name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the source is enabled in configuration.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the source can deliver synced lyrics.</summary>
    public bool SupportsSynced { get; set; }

    /// <summary>Gets or sets a short capability note.</summary>
    public string? Note { get; set; }
}
