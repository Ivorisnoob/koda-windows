using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>
/// Links pasted into search: the songs, albums, playlists and artists that YouTube,
/// YouTube Music and koda:// addresses point at.
/// </summary>
public class YouTubeLinkTests
{
    private static YouTubeLink? Link(string text) => YouTubeLink.Parse(text);

    [Fact]
    public void LinksToSongs()
    {
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=abc"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://youtu.be/dQw4w9WgXcQ?t=42"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://m.youtube.com/shorts/dQw4w9WgXcQ"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://www.youtube.com/live/dQw4w9WgXcQ"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://www.youtube.com/embed/dQw4w9WgXcQ"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("koda://watch?v=dQw4w9WgXcQ"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("HTTPS://MUSIC.YOUTUBE.COM/watch?v=dQw4w9WgXcQ"));
    }

    [Fact]
    public void LinksToCollections()
    {
        Assert.Equal(new YouTubeLink.Playlist("PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"),
            Link("https://music.youtube.com/playlist?list=PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"));
        Assert.Equal(new YouTubeLink.Playlist("OLAK5uy_abc"),
            Link("https://music.youtube.com/playlist?list=OLAK5uy_abc"));
        Assert.Equal(new YouTubeLink.Playlist("PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"),
            Link("https://music.youtube.com/watch?list=PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"));
        Assert.Equal(new YouTubeLink.Playlist("PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"),
            Link("https://music.youtube.com/watch?v=short&list=PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"));
        Assert.Equal(new YouTubeLink.Album("MPREb_4pL8gzRtw1p"),
            Link("https://music.youtube.com/browse/MPREb_4pL8gzRtw1p"));
        Assert.Equal(new YouTubeLink.Artist("UCbulh9WdLtEXiooRcYK7SWw"),
            Link("https://music.youtube.com/channel/UCbulh9WdLtEXiooRcYK7SWw"));
        Assert.Equal(new YouTubeLink.Artist("UCbulh9WdLtEXiooRcYK7SWw"),
            Link("https://music.youtube.com/browse/UCbulh9WdLtEXiooRcYK7SWw"));
        Assert.Equal(new YouTubeLink.Playlist("PL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"),
            Link("https://music.youtube.com/browse/VLPL4fGSI1pDJn6puJdseH2Rt9sMvt9E2M4i"));
        Assert.Equal(new YouTubeLink.Album("MPREb_4pL8gzRtw1p"),
            Link("koda://browse/MPREb_4pL8gzRtw1p"));
    }

    [Fact]
    public void RejectsWhatHasNoPage()
    {
        Assert.Null(Link("https://music.youtube.com/watch?list=RDAMVMdQw4w9WgXcQ"));
        Assert.Null(Link("https://example.com/watch?v=dQw4w9WgXcQ"));
        Assert.Null(Link("https://music.youtube.com/"));
        Assert.Null(Link("https://youtu.be/short"));
        Assert.Null(Link("https://music.youtube.com/watch?v=dQw4w9WgXc"));
        Assert.Null(Link("https://music.youtube.com/watch?v=dQw4w9WgXcQQ"));
        Assert.Null(Link("https://music.youtube.com/watch?v=dQw4w9WgXc!"));
        Assert.Null(Link("https://music.youtube.com/browse/FOO"));
        Assert.Null(Link("https://music.youtube.com/channel/XYZ"));
        Assert.Null(Link("koda://browse/FOO"));
        Assert.Null(Link(""));
        Assert.Null(Link("the weeknd"));
    }

    [Fact]
    public void FindsALinkInText()
    {
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("listen to this https://youtu.be/dQw4w9WgXcQ !!"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("https://example.com/watch?v=dQw4w9WgXcQ then https://youtu.be/dQw4w9WgXcQ"));
        Assert.Equal(new YouTubeLink.Song("dQw4w9WgXcQ"),
            Link("(https://youtu.be/dQw4w9WgXcQ)."));
        Assert.Null(Link("the weeknd"));
    }

    [Fact]
    public void TitlesSayWhatTheLinkDoes()
    {
        Assert.Equal("Play Song from Link", new YouTubeLink.Song("dQw4w9WgXcQ").OpenTitle);
        Assert.Equal("Open Album from Link", new YouTubeLink.Album("MPREb_x").OpenTitle);
        Assert.Equal("Open Playlist from Link", new YouTubeLink.Playlist("PLx").OpenTitle);
        Assert.Equal("Open Artist from Link", new YouTubeLink.Artist("UCx").OpenTitle);
    }
}
