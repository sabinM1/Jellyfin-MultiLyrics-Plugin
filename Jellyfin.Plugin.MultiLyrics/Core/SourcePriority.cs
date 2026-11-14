using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MultiLyrics.Core;

/// <summary>
/// Parses the configured comma-separated source order into the concrete, enabled queue order.
/// Rules: unknown names are dropped, duplicates collapse to the first occurrence, disabled
/// sources are skipped, and known sources missing from the list are appended at the end so a
/// config saved before a new source existed still exposes it.
/// </summary>
public static class SourcePriority
{
    /// <summary>
    /// Computes the ordered list of sources to query.
    /// </summary>
    /// <param name="configuredOrder">Comma-separated source names from configuration.</param>
    /// <param name="knownSources">All source names the plugin knows.</param>
    /// <param name="enabled">Per-source enable flags.</param>
    /// <returns>Ordered enabled source names; empty when nothing is enabled.</returns>
    public static IReadOnlyList<string> Parse(
        string? configuredOrder,
        IReadOnlyList<string> knownSources,
        IReadOnlyDictionary<string, bool> enabled)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredOrder))
        {
            foreach (var raw in configuredOrder.Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0
                    || !knownSources.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || result.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (enabled.TryGetValue(name, out var on) && on)
                {
                    result.Add(name);
                }
            }
        }

        foreach (var known in knownSources)
        {
            if (enabled.TryGetValue(known, out var on) && on && !result.Contains(known, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(known);
            }
        }

        return result;
    }
}
