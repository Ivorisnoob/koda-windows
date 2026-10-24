using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koda.Core;

/// <summary>How the app looks: the system's, or the listener's.</summary>
public enum Theme
{
    System,
    Light,
    Dark,
}

/// <summary>How much of the songs kept from listening may take; downloads don't count.</summary>
public enum CacheLimit : long
{
    Small = 500_000_000,
    Standard = 1_000_000_000,
    Large = 2_000_000_000,
    Huge = 5_000_000_000,
}

/// <summary>How the saved collections are ordered.</summary>
public enum LibrarySort
{
    Recent,
    Title,
    Artist,
}

/// <summary>How Liked Songs is ordered.</summary>
public enum SongSort
{
    Recent,
    Title,
    Artist,
    MostPlayed,
}

/// <summary>
/// What the listener has chosen, each saved the moment it changes.
///
/// The same choices the Mac app makes, with the same names and the same defaults:
/// the theme, the drifting player colours, the lyrics services and how they behave,
/// what is remembered, and how much is kept on disk. The Windows-only ones (closing to
/// the tray, Koda's own volume) live here too.
///
/// The settings belong to the engine, not to the shell: the engine reads them - the
/// lyrics services, the cache limit, the history - and the shell writes them.
/// </summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<Theme>(),
            new JsonStringEnumConverter<CacheLimit>(),
            new JsonStringEnumConverter<LyricsSource>(),
            new JsonStringEnumConverter<LibrarySort>(),
            new JsonStringEnumConverter<SongSort>(),
        },
    };

    private static Settings? _current;

    /// <summary>The settings of this install.</summary>
    public static Settings Current => _current ??= Load();

    /// <summary>Where the settings live.</summary>
    public static string Path => System.IO.Path.Combine(DataPaths.Root, "settings.json");

    /// <summary>How the app looks.</summary>
    public Theme Theme { get; set; } = Theme.System;

    /// <summary>The cover's colours drift behind Now Playing; off, they hold still.</summary>
    public bool AnimatesPlayerBackground { get; set; } = true;

    /// <summary>Every lyrics service, best first; the order decides which answer wins.</summary>
    public List<LyricsSource> LyricsSourceOrder { get; set; } =
        [LyricsSource.YouLyPlus, LyricsSource.BetterLyrics, LyricsSource.Lrclib];

    /// <summary>The services the listener turned off.</summary>
    public List<LyricsSource> DisabledLyricsSources { get; set; } = [];

    /// <summary>Words rise as they start and held notes swell.</summary>
    public bool LyricsMotion { get; set; } = true;

    /// <summary>The size of a sung line, in effective pixels; backing vocals follow at 70%.
    /// Kept between 14 and 32, the slider's ends.</summary>
    public double LyricsFontSize
    {
        get => _lyricsFontSize;
        set => _lyricsFontSize = Math.Clamp(value, 14, 32);
    }

    private double _lyricsFontSize = 20;

    /// <summary>The old three-step size, read once from an earlier settings file and never
    /// written: Small and Large keep their place either side of the default.</summary>
    [JsonPropertyName("lyricsSize")]
    public string? LegacyLyricsSize
    {
        get => null;
        set => _lyricsFontSize = value switch
        {
            "Small" => 16,
            "Large" => 24,
            _ => _lyricsFontSize,
        };
    }

    /// <summary>Backing vocals are shown smaller, under their line.</summary>
    public bool ShowsBackingVocals { get; set; } = true;

    /// <summary>The second singer of a duet on the right; off, every line sits on the left.</summary>
    public bool DuetSides { get; set; } = true;

    /// <summary>Off, nothing played is added to history, Quick picks or stats.</summary>
    public bool SavesListeningHistory { get; set; } = true;

    /// <summary>Off, what is searched for is not remembered.</summary>
    public bool SavesSearchHistory { get; set; } = true;

    /// <summary>How much of the songs kept from listening may take.</summary>
    public CacheLimit CacheLimit { get; set; } = CacheLimit.Standard;

    /// <summary>When the queue ends, the radio of the last song keeps it going.</summary>
    public bool Autoplay { get; set; } = true;

    /// <summary>Buttons beside previous and next jump back or ahead within the song.</summary>
    public bool ShowsSkipButtons { get; set; } = true;

    /// <summary>How far those buttons jump. Only the four the settings offer are kept;
    /// anything else reads as ten seconds, the Mac's default.</summary>
    public int SkipSeconds
    {
        get => _skipSeconds;
        set => _skipSeconds = SkipIntervals.Contains(value) ? value : 10;
    }

    public static readonly int[] SkipIntervals = [5, 10, 15, 30];

    private int _skipSeconds = 10;

    /// <summary>Closing the window keeps Koda in the notification area, still playing.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Koda's own volume, under the system's.</summary>
    public double Volume { get; set; } = 100;

    /// <summary>Loud masters are brought down to YouTube's -14 LUFS target, so songs play at
    /// a similar volume. Quiet ones are left alone rather than boosted.</summary>
    public bool NormalizesVolume { get; set; } = true;

    /// <summary>Songs overlap as one ends and the next begins, the old fading out as the new
    /// fades in. Skips get a short fade too, so changing songs never cuts off mid-note.</summary>
    public bool Crossfades { get; set; } = true;

    /// <summary>How long songs overlap, in seconds; kept between 1 and 12, as on the Mac.</summary>
    public double CrossfadeSeconds
    {
        get => _crossfadeSeconds;
        set => _crossfadeSeconds = double.IsFinite(value) ? Math.Clamp(Math.Round(value), 1, 12) : 5;
    }

    private double _crossfadeSeconds = 5;

    /// <summary>The playing song on the listener's Discord profile, through the Discord app on this PC.</summary>
    public bool SharesOnDiscord { get; set; }

    /// <summary>A notification with the song while Koda is in the background.</summary>
    public bool ShowsSongNotifications { get; set; } = true;

    /// <summary>How saved albums, playlists and artists are ordered in the library.</summary>
    public LibrarySort LibrarySort { get; set; } = LibrarySort.Recent;

    /// <summary>How Liked Songs is ordered.</summary>
    public SongSort SongSort { get; set; } = SongSort.Recent;

    /// <summary>Koda starts with Windows, so the music is a click away after sign-in.</summary>
    public bool OpenAtLogin { get; set; }

    /// <summary>The last window position and size, in physical pixels: where the listener
    /// left Koda. Zero width or height means no placement has been saved yet.</summary>
    public int WindowLeft { get; set; }

    public int WindowTop { get; set; }

    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    /// <summary>The window was maximised when it was last closed.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>The audio render device Koda plays through; null is the system default.</summary>
    public string? AudioDeviceId { get; set; }

    /// <summary>Raised when a setting changes, so what listens to it can follow.</summary>
    public event Action<Settings>? Changed;

    /// <summary>The services to ask, best first.</summary>
    [JsonIgnore]
    public IReadOnlyList<LyricsSource> LyricsSources =>
        LyricsSourceOrder.Where(s => !DisabledLyricsSources.Contains(s)).ToList();

    /// <summary>Whether a service is on.</summary>
    public bool IsEnabled(LyricsSource source) => !DisabledLyricsSources.Contains(source);

    /// <summary>Turns a service on or off. The last one cannot be turned off: the lyrics
    /// need somewhere to come from.</summary>
    public void SetEnabled(LyricsSource source, bool on)
    {
        if (on)
        {
            DisabledLyricsSources.Remove(source);
            return;
        }

        if (LyricsSources.Count > 1)
        {
            DisabledLyricsSources.Add(source);
        }
    }

    /// <summary>Changes a setting and writes the file.</summary>
    public void Update(Action<Settings> change)
    {
        change(this);
        Save();
        Changed?.Invoke(this);
    }

    /// <summary>Writes the settings to their file.</summary>
    public void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Settings that cannot be written are settings that keep their defaults.
        }
    }

    /// <summary>Reads the settings, falling back to the defaults. The path is a parameter so
    /// a file can be read from somewhere else (the old shell-settings.json, or a test).</summary>
    public static Settings Load(string? path = null)
    {
        try
        {
            // The old name is only a fallback for the default path, not for a file asked for.
            var fallback = path is null;
            path ??= Path;
            if (!File.Exists(path) && fallback)
            {
                path = System.IO.Path.Combine(DataPaths.Root, "shell-settings.json");
            }

            if (!File.Exists(path))
            {
                return new Settings();
            }

            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options);
            if (settings is null)
            {
                return new Settings();
            }

            // Services added in a later version join at the end of a saved order.
            var known = settings.LyricsSourceOrder
                .Concat([LyricsSource.YouLyPlus, LyricsSource.BetterLyrics, LyricsSource.Lrclib])
                .Distinct()
                .ToList();
            settings.LyricsSourceOrder = known;
            return settings;
        }
        catch (Exception)
        {
            return new Settings();
        }
    }
}
