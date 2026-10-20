using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;

namespace Koda;

public partial class App : Application
{
    public static MainWindow? Shell { get; private set; }

    /// <summary>Where the app keeps its state: the engine's path, one source of truth.</summary>
    public static string DataDirectory { get; } = Koda.Core.DataPaths.Root;

    /// <summary>The audio byte store under the data root's audio folder - the CachingLoader
    /// transport keeps every byte that streams through, so a song played before starts from the file.</summary>
    public static Koda.Core.Storage.AudioStore AudioStore { get; } =
        new(Path.Combine(DataDirectory, "audio"));

    /// <summary>Likes, saved collections, history, stats: library.json in the data root.</summary>
    public static Koda.Core.Library Library { get; } = new(DataDirectory);

    /// <summary>Close-to-tray: shipped ON; boxed in memory until the Settings store lands.</summary>
    public static bool CloseToTray { get; private set; } = true;

    /// <summary>App volume 0..100, remembered across launches with the shell settings.</summary>
    public static double Volume { get; private set; } = 100.0;

    private static string SettingsPath => Path.Combine(DataDirectory, "shell-settings.json");

    public App()
    {
        // Before any window exists: the shell reads the app identity when the
        // media card binds, and an unnamed card says "Unknown app".
        Koda.Interop.AppIdentity.Register();

        InitializeComponent();
        LoadShellSettings();

        // A crash leaves its real reason in the crash log (the Windows event log only shows 0xc000027b).
        UnhandledException += (_, e) => WriteCrash($"{e.Message}\n{e.Exception}");
        DebugSettings.XamlResourceReferenceFailed += (_, e) => WriteCrash($"resource: {e.Message}");
        DebugSettings.BindingFailed += (_, e) => System.Diagnostics.Debug.WriteLine(e.Message);
    }

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Shell = new MainWindow();
        Shell.Activate();
    }

    public static void SetCloseToTray(bool value)
    {
        if (CloseToTray == value)
        {
            return;
        }

        CloseToTray = value;
        SaveShellSettings();
    }

    public static void SetVolume(double value)
    {
        Volume = Math.Clamp(value, 0, 100);
        SaveShellSettings();
    }

    private static void LoadShellSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var node = JsonNode.Parse(File.ReadAllText(SettingsPath));
            if (node?["closeToTray"] is JsonNode closeToTray)
            {
                CloseToTray = closeToTray.GetValue<bool>();
            }

            if (node?["volume"] is JsonNode volume)
            {
                Volume = Math.Clamp(volume.GetValue<double>(), 0, 100);
            }
        }
        catch (Exception error)
        {
            // Defaults hold; the report shows the reason instead of breaking the launch.
            System.Diagnostics.Debug.WriteLine($"shell-settings load failed: {error.Message}");
        }
    }

    private static void SaveShellSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { closeToTray = CloseToTray, volume = Volume }));
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"shell-settings save failed: {error.Message}");
        }
    }
}
