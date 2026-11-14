using System.Text;
using Jellyfin.Plugin.MultiLyrics.Core;
using Jellyfin.Plugin.MultiLyrics.Sources;
using Xunit;

namespace Jellyfin.Plugin.MultiLyrics.Tests;

/// <summary>
/// Guards the Genius search payload mapping. The payload uses lower-case keys and snake_case for
/// primary_artist; without explicit names nothing binds, every hit is rejected and the source
/// silently returns nothing for tracks that Genius does have.
/// </summary>
public class GeniusSearchTests
{
    private const string SearchPayload = """
        {
          "meta": { "status": 200 },
          "response": {
            "hits": [
              {
                "highlights": [],
                "index": "song",
                "type": "song",
                "result": {
                  "annotation_count": 12,
                  "api_path": "/songs/1",
                  "full_title": "Bad by Michael Jackson",
                  "id": 1,
                  "lyrics_state": "complete",
                  "path": "/Michael-jackson-bad-lyrics",
                  "primary_artist": {
                    "api_path": "/artists/2",
                    "id": 2,
                    "name": "Michael Jackson",
                    "url": "https://genius.com/artists/Michael-jackson"
                  },
                  "title": "Bad",
                  "url": "https://genius.com/Michael-jackson-bad-lyrics"
                }
              }
            ]
          }
        }
        """;

    private static TrackQuery Query(string artist, string title) => new()
    {
        Artist = artist,
        Title = title
    };

    [Fact]
    public void FindsHitInRealShapedPayload()
    {
        var url = GeniusSource.SelectHitUrl(
            Encoding.UTF8.GetBytes(SearchPayload),
            Query("Michael Jackson", "Bad"),
            out var hitCount);

        Assert.Equal(1, hitCount);
        Assert.Equal("https://genius.com/Michael-jackson-bad-lyrics", url);
    }

    [Fact]
    public void RejectsHitFromAnotherArtist()
    {
        var url = GeniusSource.SelectHitUrl(
            Encoding.UTF8.GetBytes(SearchPayload),
            Query("Some Other Band", "Bad"),
            out var hitCount);

        Assert.Null(url);
        Assert.Equal(1, hitCount);
    }

    [Fact]
    public void RejectsHitWithAnotherTitle()
    {
        Assert.Null(GeniusSource.SelectHitUrl(
            Encoding.UTF8.GetBytes(SearchPayload),
            Query("Michael Jackson", "Smooth Criminal"),
            out _));
    }

    [Fact]
    public void ReturnsNullWhenThereAreNoHits()
    {
        const string empty = """{ "meta": { "status": 200 }, "response": { "hits": [] } }""";

        Assert.Null(GeniusSource.SelectHitUrl(Encoding.UTF8.GetBytes(empty), Query("Michael Jackson", "Bad"), out var hitCount));
        Assert.Equal(0, hitCount);
    }

    [Fact]
    public void ReleaseQualifiersOnTheTrackTitleDoNotBreakTheMatch()
    {
        // The exact shape seen live: Jellyfin carries the release qualifiers, Genius does not.
        const string payload = """
            {
              "meta": { "status": 200 },
              "response": { "hits": [ { "result": {
                "title": "Carnage (feat. Little Simz)",
                "full_title": "Carnage (feat. Little Simz) by Chase & Status",
                "url": "https://genius.com/Chase-and-status-carnage-lyrics",
                "primary_artist": { "name": "Chase & Status" }
              } } ] }
            }
            """;

        var url = GeniusSource.SelectHitUrl(
            Encoding.UTF8.GetBytes(payload),
            Query("Chase & Status", "Carnage (feat. Little Simz) (Original Mix)"),
            out var hitCount);

        Assert.Equal(1, hitCount);
        Assert.Equal("https://genius.com/Chase-and-status-carnage-lyrics", url);
    }

    [Fact]
    public void ArtistNamesAsASingleStringIsAccepted()
    {
        // api.genius.com documents artist_names as one joined string, and a collab page is often
        // owned by an act that is not the track artist. Mapping it as an array silently made
        // every such search "0 hits".
        const string payload = """
            {
              "meta": { "status": 200 },
              "response": { "hits": [ { "result": {
                "title": "Carnage",
                "full_title": "Carnage by Alpha & Beta Crew",
                "url": "https://genius.com/Alpha-carnage-lyrics",
                "artist_names": "Alpha & Beta Crew",
                "primary_artist": { "name": "Alpha" }
              } } ] }
            }
            """;

        var url = GeniusSource.SelectHitUrl(
            Encoding.UTF8.GetBytes(payload),
            Query("Beta Crew", "Carnage"),
            out var hitCount);

        Assert.Equal(1, hitCount);
        Assert.Equal("https://genius.com/Alpha-carnage-lyrics", url);
    }

    [Fact]
    public void ArtistNamesAsAnArrayIsAlsoAccepted()
    {
        const string payload = """
            {
              "meta": { "status": 200 },
              "response": { "hits": [ { "result": {
                "title": "Carnage",
                "url": "https://genius.com/Alpha-carnage-lyrics",
                "artist_names": [ "Alpha", "Beta Crew" ],
                "primary_artist": { "name": "Alpha" }
              } } ] }
            }
            """;

        var url = GeniusSource.SelectHitUrl(
            Encoding.UTF8.GetBytes(payload),
            Query("Beta Crew", "Carnage"),
            out var hitCount);

        Assert.Equal(1, hitCount);
        Assert.Equal("https://genius.com/Alpha-carnage-lyrics", url);
    }

    [Fact]
    public void FindsHitInPublicSearchPayload()
    {
        // Shape of https://genius.com/api/search/multi, used when no client access token is
        // configured: hits are grouped in sections instead of one top-level list.
        const string payload = """
            { "response": { "sections": [
              { "type": "song", "hits": [
                { "type": "song", "result": {
                    "title": "Bad",
                    "full_title": "Bad by Michael Jackson",
                    "url": "https://genius.com/Michael-jackson-bad-lyrics",
                    "artist_names": "Michael Jackson",
                    "primary_artist": { "name": "Michael Jackson" }
                } } ] },
              { "type": "artist", "hits": [] }
            ] } }
            """;

        var url = GeniusSource.SelectPublicHitUrl(
            Encoding.UTF8.GetBytes(payload),
            Query("Michael Jackson", "Bad"),
            out var hitCount);

        Assert.Equal(1, hitCount);
        Assert.Equal("https://genius.com/Michael-jackson-bad-lyrics", url);
    }

    [Fact]
    public void PublicSearchRejectsHitFromAnotherArtist()
    {
        const string payload = """
            { "response": { "sections": [ { "type": "song", "hits": [
              { "type": "song", "result": {
                  "title": "Bad",
                  "url": "https://genius.com/Michael-jackson-bad-lyrics",
                  "primary_artist": { "name": "Michael Jackson" }
              } } ] } ] } }
            """;

        Assert.Null(GeniusSource.SelectPublicHitUrl(
            Encoding.UTF8.GetBytes(payload),
            Query("Some Other Band", "Bad"),
            out var hitCount));
        Assert.Equal(1, hitCount);
    }

    [Fact]
    public void PublicSearchReturnsNullWhenSectionsAreEmpty()
    {
        const string payload = """{ "response": { "sections": [ { "type": "song", "hits": [] } ] } }""";

        Assert.Null(GeniusSource.SelectPublicHitUrl(Encoding.UTF8.GetBytes(payload), Query("Michael Jackson", "Bad"), out var hitCount));
        Assert.Equal(0, hitCount);
    }

    [Fact]
    public void PublicSearchReturnsNullOnMalformedJson()
    {
        Assert.Null(GeniusSource.SelectPublicHitUrl(Encoding.UTF8.GetBytes("{ not json"), Query("Michael Jackson", "Bad"), out _));
    }

    [Fact]
    public void ReturnsNullOnMalformedJson()
    {
        Assert.Null(GeniusSource.SelectHitUrl(Encoding.UTF8.GetBytes("{ not json"), Query("Michael Jackson", "Bad"), out _));
    }
}
