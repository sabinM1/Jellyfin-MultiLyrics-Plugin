using System;
using System.IO;
using Jellyfin.Plugin.MultiLyrics.Storage;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

/// <summary>
/// The retry window must not survive a change of the source configuration: a source that was
/// broken or disabled when a track was last missed would otherwise stay unqueried for up to
/// 30 days, which looks exactly like the new setting being ignored.
/// </summary>
public class SettingsStampTests : IDisposable
{
    private readonly string _path;

    public SettingsStampTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "mlstamp-" + Guid.NewGuid().ToString("N"), "cache.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FirstStampCountsAsChange()
    {
        var cache = new ResultCache(_path);

        Assert.True(cache.SourcesSettingsChanged("v1|order|toggles"));
    }

    [Fact]
    public void RepeatedStampIsNotAChange()
    {
        var cache = new ResultCache(_path);
        cache.SourcesSettingsChanged("v1|order|toggles");

        Assert.False(cache.SourcesSettingsChanged("v1|order|toggles"));
    }

    [Fact]
    public void DifferentStampCountsAsChange()
    {
        var cache = new ResultCache(_path);
        cache.SourcesSettingsChanged("v1|order|toggles");

        Assert.True(cache.SourcesSettingsChanged("v2|order|toggles"));
    }

    [Fact]
    public void StampSurvivesANewInstance()
    {
        var first = new ResultCache(_path);
        first.SourcesSettingsChanged("v1|order|toggles");

        var second = new ResultCache(_path);

        Assert.False(second.SourcesSettingsChanged("v1|order|toggles"));
    }
}
