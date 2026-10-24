using System;
using Microsoft.Win32;

namespace Koda.Interop;

/// <summary>
/// "Open at login" for an unpackaged app: one value in the current user's Run key.
/// A packaged Koda would use StartupTask; without an identity the Run key is the
/// supported way, and it needs no elevation. The value always points at the exe
/// that is running now, so a moved or updated build is picked up on the next launch.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Koda";

    /// <summary>The command Windows will run at sign-in.</summary>
    private static string? Command
    {
        get
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\"";
        }
    }

    /// <summary>Whether the Run key holds Koda's entry and points at this build.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value
                    && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Writes or removes the Run entry. Returns false when Windows refused.</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled && Command is { } command)
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return IsEnabled == enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the key honest: a setting that says on but whose entry was removed
    /// (by a cleaner, or a moved build) is written again at launch; a setting that
    /// says off never leaves an entry behind.
    /// </summary>
    public static void Sync()
    {
        var wanted = Koda.Core.Settings.Current.OpenAtLogin;
        if (wanted != IsEnabled)
        {
            Apply(wanted);
        }
    }
}
