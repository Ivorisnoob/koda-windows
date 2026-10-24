using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;

namespace Koda.Interop;

/// <summary>
/// One Koda at a time. A pinned taskbar shortcut, Windows Search, the Start menu
/// or a protocol launch starts Koda.exe again; that second process hands its
/// activation to the running instance and exits, so the listener gets the window
/// they already had (restored from the tray if needed) instead of another copy.
///
/// This is the Windows App SDK instancing model: register a key before any XAML
/// starts, redirect every later activation to the instance that owns the key, and
/// let that instance's Activated callback bring its window forward.
/// </summary>
public static class SingleInstance
{
    /// <summary>Stable for the life of the app; the shell keys the running instance on it.</summary>
    private const string Key = "Koda.Desktop.Primary";

    private static int _pending;
    private static Action? _handler;
    private static string[] _startupArguments = [];

    /// <summary>What this process was launched with, for a cold koda:// launch.</summary>
    public static IReadOnlyList<string> StartupArguments => _startupArguments;

    /// <summary>
    /// Claim the key. Returns true when this process is a second instance: its
    /// activation has been handed to the running Koda and it must exit without
    /// starting XAML. Command-line arguments (a koda:// URL) are left in the
    /// activation folder for the running instance before the redirect.
    /// </summary>
    public static bool RedirectIfRunning(string[] args)
    {
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(Key);

        if (keyInstance.IsCurrent)
        {
            _startupArguments = args;
            keyInstance.Activated += (_, _) => Notify();
            return false;
        }

        LeaveArguments(args);
        Redirect(keyInstance, activation);
        return true;
    }

    /// <summary>
    /// What the running instance does when another launch arrives. The handler is
    /// stored once the window exists; an activation that arrives before that is
    /// remembered and delivered when the handler is installed.
    /// </summary>
    public static void SetHandler(Action handler)
    {
        _handler = handler;
        if (Interlocked.Exchange(ref _pending, 0) == 1)
        {
            handler();
        }
    }

    /// <summary>The arguments a second instance left behind, oldest first. Reading
    /// consumes them: an activation is acted on once.</summary>
    public static string[] TakePendingArguments()
    {
        try
        {
            var directory = ActivationDirectory;
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var args = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    args.AddRange(File.ReadAllLines(file).Where(line => line.Length > 0));
                }
                catch (Exception)
                {
                    // An activation that cannot be read is dropped, not retried forever.
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // It will be picked up by the next launch if the lock is still there.
                }
            }

            return [.. args];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Notify()
    {
        if (_handler is { } handler)
        {
            handler();
            return;
        }

        Interlocked.Exchange(ref _pending, 1);
    }

    private static string ActivationDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Koda", "activations");

    private static void LeaveArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ActivationDirectory);
            var file = Path.Combine(ActivationDirectory,
                $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(file, args);
        }
        catch (Exception)
        {
            // Without the file the window still comes forward; the URL is lost.
        }
    }

    private static void Redirect(AppInstance target, AppActivationArguments activation)
    {
        // Let the running instance take the foreground when it shows its window;
        // Windows otherwise only flashes the taskbar button.
        try
        {
            AllowSetForegroundWindow((uint)target.ProcessId);
        }
        catch (Exception)
        {
            // Foreground is a nicety; the redirect still works without it.
        }

        // RedirectActivationToAsync must complete, but blocking the STA while it
        // does is not allowed. Run it on the pool and wait on an event instead.
        using var done = new ManualResetEventSlim(false);
        Task.Run(() =>
        {
            try
            {
                target.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"activation redirect failed: {error.Message}");
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait(TimeSpan.FromSeconds(5));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
