using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Koda.Interop;

/// <summary>
/// The app's identity for the Windows shell. The media card, the taskbar and
/// toasts read the app name from the Application User Model ID (AUMID), never
/// from the app itself. A packaged app gets one from its manifest; an
/// unpackaged one has to claim one, or those surfaces say "Unknown app".
///
/// Two halves, both needed: the process claims the AUMID (shell32), and the
/// shell is told what that AUMID looks like - a display name and an icon -
/// because there is no package manifest to read them from.
/// </summary>
public static class AppIdentity
{
    /// <summary>Stable across launches: the shell keys its records on it.</summary>
    public const string Aumid = "Koda.Desktop";

    private const string DisplayName = "Koda";

    /// <summary>Where the shell looks up an unpackaged app's name and icon.</summary>
    private const string RegistryPath = @"Software\Classes\AppUserModelId\" + Aumid;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// Claim the identity and publish it. Call before the first window exists:
    /// the media card reads the identity when it binds to the window.
    /// </summary>
    public static void Register()
    {
        ClaimProcessIdentity();
        PublishDisplayName();
    }

    /// <summary>
    /// The process-level AUMID. Without it the shell has no identity to
    /// attach a media session to.
    /// </summary>
    private static void ClaimProcessIdentity()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Aumid);
        }
        catch (Exception error)
        {
            // Identity is cosmetic: the app still runs, the card just stays unnamed.
            System.Diagnostics.Debug.WriteLine($"app identity claim failed: {error.Message}");
        }
    }

    /// <summary>
    /// The shell-side record: display name plus icon, under the AUMID. Written
    /// on every launch so the icon path stays right when the app moves.
    /// </summary>
    private static void PublishDisplayName()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);

            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
            if (File.Exists(icon))
            {
                key.SetValue("IconUri", icon, RegistryValueKind.String);
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"app identity registration failed: {error.Message}");
        }
    }
}
