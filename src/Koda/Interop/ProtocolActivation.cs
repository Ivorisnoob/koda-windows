using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Koda.Interop;

/// <summary>
/// koda:// links: what a browser, a pinned shortcut or another app can open. An
/// unpackaged app has no manifest to declare a protocol handler, so the current
/// user's Classes hive carries it. The handler is the exe itself; a running Koda
/// receives the URL through the single-instance redirect, a cold one at launch.
///
///   koda://open                  bring Koda forward
///   koda://search?q=daft+punk    search YouTube Music
/// </summary>
internal static class ProtocolActivation
{
    public const string Scheme = "koda";

    /// <summary>Register (or refresh) the per-user handler. Cheap and idempotent.</summary>
    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}", writable: true);
            if (root is null)
            {
                return;
            }

            root.SetValue(null, "URL:Koda");
            root.SetValue("URL Protocol", "");
            root.SetValue("FriendlyTypeName", "Koda link");
            using (var icon = root.CreateSubKey("DefaultIcon", writable: true))
            {
                icon?.SetValue(null, $"\"{exe}\",0");
            }

            using var command = root.CreateSubKey(@"shell\open\command", writable: true);
            command?.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        catch (Exception)
        {
            // A locked-down hive means links open the app without the URL; the app works.
        }
    }

    /// <summary>Hand every koda:// argument to the shell. Returns true when one was ours.</summary>
    public static bool Handle(MainWindow window, IEnumerable<string> args)
    {
        var handled = false;
        foreach (var arg in args)
        {
            if (!TryParse(arg, out var uri))
            {
                continue;
            }

            handled = true;
            switch (Segment(uri))
            {
                case "search":
                    window.ShowFromTray();
                    window.SearchFor(Query(uri, "q") ?? Query(uri, "query") ?? "");
                    break;

                case "open":
                case "":
                default:
                    window.ShowFromTray();
                    break;
            }
        }

        return handled;
    }

    private static bool TryParse(string argument, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(argument)
            || !argument.StartsWith($"{Scheme}:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(argument, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>The host (koda://search) or the first path segment (koda:///search).</summary>
    private static string Segment(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host.ToLowerInvariant();
        }

        var path = uri.AbsolutePath.Trim('/');
        var slash = path.IndexOf('/');
        return (slash >= 0 ? path[..slash] : path).ToLowerInvariant();
    }

    private static string? Query(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals >= 0 ? pair[..equals] : pair;
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = equals >= 0 ? pair[(equals + 1)..] : "";
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }
}
