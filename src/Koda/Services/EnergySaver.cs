using System;
using Microsoft.UI.Dispatching;
using Windows.System.Power;

namespace Koda.Services;

/// <summary>
/// Windows' Energy Saver, as the player sees it. While it is on, the decorative motion
/// (the cover colours drifting behind Now Playing) stops; the music and the lyrics keep
/// working, because those are the app, not the decoration.
///
/// The status is read from the system rather than from a Koda setting: it is the same
/// Energy Saver the battery flyout turns on, and it changes while the app is running.
/// </summary>
internal static class EnergySaver
{
    private static DispatcherQueue? _dispatcher;
    private static bool _watching;

    /// <summary>Whether Windows Energy Saver is on right now.</summary>
    public static bool IsOn { get; private set; }

    /// <summary>Raised on the UI thread when the system's Energy Saver changes.</summary>
    public static event Action? Changed;

    /// <summary>Starts following the system. Call once, after the dispatcher exists.</summary>
    public static void Watch(DispatcherQueue dispatcher)
    {
        if (_watching)
        {
            return;
        }

        _dispatcher = dispatcher;
        try
        {
            IsOn = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            PowerManager.EnergySaverStatusChanged += OnStatusChanged;
            _watching = true;
        }
        catch (Exception error)
        {
            // Power information is unavailable on some virtual machines; motion stays on.
            System.Diagnostics.Debug.WriteLine($"energy saver unavailable: {error.Message}");
        }
    }

    private static void OnStatusChanged(object? sender, object args)
    {
        // The PowerManager event arrives on a pool thread.
        _dispatcher?.TryEnqueue(() =>
        {
            var on = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            if (on == IsOn)
            {
                return;
            }

            IsOn = on;
            Changed?.Invoke();
        });
    }
}
