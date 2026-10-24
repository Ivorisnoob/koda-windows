using System;
using System.Linq;
using Koda.Core;
using Koda.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Koda.Interop;

/// <summary>
/// The song's toast: what is playing, with Previous, Play/Pause and Next, shown while Koda
/// is out of sight (minimized, or closed to the tray). A window the listener can see already
/// says what is playing, so it never toasts over itself, and switching to another app is not
/// a reason to be interrupted. The notification is a Windows App SDK app
/// notification; on an unpackaged build Register() installs the COM server itself, so no
/// manifest or AUMID plumbing is needed.
///
/// The live toasts carry the "now-playing" tag, so a track change replaces the last one
/// instead of stacking a column of them. The buttons are background actions: Next while
/// the window is in the tray moves the queue without pulling the app forward.
/// </summary>
internal static class Notifications
{
    private const string SongTag = "now-playing";
    private const string SongGroup = "playback";
    private const string FaultTag = "playback-fault";
    private const string FaultGroup = "playback";

    private static DispatcherQueue? _dispatcher;
    private static MainWindow? _shell;
    private static readonly System.Collections.Generic.Queue<string> _pending = new();
    private static bool _registered;

    /// <summary>Whether Windows accepted the registration; the settings switch follows it.</summary>
    public static bool IsAvailable => _registered;

    /// <summary>The cover shown on a toast, kept as a local file: Windows reads a desktop app's
    /// toast images from disk only, never from the web.</summary>
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static string ArtFolder => System.IO.Path.Combine(App.DataDirectory, "notification-art");

    private static bool OutOfSight => _shell is not { } shell || !shell.ShowsPlayback;

    /// <summary>
    /// Claims the notification channel. Call once, on the UI thread, before the window
    /// exists: a toast clicked while Koda was closed arrives through NotificationInvoked,
    /// and the action waits here until the window is ready.
    /// </summary>
    public static void Initialize()
    {
        if (_registered)
        {
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception error)
        {
            // Notifications are a convenience: a locked-down machine still plays music.
            System.Diagnostics.Debug.WriteLine($"app notifications unavailable: {error.Message}");
        }
    }

    /// <summary>Hands the service the window, and runs a toast action that arrived first.</summary>
    public static void Attach(MainWindow shell)
    {
        _shell = shell;
        while (_pending.Count > 0)
        {
            Run(shell, _pending.Dequeue());
        }
    }

    /// <summary>The song changed while sound is playing: a toast, if the window is elsewhere.</summary>
    public static async void SongChanged(Song song, bool playing)
    {
        if (!_registered || !OutOfSight || !Settings.Current.ShowsSongNotifications)
        {
            return;
        }

        var cover = song.Artwork is { } artwork ? await LocalArtAsync(artwork.Resized(96)) : null;
        // The song may have moved on, or the window come back, while the cover came down.
        if (PlaybackService.Instance.CurrentSong?.Id != song.Id || !OutOfSight)
        {
            return;
        }

        try
        {
            var artist = string.IsNullOrWhiteSpace(song.ArtistLine) ? "YouTube Music" : song.ArtistLine;
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "show")
                .AddText(song.Title)
                .AddText(song.Album is { Title.Length: > 0 } album ? $"{artist}  ·  {album.Title}" : artist)
                .SetTag(SongTag)
                .SetGroup(SongGroup)
                // The music is the sound; a notification chime over it would be noise.
                .MuteAudio()
                .AddButton(new AppNotificationButton("Previous").AddArgument("action", "previous"))
                .AddButton(new AppNotificationButton(playing ? "Pause" : "Play").AddArgument("action", "toggle"))
                .AddButton(new AppNotificationButton("Next").AddArgument("action", "next"));

            if (cover is not null)
            {
                builder.SetAppLogoOverride(cover, AppNotificationImageCrop.Default, song.Title);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"song notification failed: {error.Message}");
        }
    }

    /// <summary>Something went wrong while the listener was away: a toast that says what.</summary>
    public static void Fault(string message)
    {
        if (!_registered || !OutOfSight || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "show")
                .AddText("Koda couldn't play that")
                .AddText(message)
                .SetTag(FaultTag)
                .SetGroup(FaultGroup)
                .MuteAudio()
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"fault notification failed: {error.Message}");
        }
    }

    /// <summary>The cover as a file under the data folder, fetched once per picture; null when it
    /// can't be had, and the toast goes without.</summary>
    private static async System.Threading.Tasks.Task<Uri?> LocalArtAsync(Uri? remote)
    {
        if (remote is null)
        {
            return null;
        }

        try
        {
            var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(remote.AbsoluteUri))) + ".jpg";
            var path = System.IO.Path.Combine(ArtFolder, name);
            if (!System.IO.File.Exists(path))
            {
                System.IO.Directory.CreateDirectory(ArtFolder);
                var bytes = await Http.GetByteArrayAsync(remote);
                var temp = path + ".tmp";
                await System.IO.File.WriteAllBytesAsync(temp, bytes);
                System.IO.File.Move(temp, path, overwrite: true);
                PruneArt();
            }

            return new Uri(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Keeps the folder to the last few dozen covers.</summary>
    private static void PruneArt()
    {
        try
        {
            var files = new System.IO.DirectoryInfo(ArtFolder).GetFiles("*.jpg");
            foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(40))
            {
                old.Delete();
            }
        }
        catch (Exception)
        {
            // An old cover left behind is a few kilobytes.
        }
    }

    /// <summary>
    /// The toast was clicked, or one of its buttons was. NotificationInvoked arrives on a
    /// pool thread; everything it touches lives on ours.
    /// </summary>
    private static void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        var action = args.Arguments.TryGetValue("action", out var value) && !string.IsNullOrEmpty(value)
            ? value
            : "show";

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (_shell is { } shell)
            {
                Run(shell, action);
            }
            else
            {
                _pending.Enqueue(action);
            }
        });
    }

    /// <summary>The four things a toast can ask for. Transport stays in the background.</summary>
    private static void Run(MainWindow shell, string action)
    {
        switch (action)
        {
            case "previous":
                _ = PlaybackService.Instance.PreviousAsync();
                break;
            case "next":
                _ = PlaybackService.Instance.NextAsync();
                break;
            case "toggle":
                PlaybackService.Instance.Toggle();
                break;
            case "show":
            default:
                shell.ShowFromTray();
                break;
        }
    }
}
