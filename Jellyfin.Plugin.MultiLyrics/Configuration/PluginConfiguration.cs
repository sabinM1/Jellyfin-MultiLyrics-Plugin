using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MultiLyrics.Configuration;

/// <summary>
/// Genius operating mode. The official Genius API never returns lyric text, so "SearchOnly" can
/// only surface song URLs; text requires scraping. The settings page no longer offers a choice:
/// enabling Genius means scraping, and the stored value is kept only so a configuration written by
/// an older build still loads.
/// </summary>
/// <remarks>
/// The host deserializes the posted settings page with its own enum handling, so the mapping is
/// made tolerant here: a blank or unknown value must not turn a save into a 500.
/// </remarks>
[JsonConverter(typeof(GeniusModeJsonConverter))]
public enum GeniusMode
{
    /// <summary>Do not use Genius at all (default).</summary>
    Off = 0,

    /// <summary>Retired: metadata-only mode. Treated as <see cref="Scrape"/>; kept for stored configurations.</summary>
    SearchOnly = 1,

    /// <summary>Scrape the song page HTML. May violate Genius Terms of Service. Personal use only.</summary>
    Scrape = 2
}

/// <summary>
/// Where sidecar files are written.
/// </summary>
public enum WritePolicy
{
    /// <summary>Next to the audio file (served by Jellyfin automatically after a library scan).</summary>
    BesideMedia = 0,

    /// <summary>In the item's internal metadata folder (mirrors Jellyfin's own "save lyrics" behavior).</summary>
    MetadataFolder = 1,

    /// <summary>Both locations.</summary>
    Both = 2
}

/// <summary>
/// When an existing sidecar may be replaced.
/// </summary>
public enum OverwritePolicy
{
    /// <summary>Never overwrite an existing sidecar.</summary>
    Never = 0,

    /// <summary>Overwrite only to upgrade plain (.txt) to synced (.lrc). Never downgrades.</summary>
    UpgradeSynced = 1,

    /// <summary>Always overwrite with the newest result.</summary>
    Always = 2
}

/// <summary>
/// Plugin configuration. Persisted by the host as plain XML in the plugin configuration
/// folder. The Genius token is stored in that plain XML file - the Jellyfin plugin API
/// offers no secret store. Documented in README and config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the comma-separated source priority order. Unknown names are ignored,
    /// disabled sources are skipped, known-but-missing sources are appended.
    /// </summary>
    public string SourceOrder { get; set; } = Constants.DefaultSourceOrder;

    /// <summary>
    /// Gets or sets a value indicating whether a synced result always beats a plain one, regardless
    /// of the priority order. When false the order is literal: the first source that returns any
    /// lyrics wins, even if it is plain text. Needed to see or verify a plain source (Genius,
    /// Tekstowo) while a synced source sits lower in the order.
    /// </summary>
    public bool PreferSyncedLyrics { get; set; } = true;

    /// <summary>Gets or sets whether LRCLIB (synced, no key) is queried.</summary>
    public bool EnableLrcLib { get; set; } = true;

    /// <summary>Gets or sets whether lyrics.ovh (plain, no key) is queried.</summary>
    public bool EnableLyricsOvh { get; set; } = true;

    /// <summary>Gets or sets whether Tekstowo (plain, unofficial scrape) is queried.</summary>
    public bool EnableTekstowo { get; set; } = true;

    /// <summary>Gets or sets whether Genius is queried. Requires an explicit ToS acknowledgement.</summary>
    public bool EnableGenius { get; set; } = false;

    /// <summary>
    /// Gets or sets the Genius client access token. Stored in plain XML (see class remarks).
    /// Never logged by the plugin.
    /// </summary>
    public string GeniusToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the Genius operating mode. Only <see cref="GeniusMode.Off"/> and
    /// <see cref="GeniusMode.Scrape"/> are produced by the settings page; the mode follows
    /// <see cref="EnableGenius"/>.</summary>
    public GeniusMode GeniusMode { get; set; } = GeniusMode.Off;

    /// <summary>
    /// Gets or sets a value indicating whether the admin acknowledged:
    /// "I understand this may violate Genius Terms of Service; personal use only."
    /// Genius stays unusable until this is true and <see cref="EnableGenius"/> is set.
    /// </summary>
    public bool GeniusToSAcknowledged { get; set; } = false;

    /// <summary>
    /// Gets or sets the base URL of the optional companion alignment service
    /// (Demucs + WhisperX). Empty disables alignment: plain-only results stay .txt.
    /// </summary>
    public string AlignmentServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether private/loopback addresses are accepted for
    /// the alignment host (typical for Docker/LAN deployments). Cloud metadata endpoints
    /// are always refused, even when this is true.
    /// </summary>
    public bool AllowPrivateAlignmentHost { get; set; } = false;

    /// <summary>Gets or sets the sidecar write policy (BesideMedia / MetadataFolder / Both).</summary>
    public WritePolicy WritePolicy { get; set; } = WritePolicy.BesideMedia;

    /// <summary>Gets or sets the overwrite rule for existing sidecars.</summary>
    public OverwritePolicy OverwritePolicy { get; set; } = OverwritePolicy.UpgradeSynced;

    /// <summary>Gets or sets the duration tolerance in seconds used to accept a remote match.</summary>
    public int DurationToleranceSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether strict search is enforced: the remote result's
    /// artist must match the track's artist, not just its title.
    /// </summary>
    public bool StrictSearch { get; set; } = false;

    /// <summary>Gets or sets the maximum number of tracks processed by one task run.</summary>
    public int MaxTracksPerRun { get; set; } = 200;

    /// <summary>Gets or sets the retry escalation in days as a comma-separated list, e.g. "1,3,7,30".</summary>
    public string RetryAfterDays { get; set; } = Constants.DefaultRetryScheduleDays;

    /// <summary>Gets or sets the minimum delay in milliseconds between two outbound source requests.</summary>
    public int RequestDelayMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets a value indicating whether the lyric provider may fetch on demand when a
    /// client requests lyrics for a track that has none cached yet.
    /// </summary>
    public bool FetchOnDemand { get; set; } = true;
}
