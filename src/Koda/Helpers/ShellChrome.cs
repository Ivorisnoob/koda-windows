using Koda.Core;
using Microsoft.UI.Xaml;

namespace Koda.Helpers;

/// <summary>
/// The shell's own chrome: the theme the listener chose, applied to the window.
/// The Now Playing page is always dark, the way it is on the Mac.
/// </summary>
public static class ShellChrome
{
    /// <summary>Applies the theme to the shell window.</summary>
    public static void ApplyTheme()
    {
        if (App.Shell?.Content is not FrameworkElement content)
        {
            return;
        }

        content.RequestedTheme = App.Settings.Theme switch
        {
            Theme.Light => ElementTheme.Light,
            Theme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
