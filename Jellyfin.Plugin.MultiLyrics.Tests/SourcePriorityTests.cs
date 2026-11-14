using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MultiLyrics.Core;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

public class SourcePriorityTests
{
    private static readonly string[] Known =
    [
        Constants.SourceNames.LrcLib,
        Constants.SourceNames.LyricsOvh,
        Constants.SourceNames.Tekstowo,
        Constants.SourceNames.Genius
    ];

    private static Dictionary<string, bool> AllEnabled(bool value = true) => new(StringComparer.OrdinalIgnoreCase)
    {
        [Constants.SourceNames.LrcLib] = value,
        [Constants.SourceNames.LyricsOvh] = value,
        [Constants.SourceNames.Tekstowo] = value,
        [Constants.SourceNames.Genius] = value
    };

    [Fact]
    public void ParsesConfiguredOrderAndSkipsDisabled()
    {
        var enabled = AllEnabled();
        enabled[Constants.SourceNames.Tekstowo] = false;

        var order = SourcePriority.Parse("LrcLib,Tekstowo,LyricsOvh", Known, enabled);

        Assert.Equal(new[] { "LrcLib", "LyricsOvh", "Genius" }, order);
    }

    [Fact]
    public void DropsUnknownNamesAndDuplicates()
    {
        var order = SourcePriority.Parse(
            "LrcLib, FooBar, LyricsOvh, LrcLib, , Genius",
            Known,
            AllEnabled());

        Assert.Equal(new[] { "LrcLib", "LyricsOvh", "Genius", "Tekstowo" }, order);
    }

    [Fact]
    public void AppendsKnownSourcesMissingFromList()
    {
        var order = SourcePriority.Parse("Genius", Known, AllEnabled());

        // Genius first (as configured), then every other known source in default order.
        Assert.Equal(new[] { "Genius", "LrcLib", "LyricsOvh", "Tekstowo" }, order);
    }

    [Fact]
    public void EmptyConfigUsesDefaultOrder()
    {
        var order = SourcePriority.Parse(null, Known, AllEnabled());

        Assert.Equal(new[] { "LrcLib", "LyricsOvh", "Tekstowo", "Genius" }, order);
    }

    [Fact]
    public void EmptyResultWhenNothingEnabled()
    {
        Assert.Empty(SourcePriority.Parse("LrcLib,LyricsOvh", Known, AllEnabled(false)));
    }
}

public class DurationFilterTests
{
    [Theory]
    [InlineData(213.0, 214.0, 10, true)]
    [InlineData(213.0, 224.0, 10, false)]
    [InlineData(213.0, 223.0, 10, true)]
    [InlineData(30.0, 300.0, 5, false)]
    public void FiltersByTolerance(double? result, double? track, int tolerance, bool expected)
    {
        Assert.Equal(expected, DurationFilter.IsWithinTolerance(result, track, tolerance));
    }

    [Fact]
    public void NullDurationsAlwaysPass()
    {
        Assert.True(DurationFilter.IsWithinTolerance(null, 200, 10));
        Assert.True(DurationFilter.IsWithinTolerance(200, null, 10));
        Assert.True(DurationFilter.IsWithinTolerance(null, null, 10));
    }
}

public class RetryScheduleTests
{
    [Fact]
    public void EscalatesOneThreeSevenThirty()
    {
        var schedule = RetrySchedule.Parse("1,3,7,30");
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(t0.AddDays(1), RetrySchedule.ComputeRetryAfter(1, schedule, t0));
        Assert.Equal(t0.AddDays(3), RetrySchedule.ComputeRetryAfter(2, schedule, t0));
        Assert.Equal(t0.AddDays(7), RetrySchedule.ComputeRetryAfter(3, schedule, t0));
        Assert.Equal(t0.AddDays(30), RetrySchedule.ComputeRetryAfter(4, schedule, t0));
    }

    [Fact]
    public void CapsAtLastEntry()
    {
        var schedule = RetrySchedule.Parse("1,3,7,30");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(t0.AddDays(30), RetrySchedule.ComputeRetryAfter(5, schedule, t0));
        Assert.Equal(t0.AddDays(30), RetrySchedule.ComputeRetryAfter(99, schedule, t0));
    }

    [Fact]
    public void ParsesCustomScheduleAndIgnoresGarbage()
    {
        Assert.Equal(new[] { 2, 5 }, RetrySchedule.Parse("2, 5"));
        Assert.Equal(new[] { 4 }, RetrySchedule.Parse("4,x,-3,0"));
        Assert.Equal(new[] { 1, 3, 7, 30 }, RetrySchedule.Parse(""));
        Assert.Equal(new[] { 1, 3, 7, 30 }, RetrySchedule.Parse(null));
        Assert.Equal(new[] { 1, 3, 7, 30 }, RetrySchedule.Parse(",,"));
    }
}
