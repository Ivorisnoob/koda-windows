using System;
using System.IO;
using Koda.Playback;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace Koda;

/// <summary>
/// Picture in picture: the video in a compact overlay, Windows' own always-on-top surface,
/// with the system's frame to move and resize it. Play, back to Koda and close show over the
/// picture while the pointer is on it. The picture is the same player the watch page and the
/// bar draw, handed over while this is open and handed back when it closes.
/// </summary>
public sealed class VideoPipWindow : Window
{
    private readonly MediaPlayerElement _picture;
    private readonly Grid _controls;
    private readonly FontIcon _playIcon;
    private readonly Button _playButton;
    private bool _closed;

    private static VideoPlaybackService Player => VideoPlaybackService.Instance;

    /// <summary>The listener asked to go back to the full watch page.</summary>
    public event Action? RestoreRequested;

    public VideoPipWindow()
    {
        Title = Player.Current?.Title ?? "Koda";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        _picture = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };

        _playIcon = new FontIcon { Glyph = "", FontSize = 18 };
        _playButton = Round(_playIcon, "Play (Space)", "Play");
        _playButton.Click += (_, _) => Player.TogglePlay();

        var back = Round(new FontIcon { Glyph = "", FontSize = 14 }, "Back to Koda", "Back to Koda");
        back.Click += (_, _) => RestoreRequested?.Invoke();
        var close = Round(new FontIcon { Glyph = "", FontSize = 14 }, "Close picture in picture", "Close picture in picture");
        close.Click += (_, _) => Close();

        var skipBack = Round(new FontIcon { Glyph = "", FontSize = 16 }, "Back 10 seconds", "Back 10 seconds");
        skipBack.Click += (_, _) => Player.Skip(-10);
        var skipAhead = Round(new FontIcon { Glyph = "", FontSize = 16 }, "Ahead 10 seconds", "Ahead 10 seconds");
        skipAhead.Click += (_, _) => Player.Skip(10);

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { skipBack, _playButton, skipAhead },
        };
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { back, close },
        };

        _controls = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0, 0, 0)),
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) },
            Children = { transport, corner },
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            RequestedTheme = ElementTheme.Dark,
            Children = { _picture, _controls },
        };
        root.PointerEntered += (_, _) => _controls.Opacity = 1;
        root.PointerMoved += (_, _) => _controls.Opacity = 1;
        root.PointerExited += (_, _) => _controls.Opacity = Player.IsPlaying ? 0 : 1;
        root.GotFocus += (_, _) => _controls.Opacity = 1;
        root.KeyDown += OnKeyDown;
        root.DoubleTapped += (_, _) => RestoreRequested?.Invoke();
        AutomationProperties.SetName(root, "Picture in picture");
        Content = root;

        // A 16:9 overlay, small enough to sit in a corner over other work.
        var presenter = CompactOverlayPresenter.Create();
        presenter.InitialSize = CompactOverlaySize.Medium;
        AppWindow.SetPresenter(presenter);
        try
        {
            AppWindow.Resize(new SizeInt32(480, 270));
        }
        catch (Exception)
        {
            // The preset size is fine when the shell refuses this one.
        }

        Player.BeginPictureInPicture(_picture);
        Player.Changed += OnChanged;
        Draw();
        Closed += (_, _) =>
        {
            _closed = true;
            Player.Changed -= OnChanged;
            Player.EndPictureInPicture();
        };
    }

    private static Button Round(FontIcon icon, string tip, string name)
    {
        var button = new Button
        {
            Content = icon,
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x20, 0x20, 0x20)),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(Draw);

    private void Draw()
    {
        if (_closed) return;
        if (!Player.HasVideo)
        {
            Close();
            return;
        }

        var playing = Player.IsPlaying;
        _playIcon.Glyph = playing ? "" : "";
        var label = playing ? "Pause" : "Play";
        ToolTipService.SetToolTip(_playButton, $"{label} (Space)");
        AutomationProperties.SetName(_playButton, label);
        Title = Player.Current?.Title ?? "Koda";
        if (!playing) _controls.Opacity = 1;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
                Player.TogglePlay();
                break;
            case VirtualKey.Left:
            case VirtualKey.J:
                Player.Skip(e.Key == VirtualKey.J ? -10 : -5);
                break;
            case VirtualKey.Right:
            case VirtualKey.L:
                Player.Skip(e.Key == VirtualKey.L ? 10 : 5);
                break;
            case VirtualKey.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
