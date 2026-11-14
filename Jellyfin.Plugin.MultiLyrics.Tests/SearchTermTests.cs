using Jellyfin.Plugin.MultiLyrics.Core;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

public class SearchTermTests
{
    [Theory]
    [InlineData("Carnage (feat. Little Simz) (Original Mix)", "Carnage")]
    [InlineData("Carnage (feat. Little Simz)", "Carnage")]
    [InlineData("Song (Remastered 2011)", "Song")]
    [InlineData("Song [Explicit]", "Song")]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song (2011)", "Song")]
    [InlineData("Song (Part 2)", "Song (Part 2)")]
    [InlineData("Song (Live)", "Song (Live)")]
    [InlineData("Song (Acoustic Version)", "Song (Acoustic Version)")]
    public void SearchTitleDropsOnlyReleaseQualifiers(string input, string expected)
    {
        Assert.Equal(expected, TrackMatcher.SearchTitle(input));
    }

    [Fact]
    public void SearchTitleKeepsSomethingWhenTheTitleIsOnlyQualifiers()
    {
        Assert.Equal("(Original Mix)", TrackMatcher.SearchTitle("(Original Mix)"));
        Assert.Equal(string.Empty, TrackMatcher.SearchTitle(null));
    }

    [Theory]
    [InlineData("Chase & Status/ Stefflon Don", "Chase & Status")]
    [InlineData("Chase & Status; Little Simz", "Chase & Status")]
    [InlineData("AC/DC", "AC/DC")]
    [InlineData("Artist feat. Guest", "Artist")]
    public void SearchArtistKeepsTheFirstAct(string input, string expected)
    {
        Assert.Equal(expected, TrackMatcher.SearchArtist(input));
    }
}
