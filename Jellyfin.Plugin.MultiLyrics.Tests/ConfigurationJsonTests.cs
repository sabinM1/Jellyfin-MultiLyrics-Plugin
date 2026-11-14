using System.Text.Json;
using Jellyfin.Plugin.MultiLyrics.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

public class ConfigurationJsonTests
{
    private static GeniusMode Read(string json)
        => JsonSerializer.Deserialize<PluginConfiguration>(json)!.GeniusMode;

    [Theory]
    [InlineData("{\"GeniusMode\": 2}", GeniusMode.Scrape)]
    [InlineData("{\"GeniusMode\": \"Scrape\"}", GeniusMode.Scrape)]
    [InlineData("{\"GeniusMode\": \"scrape\"}", GeniusMode.Scrape)]
    [InlineData("{\"GeniusMode\": \"2\"}", GeniusMode.Scrape)]
    [InlineData("{\"GeniusMode\": \"\"}", GeniusMode.Off)]
    [InlineData("{\"GeniusMode\": null}", GeniusMode.Off)]
    [InlineData("{\"GeniusMode\": \"nonsense\"}", GeniusMode.Off)]
    [InlineData("{\"GeniusMode\": 99}", GeniusMode.Off)]
    public void GeniusModeIsReadTolerantly(string json, GeniusMode expected)
    {
        // A blank or unknown value used to reject the whole configuration post with a 500,
        // which silently discarded every other setting on the page.
        Assert.Equal(expected, Read(json));
    }

    [Fact]
    public void GeniusModeIsWrittenAsANumber()
    {
        // The settings page maps the value onto an option; numbers always match.
        var json = JsonSerializer.Serialize(new PluginConfiguration { GeniusMode = GeniusMode.Scrape });

        Assert.Contains("\"GeniusMode\":2", json, System.StringComparison.Ordinal);
    }
}
