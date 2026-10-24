using System;
using System.IO;
using Koda.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Koda;

/// <summary>
/// The mini player as Windows knows it: a compact overlay, the same picture-in-picture
/// surface video apps use, so it floats above other windows with the system's own frame
/// and can be moved where the listener wants it. It is a second window, not a restyle of
/// the main one, so the library stays open behind it.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    private readonly MiniPlayerPage _page;

    public MiniPlayerWindow()
    {
        Title = "Koda";
        SystemBackdrop = new MicaBackdrop();

        _page = new MiniPlayerPage();
        Content = _page;

        var appWindow = AppWindow;
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // Windows sizes a compact overlay from a preset, then lets the app make it its
        // own. A strip, not a video frame: wide enough for a title, short enough to sit
        // over a browser without hiding it.
        var presenter = CompactOverlayPresenter.Create();
        presenter.InitialSize = CompactOverlaySize.Small;
        appWindow.SetPresenter(presenter);
        try
        {
            appWindow.Resize(new SizeInt32(420, 156));
        }
        catch (Exception)
        {
            // The preset is a perfectly good mini player if the shell refuses the size.
        }

        _page.RestoreRequested += OnRestoreRequested;
        Closed += (_, _) =>
        {
            _page.RestoreRequested -= OnRestoreRequested;
            _page.Detach();
        };
    }

    /// <summary>Leave the overlay and bring the library forward.</summary>
    private void OnRestoreRequested()
    {
        Close();
        App.Shell?.ShowFromTray();
    }
}
