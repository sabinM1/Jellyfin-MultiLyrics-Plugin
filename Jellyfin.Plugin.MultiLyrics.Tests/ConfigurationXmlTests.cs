using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.MultiLyrics.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

/// <summary>
/// Configuration files written by an earlier build must keep loading. The host deserializes them
/// with <see cref="XmlSerializer"/>, which throws on an undefined enum member but silently skips
/// elements it does not know - that is what makes it safe to retire a setting without wiping the
/// rest of a user's configuration.
/// </summary>
public class ConfigurationXmlTests
{
    private const string LegacyXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration>
            <SourceOrder>Genius,LrcLib,LyricsOvh,Tekstowo</SourceOrder>
            <EnableLrcLib>true</EnableLrcLib>
            <EnableLyricsOvh>false</EnableLyricsOvh>
            <EnableTekstowo>true</EnableTekstowo>
            <EnableGenius>true</EnableGenius>
            <GeniusToSAcknowledged>true</GeniusToSAcknowledged>
            <GeniusToken>legacy-token</GeniusToken>
            <GeniusMode>SearchOnly</GeniusMode>
            <DurationToleranceSeconds>25</DurationToleranceSeconds>
            <WritePolicy>MetadataFolder</WritePolicy>
            <OverwritePolicy>Always</OverwritePolicy>
            <MaxTracksPerRun>50</MaxTracksPerRun>
        </PluginConfiguration>
        """;

    private const string EmptyXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><PluginConfiguration />";

    private static PluginConfiguration Load(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    [Fact]
    public void LegacyConfigWithRetiredSettingsStillLoads()
    {
        var config = Load(LegacyXml);

        Assert.Equal("Genius,LrcLib,LyricsOvh,Tekstowo", config.SourceOrder);
        Assert.True(config.EnableGenius);
        Assert.True(config.GeniusToSAcknowledged);
        Assert.Equal("legacy-token", config.GeniusToken);
        Assert.False(config.EnableLyricsOvh);
        Assert.Equal(25, config.DurationToleranceSeconds);

        // The host writes enums by name, so an older file says SearchOnly. The member stays in
        // the enum for exactly that reason - XmlSerializer throws on a name it cannot resolve, and
        // that would lose every other setting in the file. Nothing acts on the value any more.
        Assert.Equal(GeniusMode.SearchOnly, config.GeniusMode);
    }

    [Fact]
    public void MissingElementsFallBackToDefaults()
    {
        var config = Load(EmptyXml);

        Assert.Equal(Constants.DefaultSourceOrder, config.SourceOrder);
        Assert.True(config.EnableLrcLib);
        Assert.False(config.EnableGenius);
        Assert.True(config.PreferSyncedLyrics);
        Assert.Equal(GeniusMode.Off, config.GeniusMode);
    }
}
