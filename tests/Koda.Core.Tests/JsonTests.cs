using Koda.Core;

namespace Koda.Core.Tests;

public class JsonTests
{
    private const string Sample = """
        {
            "a": {"text": "hi"},
            "list": [1, "two", {"text": "three"}],
            "runs": [{"text": "A"}, {"text": "B"}],
            "simple": {"simpleText": "flat"},
            "deep": {"mid": {"target": {"id": "UC1"}}},
            "num": "42",
            "big": "9007199254740993",
            "flag": true,
            "pad": "   ",
            "thumb": {"over": {"overlay": {"thumbnails": [{"url": "//host/wrong", "width": 1}]}},
                      "own": {"thumbnails": [{"url": "//host/small", "width": 60}, {"url": "https://host/big", "width": 544}]}}
        }
        """;

    private static Json Root => Json.Parse(Sample);

    [Fact]
    public void MissingKeysReadAsNull()
    {
        Assert.False(Root["nope"].Exists);
        Assert.False(Root[99].Exists);
        Assert.Null(Root["list"]["text"].String);
        Assert.Equal(Json.Null, Json.Parse("{}")["whatever"]);
    }

    [Fact]
    public void ObjectsWalksWholeTreeInDocumentOrderWithoutDescendingIntoMatches()
    {
        var doc = Json.Parse("""{"x": {"text": {"text": "inner"}}, "y": {"text": {"id": 1}}}""");
        var found = doc.Objects("text");
        Assert.Equal(2, found.Length); // the nested "text" under the match is NOT repeated
        Assert.Equal("inner", found[0]["text"].String);
        Assert.Equal(1, found[1]["id"].Int);
    }

    [Fact]
    public void FirstReturnsFirstObjectUnderKeyAnywhere()
    {
        Assert.Equal("UC1", Root.First("target")["id"].String);
        Assert.False(Root.First("absent").Exists);
    }

    [Fact]
    public void StringsCollectsEveryStringUnderAKey()
    {
        // Document order: "a" first (k=1), then the array items, then root k.
        var doc = Json.Parse("""{"a": {"k": "1"}, "b": [{"k": "2"}, {"k": 3}], "k": "top"}""");
        Assert.Equal(["1", "2", "top"], doc.Strings("k"));
    }

    [Fact]
    public void TextPrefersSimpleTextThenRuns()
    {
        // Text reads the runs stored ON this node (the root here).
        Assert.Equal("AB", Root.Text());
        Assert.Equal("flat", Root["simple"].Text());
        Assert.Equal("", Root["a"].Text()); // no runs, no simpleText
    }

    [Fact]
    public void BestThumbnailSkipsOverlaysAndPicksWidest()
    {
        var best = Root.BestThumbnail();
        Assert.NotNull(best);
        Assert.Equal(544, best!.Width);
        Assert.Equal("https://host/big", best.Url.ToString());
    }

    [Fact]
    public void NumbersReadFromStringsToo()
    {
        Assert.Equal(42, Root["num"].Int);
        Assert.Equal(42d, Root["num"].Double);
        Assert.Equal(9007199254740993L, Root["big"].Long);
        Assert.True(Root["flag"].Bool);
        Assert.Null(Root["num"].Bool);
    }

    [Fact]
    public void NonEmptyTrimsAndNullsWhitespace()
    {
        Assert.Equal("hi", Root["a"]["text"].NonEmpty);
        Assert.Null(Root["pad"].NonEmpty);
        Assert.Null(Root["pad"].String is { } s && s.Trim().Length == 0 ? Root["pad"].NonEmpty : null);
    }

    [Fact]
    public void ProtocolsRelativeImageUrlsGetHttps()
    {
        var url = Urls.Image("//host/img");
        Assert.NotNull(url);
        Assert.Equal("https://host/img", url!.ToString());
        Assert.Null(Urls.Image("not a url"));
    }

    [Fact]
    public void EqualityTracksRawShape()
    {
        var a = Json.Parse("""{"same": 1}""");
        var b = Json.Parse("""{ "same" : 1 }""");
        Assert.Equal(a, b);
        Assert.NotEqual(a, Json.Parse("""{"same": 2}"""));
        Assert.Equal(Json.Null, default(Json));
    }
}
