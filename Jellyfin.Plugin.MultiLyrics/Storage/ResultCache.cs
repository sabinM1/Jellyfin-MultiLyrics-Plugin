using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.MultiLyrics.Core;

namespace Jellyfin.Plugin.MultiLyrics.Storage;

/// <summary>
/// Persistent cache mapping tracks to lyric results, per-source attempts and retry state.
/// Entries are keyed by item id when available, otherwise by a deterministic fingerprint
/// derived from artist/album/title/duration. Backed by a single JSON file written atomically.
/// </summary>
public interface IResultCache
{
    /// <summary>
    /// Returns the entry for the track, creating it (and resetting stale attempts) when needed.
    /// </summary>
    /// <param name="query">Track query.</param>
    /// <returns>The cache entry.</returns>
    CacheEntry GetOrAdd(TrackQuery query);

    /// <summary>
    /// Returns the entry for an item id, or null.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The entry or null.</returns>
    CacheEntry? Get(Guid itemId);

    /// <summary>
    /// Records one attempt of one source against the entry.
    /// </summary>
    /// <param name="entry">Entry to update.</param>
    /// <param name="attempt">Attempt to append.</param>
    void RecordAttempt(CacheEntry entry, SourceAttempt attempt);

    /// <summary>
    /// Records a winning result (ends the sweep with success).
    /// </summary>
    /// <param name="entry">Entry to update.</param>
    /// <param name="result">Winning result.</param>
    /// <param name="aligned">True when the text was aligned by the companion service.</param>
    void RecordWin(CacheEntry entry, LyricResult result, bool aligned);

    /// <summary>
    /// Records a full miss sweep: bumps the attempt counter and escalates the retry date.
    /// </summary>
    /// <param name="entry">Entry to update.</param>
    void RecordMiss(CacheEntry entry);

    /// <summary>
    /// Returns true when the given source should be skipped right now. The window is per source:
    /// one source missing (or failing on) a track must never stop the others from being queried.
    /// A recorded win is deliberately NOT a skip — the lyric text is ephemeral, so a track whose
    /// text left the in-memory window has to be fetched again.
    /// </summary>
    /// <param name="entry">Entry.</param>
    /// <param name="sourceName">Source about to be queried.</param>
    /// <returns>True to skip.</returns>
    bool ShouldSkip(CacheEntry entry, string sourceName);

    /// <summary>
    /// Removes entries whose last attempt is older than the retention window.
    /// </summary>
    /// <param name="retentionDays">Retention in days.</param>
    /// <returns>Number of pruned entries.</returns>
    int Prune(int retentionDays);

    /// <summary>
    /// Compares the source configuration (order, toggles, Genius mode/token) and the plugin
    /// version with what was recorded at the last sweep. When they differ, retry windows are
    /// ignored once: a fixed or newly enabled source must not stay blocked by misses recorded by
    /// the previous configuration.
    /// </summary>
    /// <param name="stamp">Stamp describing the current source configuration.</param>
    /// <returns>True when the stamp changed and was recorded.</returns>
    bool SourcesSettingsChanged(string stamp);
}

/// <summary>
/// One cached track.
/// </summary>
public sealed class CacheEntry
{
    /// <summary>Gets or sets the cache key (item id or deterministic fingerprint id).</summary>
    public Guid Key { get; set; }

    /// <summary>Gets or sets the Jellyfin item id, or Guid.Empty for fingerprint-only entries.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the track fingerprint at last use.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist at last use (for status display).</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the title at last use (for status display).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC timestamp of the last sweep.</summary>
    public DateTime LastAttemptUtc { get; set; } = DateTime.MinValue;

    /// <summary>Gets or sets the number of completed miss sweeps.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets the UTC time after which the track may be retried. Informational
    /// (status API): the actual gate is <see cref="RetryAfterBySource"/>.</summary>
    public DateTime? RetryAfterUtc { get; set; }

    /// <summary>
    /// Gets or sets when each source may be queried again for this track. A source missing a
    /// track only blocks itself; the others keep working.
    /// </summary>
    public Dictionary<string, DateTime> RetryAfterBySource { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets how many times each source failed for this track (drives the escalation).</summary>
    public Dictionary<string, int> MissCountBySource { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the kind of the held result.</summary>
    public LyricKind Kind { get; set; } = LyricKind.None;

    /// <summary>Gets or sets the source that produced the held result.</summary>
    public string? WinningSource { get; set; }

    /// <summary>Gets or sets a value indicating whether the result was produced by the alignment service.</summary>
    public bool Aligned { get; set; }

    /// <summary>Gets the per-source attempts of the last sweep.</summary>
    public List<SourceAttempt> Attempts { get; } = [];
}

/// <summary>
/// JSON-file backed implementation of <see cref="IResultCache"/>.
/// </summary>
public sealed class ResultCache : IResultCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        IncludeFields = true
    };

    private readonly string _filePath;
    private readonly string _statePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<Guid, CacheEntry> _entries;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResultCache"/> class.
    /// </summary>
    /// <param name="cacheFilePath">Absolute path of the cache JSON file.</param>
    public ResultCache(string cacheFilePath)
    {
        _filePath = cacheFilePath;
        _statePath = cacheFilePath + ".state";
        _entries = Load();
    }

    /// <inheritdoc />
    public CacheEntry GetOrAdd(TrackQuery query)
    {
        _lock.Wait();
        try
        {
            var key = query.ItemId == Guid.Empty ? DeterministicKey(query.Fingerprint()) : query.ItemId;
            if (_entries.TryGetValue(key, out var entry))
            {
                // Track metadata changed: previous attempts are meaningless.
                if (!string.Equals(entry.Fingerprint, query.Fingerprint(), StringComparison.Ordinal))
                {
                    entry.Attempts.Clear();
                    entry.AttemptCount = 0;
                    entry.RetryAfterUtc = null;
                    entry.RetryAfterBySource.Clear();
                    entry.MissCountBySource.Clear();
                    entry.WinningSource = null;
                    entry.Kind = LyricKind.None;
                    entry.Aligned = false;
                }
            }
            else
            {
                entry = new CacheEntry
                {
                    Key = key,
                    ItemId = query.ItemId,
                    Fingerprint = query.Fingerprint(),
                    Artist = query.Artist,
                    Title = query.Title
                };
                _entries[key] = entry;
            }

            entry.Artist = query.Artist;
            entry.Title = query.Title;
            entry.ItemId = query.ItemId == Guid.Empty ? entry.ItemId : query.ItemId;
            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public CacheEntry? Get(Guid itemId)
    {
        _lock.Wait();
        try
        {
            return _entries.TryGetValue(itemId, out var entry) ? entry : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void RecordAttempt(CacheEntry entry, SourceAttempt attempt)
    {
        _lock.Wait();
        try
        {
            entry.Attempts.Add(attempt);
            entry.LastAttemptUtc = DateTime.UtcNow;

            if (attempt.Success)
            {
                entry.RetryAfterBySource.Remove(attempt.Source);
                entry.MissCountBySource.Remove(attempt.Source);
            }
            else
            {
                var misses = entry.MissCountBySource.TryGetValue(attempt.Source, out var previous) ? previous + 1 : 1;
                entry.MissCountBySource[attempt.Source] = misses;
                entry.RetryAfterBySource[attempt.Source] = attempt.Transient
                    ? DateTime.UtcNow.Add(Constants.TransientRetryDelay)
                    : RetrySchedule.ComputeRetryAfter(
                        misses,
                        RetrySchedule.Parse(MultiLyricsPlugin.Config.RetryAfterDays),
                        DateTime.UtcNow);
            }

            SaveUnsafe();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void RecordWin(CacheEntry entry, LyricResult result, bool aligned)
    {
        _lock.Wait();
        try
        {
            entry.Kind = result.Kind;
            entry.WinningSource = result.SourceName;
            entry.Aligned = aligned;
            entry.RetryAfterUtc = null;
            entry.RetryAfterBySource.Clear();
            entry.MissCountBySource.Clear();
            entry.LastAttemptUtc = DateTime.UtcNow;

            SaveUnsafe();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void RecordMiss(CacheEntry entry)
    {
        _lock.Wait();
        try
        {
            entry.AttemptCount++;
            entry.LastAttemptUtc = DateTime.UtcNow;

            // Informational only: the per-source windows decide when a source runs again.
            entry.RetryAfterUtc = entry.RetryAfterBySource.Count == 0
                ? null
                : entry.RetryAfterBySource.Values.Max();
            SaveUnsafe();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public bool ShouldSkip(CacheEntry entry, string sourceName)
    {
        _lock.Wait();
        try
        {
            // Deliberately not keyed on entry.Kind: the lyric text lives in memory for one track
            // only, so "this track had lyrics once" must not stop a later fetch.
            return entry.RetryAfterBySource.TryGetValue(sourceName, out var due) && due > DateTime.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public int Prune(int retentionDays)
    {
        _lock.Wait();
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
            var stale = _entries.Where(kv => kv.Value.LastAttemptUtc < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                _entries.Remove(key);
            }

            if (stale.Count > 0)
            {
                SaveUnsafe();
            }

            return stale.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public bool SourcesSettingsChanged(string stamp)
    {
        try
        {
            if (File.Exists(_statePath)
                && string.Equals(File.ReadAllText(_statePath).Trim(), stamp, StringComparison.Ordinal))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_statePath, stamp);

            // The configuration changed: previous misses were recorded under a different set of
            // sources and must not gate the new one. Clearing here (instead of only bypassing for
            // one sweep) makes a newly enabled source effective for every track immediately.
            _lock.Wait();
            try
            {
                foreach (var entry in _entries.Values)
                {
                    entry.RetryAfterBySource.Clear();
                    entry.MissCountBySource.Clear();
                    entry.AttemptCount = 0;
                    entry.RetryAfterUtc = null;
                }

                SaveUnsafe();
            }
            finally
            {
                _lock.Release();
            }

            return true;
        }
        catch (IOException)
        {
            // A cache that cannot remember the setting behaves exactly as before.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Guid DeterministicKey(string fingerprint)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return new Guid(bytes);
    }

    private Dictionary<Guid, CacheEntry> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<Guid, CacheEntry>>(json, JsonOptions);
                if (entries is not null)
                {
                    return entries;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt cache: back it up and start fresh rather than failing the plugin.
            try
            {
                var backup = _filePath + ".corrupt-" + DateTime.UtcNow.Ticks;
                File.Move(_filePath, backup);
            }
            catch (IOException)
            {
            }
        }

        return [];
    }

    private void SaveUnsafe()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (IOException)
        {
            // Cache persistence failures must never break a lyrics run.
        }
    }
}
