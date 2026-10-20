using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

public class QueueTests
{
    [Fact]
    public void RadioQueueStartsWithTheSeed()
    {
        var queue = Pages.Queue(Fixtures.Load("next-song.json"));

        Assert.Equal("J7p4bzqLvCw", queue.Songs[0].Id);
        Assert.Equal("Blinding Lights", queue.Songs[0].Title);
        Assert.Equal("I6Z2fqEVbPQ", queue.Songs[1].Id);
        Assert.Equal("São Paulo", queue.Songs[1].Title);
        Assert.Equal(50, queue.Songs.Length);
        Assert.NotNull(queue.Continuation);
        Assert.All(queue.Songs, s => Assert.False(s.IsVideo));
        Assert.All(queue.Songs, s => Assert.NotEqual("", s.ArtistLine));
    }

    [Fact]
    public void EmptyRootParsesAsEmptyQueue()
    {
        var queue = Pages.Queue(Json.Parse("{}"));
        Assert.Empty(queue.Songs);
        Assert.Null(queue.Continuation);
    }
}
