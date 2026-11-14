using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MultiLyrics.Core;

namespace Jellyfin.Plugin.MultiLyrics.Storage;

/// <summary>
/// One lyric payload held in memory.
/// </summary>
/// <param name="Kind">Synced, plain or instrumental.</param>
/// <param name="SyncedLrc">LRC payload for a synced result.</param>
/// <param name="PlainText">Text payload for a plain result.</param>
/// <param name="SourceName">Source that produced the payload.</param>
public readonly record struct CachedLyric(LyricKind Kind, string? SyncedLrc, string? PlainText, string? SourceName);

/// <summary>
/// Holds fetched lyrics in memory only. Nothing is written to the library or to the metadata
/// folder: a track's lyrics exist only while that track is the one being resolved.
/// <para>
/// The store keeps exactly one entry, so moving to the next track drops the previous lyrics
/// immediately; the payload is gone for good on restart. Entries also expire after
/// <see cref="Ttl"/> so an idle server does not pin text. Instrumental results are stored as a
/// marker without payload.
/// </para>
/// </summary>
public sealed class EphemeralLyricsStore
{
    /// <summary>
    /// Number of tracks kept in memory. One: the track being resolved right now. The next track's
    /// lyrics replace them, and a miss drops them, so nothing survives a song change.
    /// </summary>
    public const int Capacity = 1;

    /// <summary>How long an entry stays available after it was last written.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly LinkedList<Guid> _order = new();
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Initializes a new instance of the <see cref="EphemeralLyricsStore"/> class with the default TTL.
    /// </summary>
    public EphemeralLyricsStore()
        : this(Ttl)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EphemeralLyricsStore"/> class with an explicit
    /// TTL. Used by tests; production code uses the default.
    /// </summary>
    /// <param name="ttl">How long an entry stays available after it was written.</param>
    public EphemeralLyricsStore(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    /// <summary>
    /// Gets the number of tracks currently held. Exposed for tests and the status API.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                PruneExpired();
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Tries to get the lyrics for a track, marking the entry as most recently used.
    /// </summary>
    /// <param name="key">Track key (item id, or the cache entry key).</param>
    /// <param name="lyric">The payload, when found.</param>
    /// <returns>True when the track is still in memory.</returns>
    public bool TryGet(Guid key, out CachedLyric lyric)
    {
        lock (_sync)
        {
            PruneExpired();

            if (_entries.TryGetValue(key, out var entry))
            {
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                lyric = entry.Lyric;
                return true;
            }

            lyric = default;
            return false;
        }
    }

    /// <summary>
    /// Stores lyrics for a track and drops the least recently used entry when the window is full.
    /// </summary>
    /// <param name="key">Track key (item id, or the cache entry key).</param>
    /// <param name="lyric">Payload to hold.</param>
    public void Set(Guid key, CachedLyric lyric)
    {
        lock (_sync)
        {
            PruneExpired();

            if (_entries.TryGetValue(key, out var existing))
            {
                _order.Remove(existing.Node);
            }

            var node = _order.AddFirst(key);
            _entries[key] = new Entry(lyric, node, DateTime.UtcNow);

            while (_entries.Count > Capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _entries.Remove(oldest.Value);
            }
        }
    }

    /// <summary>
    /// Reports whether a track is currently held, without changing the LRU order.
    /// </summary>
    /// <param name="key">Track key.</param>
    /// <returns>True when a payload is held.</returns>
    public bool Contains(Guid key)
    {
        lock (_sync)
        {
            PruneExpired();
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// Drops everything held. Used by tests and when the track being resolved has no lyrics: the
    /// previous track's payload must not outlive the song change.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        while (_order.Last is { } oldest && _entries.TryGetValue(oldest.Value, out var entry) && entry.WrittenUtc < cutoff)
        {
            _order.RemoveLast();
            _entries.Remove(oldest.Value);
        }
    }

    private sealed record Entry(CachedLyric Lyric, LinkedListNode<Guid> Node, DateTime WrittenUtc);
}
