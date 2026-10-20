using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>
/// The settings: what the listener chose, kept in one file, with defaults that hold
/// when the file is missing or damaged.
/// </summary>
public class SettingsTests
{
    [Fact]
    public void ADamagedFileLeavesTheDefaults()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-broken-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ this is not json");

        var settings = Settings.Load(file);

        Assert.True(settings.CloseToTray);
        Assert.True(settings.Autoplay);
        Assert.Equal(100, settings.Volume);
        Assert.Equal(3, settings.LyricsSources.Count);
    }

    [Fact]
    public void AMissingFileLeavesTheDefaults()
    {
        var settings = Settings.Load(Path.Combine(Fixtures.Scratch, $"settings-gone-{Guid.NewGuid():N}.json"));

        Assert.True(settings.CloseToTray);
        Assert.Equal(100, settings.Volume);
    }

    [Fact]
    public void TheChoicesSurviveASaveAndLoad()
    {
        var settings = new Settings
        {
            CloseToTray = false,
            Autoplay = false,
            Volume = 42,
            LyricsMotion = false,
            ShowsBackingVocals = false,
            DuetSides = false,
            SavesListeningHistory = false,
            SavesSearchHistory = false,
            Theme = Theme.Dark,
            LyricsSize = LyricsSize.Large,
            CacheLimit = CacheLimit.Large,
            DisabledLyricsSources = [LyricsSource.BetterLyrics],
        };

        var text = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(text);

        Assert.NotNull(back);
        Assert.False(back!.CloseToTray);
        Assert.False(back.Autoplay);
        Assert.Equal(42, back.Volume);
        Assert.False(back.LyricsMotion);
        Assert.False(back.ShowsBackingVocals);
        Assert.False(back.DuetSides);
        Assert.False(back.SavesListeningHistory);
        Assert.False(back.SavesSearchHistory);
        Assert.Equal(Theme.Dark, back.Theme);
        Assert.Equal(LyricsSize.Large, back.LyricsSize);
        Assert.Equal(CacheLimit.Large, back.CacheLimit);
        Assert.False(back.IsEnabled(LyricsSource.BetterLyrics));
        Assert.True(back.IsEnabled(LyricsSource.YouLyPlus));
    }

    [Fact]
    public void TheLastServiceCannotBeTurnedOff()
    {
        var settings = new Settings();

        // The lyrics need somewhere to come from.
        settings.SetEnabled(LyricsSource.YouLyPlus, false);
        settings.SetEnabled(LyricsSource.BetterLyrics, false);
        settings.SetEnabled(LyricsSource.Lrclib, false);

        Assert.Single(settings.LyricsSources);
        Assert.True(settings.IsEnabled(LyricsSource.Lrclib));
    }

    [Fact]
    public void TheServicesAreAskedInTheOrderTheyWereSaved()
    {
        var settings = new Settings
        {
            LyricsSourceOrder = [LyricsSource.Lrclib, LyricsSource.YouLyPlus, LyricsSource.BetterLyrics],
            DisabledLyricsSources = [LyricsSource.YouLyPlus],
        };

        Assert.Equal([LyricsSource.Lrclib, LyricsSource.BetterLyrics], settings.LyricsSources);
    }

    [Fact]
    public void AServiceAddedLaterJoinsTheEndOfTheOrder()
    {
        // A file saved before a service existed still asks for it.
        var file = Path.Combine(Fixtures.Scratch, $"settings-order-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "lyricsSourceOrder": [ "Lrclib" ] }""");

        var settings = Settings.Load(file);

        Assert.Equal(3, settings.LyricsSourceOrder.Count);
        Assert.Equal(LyricsSource.Lrclib, settings.LyricsSourceOrder[0]);
    }
}
