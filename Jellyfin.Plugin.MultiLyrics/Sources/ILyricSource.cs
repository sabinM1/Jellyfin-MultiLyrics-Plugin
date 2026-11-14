using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;

namespace Jellyfin.Plugin.MultiLyrics.Sources;

/// <summary>
/// One searchable lyric source. Implementations must be stateless and thread-safe, must never
/// throw on remote failures (return null + log instead), and must never log tokens or lyric
/// text.
/// </summary>
public interface ILyricSource
{
    /// <summary>Gets the stable source name used in configuration, cache and API.</summary>
    string Name { get; }

    /// <summary>Gets a value indicating whether the source can return synced (timed) lyrics.</summary>
    bool SupportsSynced { get; }

    /// <summary>Gets a value indicating whether the source is enabled under current configuration.</summary>
    bool IsEnabled { get; }

    /// <summary>Gets the minimum delay this source requires between two outbound requests.</summary>
    int MinDelayMs { get; }

    /// <summary>
    /// Searches this source for the track. Must return null (not throw) when nothing usable
    /// was found; network errors are logged by the queue.
    /// </summary>
    /// <param name="query">Track information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result, or null on miss/error.</returns>
    Task<LyricResult?> FindAsync(TrackQuery query, CancellationToken cancellationToken);
}
