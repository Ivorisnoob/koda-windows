using System;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// The compact overlay's surface: the cover, what is playing, and the only controls a
/// window this size needs. It follows the player like the bar does, and its one exit is
/// the full window, because Windows already gives the overlay a close button.
/// </summary>
public sealed partial class MiniPlayerPage : Page
{
    private bool _attached;

    /// <summary>Raised when the listener asks for the full window.</summary>
    public event Action? RestoreRequested;

    private PlaybackService Playback => PlaybackService.Instance;

    public MiniPlayerPage()
    {
        InitializeComponent();
        Attach();
        RenderTrack();
        RenderPlayState();
        RenderPosition();

        ActualThemeChanged += (_, _) => RenderLike();
    }

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Playback.TrackChanged += OnTrackChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;
        Playback.PositionTick += OnPositionTick;
        App.Library.Changed += OnLibraryChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>Stop listening: the window is closing, and the events outlive it.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        Playback.TrackChanged -= OnTrackChanged;
        Playback.PlayStateChanged -= OnPlayStateChanged;
        Playback.PositionTick -= OnPositionTick;
        App.Library.Changed -= OnLibraryChanged;
        Unloaded -= OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnTrackChanged() => RenderTrack();

    private void OnPlayStateChanged() => RenderPlayState();

    private void OnPositionTick() => RenderPosition();

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(RenderLike);

    private void RenderTrack()
    {
        var song = Playback.CurrentSong;
        TitleText.Text = song?.Title ?? "Nothing playing";
        ArtistText.Text = song?.ArtistLine ?? "";
        Art.SetSource(CoverImage, song?.Artwork);
        CoverGlyph.Visibility = song?.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;

        PreviousButton.IsEnabled = song is not null;
        PlayButton.IsEnabled = song is not null;
        NextButton.IsEnabled = Playback.CanNext;
        RenderLike();
        RenderPosition();
    }

    private void RenderPlayState()
    {
        var playing = Playback.IsPlaying;
        var loading = Playback.IsLoading;
        PlayIcon.Glyph = playing ? "\uF8AE" : "\uF5B0";
        PlayIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PlayButton, playing ? "Pause" : "Play");
        AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
        PlayButton.IsEnabled = Playback.CurrentSong is not null;
        NextButton.IsEnabled = Playback.CanNext;
    }

    private void RenderPosition()
    {
        var duration = Playback.DurationSeconds;
        var position = Playback.PositionSeconds;
        Progress.Visibility = duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        Progress.Maximum = Math.Max(1, duration);
        Progress.Value = Math.Clamp(position, 0, Progress.Maximum);
        PositionText.Text = duration > 0
            ? $"{FormatTime(position)} / {FormatTime(duration)}"
            : "";
    }

    private void RenderLike()
    {
        var liked = Playback.CurrentSong is { } song && App.Library.IsLiked(song);
        LikeIcon.Glyph = liked ? "\uEB52" : "\uEB51";
        LikeIcon.Foreground = liked
            ? new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x4F, 0x8B))
            : new SolidColorBrush(ActualTheme == ElementTheme.Dark
                ? Colors.White
                : Color.FromArgb(255, 0x20, 0x20, 0x20));
        var label = liked ? "Remove from Liked" : "Add to Liked";
        ToolTipService.SetToolTip(LikeButton, label);
        AutomationProperties.SetName(LikeButton, label);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();

    private void OnPlayClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song)
        {
            App.Library.ToggleLike(song);
        }
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke();

    private void OnRestoreTapped(object sender, DoubleTappedRoutedEventArgs e) => RestoreRequested?.Invoke();

    private static string FormatTime(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes}:{secs:D2}";
    }
}
