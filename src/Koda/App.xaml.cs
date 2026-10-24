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

    /// <summary>Likes, saved collections, history, stats: library.json in the data root.</summary>
    public static Koda.Core.Library Library { get; } = new(DataDirectory);

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

        // A cold koda:// launch carries its URL on this process's command line.
        if (Koda.Interop.SingleInstance.StartupArguments.Count > 0)
        {
            Koda.Interop.ProtocolActivation.Handle(Shell, Koda.Interop.SingleInstance.StartupArguments);
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
                Koda.Interop.ProtocolActivation.Handle(shell, pending);
            }
        }));
    }
}
