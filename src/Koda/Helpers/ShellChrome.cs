using System;
using System.Collections.Generic;
using Koda.Core;
using Microsoft.UI.Xaml;

namespace Koda.Helpers;

/// <summary>
/// The shell's own chrome: the theme the listener chose, applied to every window Koda has
/// open. A window's Mica takes its tint from its content's theme, so the mini player follows
/// the choice too rather than Windows' own. The Now Playing page is always dark, the way it
/// is on the Mac.
/// </summary>
public static class ShellChrome
{
    private static readonly List<WeakReference<FrameworkElement>> Others = [];

    /// <summary>Applies the theme to the shell window and every window registered with it.</summary>
    public static void ApplyTheme()
    {
        if (App.Shell?.Content is FrameworkElement content)
        {
            Apply(content);
        }

        Others.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in Others)
        {
            if (reference.TryGetTarget(out var other)) Apply(other);
        }
    }

    /// <summary>A second window's content takes the chosen theme now and whenever it changes.</summary>
    public static void Register(FrameworkElement content)
    {
        Others.Add(new WeakReference<FrameworkElement>(content));
        Apply(content);
    }

    private static void Apply(FrameworkElement content) =>
        content.RequestedTheme = App.Settings.Theme switch
        {
            Theme.Light => ElementTheme.Light,
            Theme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
}
