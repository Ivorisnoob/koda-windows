using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace Koda;

/// <summary>
/// The app: where the data lives, what the listener has chosen, and the window.
///
/// The settings live in the engine (<see cref="Koda.Core.Settings"/>) so the engine
/// can read them too - the lyrics services, the cache limit, the history. The shell
/// only writes them.
/// </summary>
public partial class App : Application
{
    public static MainWindow? Shell { get; private set; }

    /// <summary>Where the app keeps its state: the engine's path, one source of truth.</summary>
    public static string DataDirectory { get; } = Koda.Core.DataPaths.Root;

    /// <summary>The audio byte store under the data root's audio folder - the CachingLoader
    /// transport keeps every byte that streams through, so a song played before starts from the file.</summary>
    public static Koda.Core.Storage.AudioStore AudioStore { get; } =
        new(Path.Combine(DataDirectory, "audio"))
        {
            // How much is kept for songs played from the network is the listener's choice.
            CacheLimit = (long)Koda.Core.Settings.Current.CacheLimit,
        };

    /// <summary>Songs the listener downloaded, and the work of fetching them, two at a time.</summary>
    public static Koda.Core.Storage.Downloads Downloads { get; } = new(
        AudioStore,
        (id, cancellation) => Koda.Services.EngineHost.Music.StreamAsync(id, cancellation),
        id => Koda.Services.EngineHost.Music.StreamFailedAsync(id));

    /// <summary>Scrobbling to Last.fm: the queue in the data root, the sign-in in the Windows
    /// Credential Manager, so neither a copied data folder nor a backup carries the account.</summary>
    public static Koda.Core.LastFm.LastFm LastFm { get; } =
        new(DataDirectory, new Koda.Interop.CredentialStore("Koda/Last.fm"));

    /// <summary>The playing song on the listener's Discord profile.</summary>
    public static Koda.Core.Discord.DiscordPresence Discord { get; } = new();

    /// <summary>Likes, saved collections, history, stats: library.json in the data root.</summary>
    public static Koda.Core.Library Library { get; } = new(DataDirectory);

    /// <summary>The YouTube Music account: the session in the Windows Credential Manager, and
    /// the library's sync with it.</summary>
    public static Koda.Core.YouTubeAccount Account => Koda.Services.EngineHost.Account;

    /// <summary>Close-to-tray: shipped ON; the settings file holds the listener's choice.</summary>
    public static bool CloseToTray => Koda.Core.Settings.Current.CloseToTray;

    /// <summary>App volume 0..100, remembered across launches with the shell settings.</summary>
    public static double Volume => Koda.Core.Settings.Current.Volume;

    /// <summary>The listener's settings.</summary>
    public static Koda.Core.Settings Settings => Koda.Core.Settings.Current;

    public App()
    {
        // Before any window exists: the shell reads the app identity when the
        // media card binds, and an unnamed card says "Unknown app".
        Koda.Interop.AppIdentity.Register();

        InitializeComponent();

        // The toast channel registers before any activation is read: a notification
        // clicked while Koda was closed arrives through NotificationInvoked.
        Koda.Interop.Notifications.Initialize();

        // A crash leaves its real reason in the crash log (the Windows event log only shows 0xc000027b).
        UnhandledException += (_, e) => WriteCrash($"{e.Message}\n{e.Exception}");
        DebugSettings.XamlResourceReferenceFailed += (_, e) => WriteCrash($"resource: {e.Message}");
        DebugSettings.BindingFailed += (_, e) => System.Diagnostics.Debug.WriteLine(e.Message);
    }

    public static void SetCloseToTray(bool value) =>
        Settings.Update(s => s.CloseToTray = value);

    public static void SetVolume(double value) =>
        Settings.Update(s => s.Volume = Math.Clamp(value, 0, 100));

    private static void WriteCrash(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDirectory, "crash.txt"), $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{text}\n");
        }
        catch (Exception)
        {
            // Nothing more to do while going down.
        }
    }

    /// <summary>A failure the shell caught and carried on from: the page, the file, the reason.</summary>
    internal static void WriteError(string source, Exception error) => WriteCrash($"{source}\n{error}");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Shell integration that must exist before the first window: the Run entry
        // follows the listener's setting, and koda:// links point at this build.
        Koda.Interop.Startup.Sync();
        Koda.Interop.ProtocolActivation.Register();

        Shell = new MainWindow();
        Shell.Activate();
        Koda.Helpers.ShellChrome.ApplyTheme();

        // Downloads the last session left unfinished carry on, now the window is up.
        Downloads.Start();

        // Last.fm hears what plays: a play is announced when its sound starts and queued once
        // enough of it was heard. Pausing listening history pauses scrobbling too.
        var playback = Koda.Playback.PlaybackService.Instance;
        playback.ItemStarted += item => LastFm.Started(item.Id, item.Song);
        playback.ItemHeard += (item, seconds) => LastFm.Heard(seconds, item.Id, item.Song);
        LastFm.IsPaused = !Settings.SavesListeningHistory;
        Settings.Changed += settings => LastFm.IsPaused = !settings.SavesListeningHistory;
        LastFm.SyncSoon();

        // The YouTube Music account: the saved session signs requests, the library follows the
        // account, and a play heard for a while goes to the account's YouTube history.
        var account = Koda.Services.EngineHost.Account;
        playback.ItemHeard += (item, seconds) =>
            account.ItemHeard(item.Id, item.Song.Id, item.Song.Duration, seconds);
        account.Start();

        // The Discord card follows what plays; it only shows while the listener has it on and
        // listening history is on.
        void ShareOnDiscord() => Discord.Update(
            Settings.SharesOnDiscord && Settings.SavesListeningHistory,
            playback.CurrentSong is { } song
                ? Koda.Core.Discord.DiscordNowPlaying.Of(song, playback.DurationSeconds, playback.PositionSeconds, playback.IsPlaying)
                : null);
        playback.TrackChanged += ShareOnDiscord;
        playback.PlayStateChanged += ShareOnDiscord;
        playback.PositionTick += ShareOnDiscord;
        Settings.Changed += _ => ShareOnDiscord();
        ShareOnDiscord();

        // A cold koda:// launch or a Jump List task carries its argument on this
        // process's command line.
        if (Koda.Interop.SingleInstance.StartupArguments.Count > 0)
        {
            HandleArguments(Shell, Koda.Interop.SingleInstance.StartupArguments);
        }

        // A second launch (taskbar, Start, Search, koda://) is redirected here by
        // Program/SingleInstance: show the window the listener already has. The
        // callback arrives on a pool thread, so everything UI happens on ours.
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Koda.Interop.SingleInstance.SetHandler(() => dispatcher.TryEnqueue(() =>
        {
            Shell?.ShowFromTray();
            var pending = Koda.Interop.SingleInstance.TakePendingArguments();
            if (pending.Length > 0 && Shell is { } shell)
            {
                HandleArguments(shell, pending);
            }
        }));
    }

    /// <summary>Shell activations that arrive as arguments: Jump List tasks and koda:// links.</summary>
    private static void HandleArguments(MainWindow shell, System.Collections.Generic.IReadOnlyList<string> args)
    {
        Koda.Interop.JumpList.Handle(shell, args);
        Koda.Interop.ProtocolActivation.Handle(shell, args);
    }
}
