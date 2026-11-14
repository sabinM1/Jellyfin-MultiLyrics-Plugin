using System;

namespace Jellyfin.Plugin.MultiLyrics;

/// <summary>
/// Static constants shared across the plugin.
/// </summary>
public static class Constants
{
    /// <summary>Stable plugin GUID. Referenced by build.yaml and the configuration page.</summary>
    public const string PluginGuid = "7c1e4a9e-52b8-4c2f-9a3d-6e8f1b2c4d5e";

    /// <summary>Display name of the plugin. Used as the lyric fetcher name in library options.</summary>
    public const string PluginName = "Multi-Lyrics";

    /// <summary>User-Agent used for all outbound lyric-source requests. Never contains tokens.</summary>
    public const string UserAgent = "Jellyfin-Multi-Lyrics/1.0 (https://github.com/multi-lyrics/jellyfin-plugin-multi-lyrics)";

    /// <summary>Default comma-separated source priority order.</summary>
    public const string DefaultSourceOrder = "LrcLib,LyricsOvh,Tekstowo,Genius";

    /// <summary>Default per-track retry escalation in days (misses).</summary>
    public const string DefaultRetryScheduleDays = "1,3,7,30";

    /// <summary>
    /// How long a source is skipped after a transient failure (timeout, HTTP 429/5xx, Cloudflare
    /// challenge). Short on purpose: an outage must not hide a track for the usual retry schedule.
    /// </summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>Hard cap on any single lyric/alignment HTTP response body read by the plugin.</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>Hard cap on the plain-text payload sent to the alignment service.</summary>
    public const int MaxAlignTextChars = 32 * 1024;

    /// <summary>Stable source names. Persisted in config (SourceOrder), cache and API responses.</summary>
    public static class SourceNames
    {
        /// <summary>LRCLIB (lrclib.net) - synced + plain, no key.</summary>
        public const string LrcLib = "LrcLib";

        /// <summary>lyrics.ovh - plain, no key.</summary>
        public const string LyricsOvh = "LyricsOvh";

        /// <summary>Tekstowo.pl - plain, unofficial scrape.</summary>
        public const string Tekstowo = "Tekstowo";

        /// <summary>Genius - metadata API; lyric text only via scraping (ToS risk). Off by default.</summary>
        public const string Genius = "Genius";
    }

    /// <summary>Named HttpClient registrations created in PluginServiceRegistrator.</summary>
    public static class HttpClientNames
    {
        /// <summary>LRCLIB client.</summary>
        public const string LrcLib = "MultiLyrics.LrcLib";

        /// <summary>lyrics.ovh client.</summary>
        public const string LyricsOvh = "MultiLyrics.LyricsOvh";

        /// <summary>Tekstowo client.</summary>
        public const string Tekstowo = "MultiLyrics.Tekstowo";

        /// <summary>Genius API client (api.genius.com).</summary>
        public const string Genius = "MultiLyrics.Genius";

        /// <summary>Genius song-page client (genius.com, used only in scrape mode).</summary>
        public const string GeniusPage = "MultiLyrics.GeniusPage";

        /// <summary>Companion alignment service client. Redirects disabled; size-capped in code.</summary>
        public const string Alignment = "MultiLyrics.Alignment";
    }
}
