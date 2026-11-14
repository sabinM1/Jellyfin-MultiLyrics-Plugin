using System;

namespace Jellyfin.Plugin.MultiLyrics.Core;

/// <summary>
/// Duration-based match filtering. A remote result is accepted when the track's duration is
/// unknown, the result's duration is unknown, or both are within tolerance.
/// </summary>
public static class DurationFilter
{
    /// <summary>
    /// Checks whether a remote result's duration is within tolerance of the track's duration.
    /// </summary>
    /// <param name="resultDurationSec">Remote duration in seconds, or null.</param>
    /// <param name="trackDurationSec">Track duration in seconds, or null.</param>
    /// <param name="toleranceSeconds">Tolerance in seconds (clamped to &gt;= 0).</param>
    /// <returns>True when the match passes the filter.</returns>
    public static bool IsWithinTolerance(double? resultDurationSec, double? trackDurationSec, int toleranceSeconds)
    {
        if (resultDurationSec is null || trackDurationSec is null)
        {
            return true;
        }

        var tolerance = Math.Max(0, toleranceSeconds);
        return Math.Abs(resultDurationSec.Value - trackDurationSec.Value) <= tolerance;
    }
}

/// <summary>
/// Escalating retry schedule: after each full miss sweep the track is skipped for
/// schedule[attempts-1] days (capped at the last entry). Default schedule: 1, 3, 7, 30 days.
/// </summary>
public static class RetrySchedule
{
    private static readonly int[] Fallback = [1, 3, 7, 30];

    /// <summary>
    /// Parses a comma-separated day list like "1,3,7,30". Invalid or non-positive entries are
    /// dropped; empty results fall back to the default 1/3/7/30. At most 10 entries are kept.
    /// </summary>
    /// <param name="csv">Configured value.</param>
    /// <returns>Parsed schedule in days.</returns>
    public static int[] Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return (int[])Fallback.Clone();
        }

        var days = new List<int>();
        foreach (var part in csv.Split(','))
        {
            if (int.TryParse(part.Trim(), out var d) && d > 0)
            {
                days.Add(d);
                if (days.Count == 10)
                {
                    break;
                }
            }
        }

        return days.Count == 0 ? (int[])Fallback.Clone() : [.. days];
    }

    /// <summary>
    /// Computes when a track may be retried after the n-th full miss sweep.
    /// </summary>
    /// <param name="attemptCount">Number of completed sweeps (1-based).</param>
    /// <param name="scheduleDays">Escalation schedule in days.</param>
    /// <param name="utcNow">Current UTC time.</param>
    /// <returns>UTC timestamp after which the track is due again.</returns>
    public static DateTime ComputeRetryAfter(int attemptCount, int[] scheduleDays, DateTime utcNow)
    {
        var schedule = scheduleDays is { Length: > 0 } ? scheduleDays : Fallback;
        var index = Math.Clamp(attemptCount - 1, 0, schedule.Length - 1);
        return utcNow.AddDays(schedule[index]);
    }
}
