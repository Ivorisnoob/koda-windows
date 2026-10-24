using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core.LastFm;
using Koda.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// Last.fm, as the Mac's sidebar page: before connecting, the three steps in one card; once
/// connected, the account and what Koda is sending beside the listener's recent plays. Every
/// figure is read from <see cref="App.LastFm"/> and redrawn on its changes.
///
/// Last.fm's desktop sign-in has no way to call back, so while a token waits for approval the
/// page asks every few seconds, quietly, and connects the moment the listener approves.
/// </summary>
public sealed partial class LastFmPage : Page
{
    private static readonly TimeSpan ApprovalPoll = TimeSpan.FromSeconds(4);

    private DispatcherQueueTimer? _poll;
    private bool _rendering;

    public LastFmPage()
    {
        InitializeComponent();
    }

    private static LastFm LastFm => App.LastFm;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LastFm.Changed += OnChanged;
        ApiKeyBox.Text = LastFm.ApiKey;
        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = ApprovalPoll;
        _poll.Tick += (_, _) => _ = PollApprovalAsync();
        Render();
        if (LastFm.IsConnected && LastFm.IsEnabled)
        {
            _ = LastFm.RefreshAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        LastFm.Changed -= OnChanged;
        _poll?.Stop();
        _poll = null;
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        _rendering = true;
        try
        {
            var connected = LastFm.IsConnected;
            var busy = LastFm.IsBusy;
            BusyRing.IsActive = busy;
            SyncButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            SyncButton.IsEnabled = !busy && LastFm.IsEnabled && !LastFm.IsPaused;

            ShowMessage(ErrorBar, LastFm.Error?.Message);
            ShowMessage(NoticeBar, LastFm.Notice);

            ConnectCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            ConnectedView.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (connected)
            {
                RenderAccount();
                RenderHistory();
                _poll?.Stop();
            }
            else
            {
                var waiting = LastFm.IsSigningIn;
                Steps.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
                Waiting.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
                ApprovedButton.IsEnabled = ReopenButton.IsEnabled = StartOverButton.IsEnabled = !busy;
                ConnectButton.IsEnabled = CanConnect && !busy;
                if (waiting) _poll?.Start();
                else _poll?.Stop();
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private static void ShowMessage(InfoBar bar, string? message)
    {
        bar.Message = message ?? "";
        bar.IsOpen = !string.IsNullOrEmpty(message);
    }

    private void RenderAccount()
    {
        var user = LastFm.User;
        var profile = LastFm.Profile;
        NameText.Text = profile is { RealName.Length: > 0 } ? profile.RealName : user;
        HandleText.Text = $"@{user}";
        Avatar.DisplayName = NameText.Text;
        Avatar.ProfilePicture = profile?.Avatar is { } avatar ? new BitmapImage(avatar) { DecodePixelWidth = 176 } : null;

        PlayCountPanel.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        if (profile is not null)
        {
            PlayCountText.Text = profile.PlayCount.ToString("N0", CultureInfo.CurrentCulture);
            PlayCountLabel.Text = profile.PlayCount == 1 ? "scrobble" : "scrobbles";
        }

        var since = Since(profile);
        SinceText.Text = since ?? "";
        SinceText.Visibility = since is null ? Visibility.Collapsed : Visibility.Visible;

        ScrobbleSwitch.IsOn = LastFm.IsEnabled;
        StatusText.Text = Status();
        StatusText.Foreground = (Brush)Application.Current.Resources[
            LastFm.IsPaused ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];

        ProfileLink.NavigateUri = new Uri($"https://www.last.fm/user/{Uri.EscapeDataString(user)}");
    }

    /// <summary>The recent plays by day, newest first; what is playing now leads. While nothing has
    /// been fetched yet, rows in the list's own shape stand in.</summary>
    private void RenderHistory()
    {
        var refreshed = LastFm.RefreshedAt;
        UpdatedText.Text = refreshed is { } at ? $"Updated {Relative(at)}" : "";
        UpdatedText.Visibility = refreshed is null ? Visibility.Collapsed : Visibility.Visible;

        // A "now playing" seen long ago is stale: only a fresh look still believes it.
        var live = refreshed is { } r && DateTimeOffset.UtcNow - r < TimeSpan.FromMinutes(2);
        var tracks = LastFm.Recent.Where(t => live || !t.IsNowPlaying).ToArray();

        HistoryDays.Children.Clear();
        if (refreshed is null && LastFm.IsBusy)
        {
            HistorySkeleton.Content ??= Skeletons.Rows(8);
            HistoryEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        HistorySkeleton.Content = null;
        if (tracks.Length == 0)
        {
            HistoryEmpty.Show("Nothing scrobbled yet", "Songs you play in Koda appear here once they count.", "");
            HistoryEmpty.Visibility = Visibility.Visible;
            return;
        }

        HistoryEmpty.Visibility = Visibility.Collapsed;
        foreach (var (title, day) in Days(tracks))
        {
            var group = new StackPanel { Spacing = 2 };
            group.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(8, 0, 0, 4),
            });
            foreach (var track in day)
            {
                group.Children.Add(Row(track));
            }

            HistoryDays.Children.Add(group);
        }
    }

    private static Grid Row(LastFmTrack track)
    {
        var row = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cover = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new Grid
            {
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] },
                },
            },
        };
        if (track.Artwork is { } art)
        {
            ((Grid)cover.Child).Children.Add(new Image
            {
                Source = new BitmapImage(art) { DecodePixelWidth = 80 },
                Stretch = Stretch.UniformToFill,
            });
        }

        row.Children.Add(cover);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = track.Title,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => s.Length > 0)),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var when = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources[track.IsNowPlaying ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            Text = track.IsNowPlaying ? "Now" : track.PlayedAt is { } at ? Format.Time(at) : "",
        };
        Grid.SetColumn(when, 2);
        row.Children.Add(when);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row,
            $"{track.Title}, {track.Artist}{(track.IsNowPlaying ? ", playing now" : track.PlayedAt is { } p ? $", {Format.Time(p)}" : "")}");
        return row;
    }

    private static IEnumerable<(string Title, List<LastFmTrack> Tracks)> Days(IEnumerable<LastFmTrack> tracks)
    {
        var groups = new List<(string Title, List<LastFmTrack> Tracks)>();
        foreach (var track in tracks)
        {
            var title = track.IsNowPlaying ? "Now" : track.PlayedAt is { } at ? Format.Day(at) : "Earlier";
            if (groups.Count > 0 && groups[^1].Title == title)
            {
                groups[^1].Tracks.Add(track);
            }
            else
            {
                groups.Add((title, [track]));
            }
        }

        return groups;
    }

    /// <summary>"Scrobbling since 2014 · United Kingdom".</summary>
    private static string? Since(LastFmProfile? profile)
    {
        if (profile is null) return null;
        var parts = new List<string>();
        if (profile.Registered is { } registered) parts.Add($"Scrobbling since {registered.ToLocalTime().Year}");
        if (profile.Country.Length > 0) parts.Add(profile.Country);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>How the queue stands, in a line: what's waiting, or when the last play went.</summary>
    private static string Status()
    {
        if (LastFm.IsPaused) return "Paused while listening history is off";
        var waiting = LastFm.Queued.Length;
        if (waiting > 0) return waiting == 1 ? "1 play waiting to send" : $"{waiting} plays waiting to send";
        return LastFm.LastSent is { } sent ? $"Up to date · last sent {Relative(sent)}" : "Up to date";
    }

    private static string Relative(DateTimeOffset at)
    {
        var ago = DateTimeOffset.UtcNow - at;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return (int)ago.TotalHours == 1 ? "1 hr ago" : $"{(int)ago.TotalHours} hr ago";
        return (int)ago.TotalDays == 1 ? "yesterday" : $"{(int)ago.TotalDays} days ago";
    }

    // MARK: connecting

    private bool CanConnect =>
        LastFmProtocol.IsCredential(ApiKeyBox.Text.Trim()) && LastFmProtocol.IsCredential(SecretBox.Password.Trim());

    private void OnKeysChanged(object sender, RoutedEventArgs e) =>
        ConnectButton.IsEnabled = CanConnect && !LastFm.IsBusy;

    private void OnSecretKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && CanConnect)
        {
            e.Handled = true;
            _ = OpenApprovalAsync(restart: false);
        }
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => _ = OpenApprovalAsync(restart: false);

    private void OnReopenClick(object sender, RoutedEventArgs e) => _ = OpenApprovalAsync(restart: true);

    private async Task OpenApprovalAsync(bool restart)
    {
        try
        {
            var url = restart
                ? await LastFm.RestartSignInAsync()
                : await LastFm.BeginSignInAsync(ApiKeyBox.Text, SecretBox.Password);
            SecretBox.Password = "";
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
        catch (Exception)
        {
            // The reason is on the page, in the warning bar.
        }
    }

    private async void OnApprovedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await LastFm.FinishSignInAsync();
        }
        catch (Exception)
        {
            // The reason is on the page, in the warning bar.
        }
    }

    /// <summary>While Koda waits for the approval, a quiet look every few seconds.</summary>
    private async Task PollApprovalAsync()
    {
        if (!LastFm.IsSigningIn || LastFm.IsBusy)
        {
            return;
        }

        try
        {
            await LastFm.FinishSignInAsync(quietly: true);
        }
        catch (Exception)
        {
            // Not approved yet, most likely; the next look tries again.
        }
    }

    private void OnStartOverClick(object sender, RoutedEventArgs e) => LastFm.SetEnabled(false);

    // MARK: connected

    private void OnSyncClick(object sender, RoutedEventArgs e) => _ = LastFm.RefreshAsync();

    private void OnScrobbleToggled(object sender, RoutedEventArgs e)
    {
        if (_rendering || ScrobbleSwitch.IsOn == LastFm.IsEnabled) return;
        LastFm.SetEnabled(ScrobbleSwitch.IsOn);
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var waiting = LastFm.Queued.Length;
        var message = waiting == 0
            ? "Koda forgets your account and API keys on this PC. Your Last.fm history stays on Last.fm."
            : $"{waiting} plays haven't been sent yet and will be lost. Your Last.fm history stays on Last.fm.";
        if (await Dialogs.ConfirmAsync("Disconnect Last.fm?", message, "Disconnect and Forget Keys"))
        {
            LastFm.Disconnect();
            ApiKeyBox.Text = "";
        }
    }

    /// <summary>Only the listener closing a message dismisses it; the page closing one it no longer
    /// needs is not a dismissal.</summary>
    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            LastFm.DismissNotice();
        }
    }

    /// <summary>The account sits beside its plays when there is room, above them when not.</summary>
    private void OnConnectedSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width > 760;
        AccountColumn.Width = wide ? new GridLength(320) : new GridLength(1, GridUnitType.Star);
        HistoryColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(HistoryPanel, wide ? 1 : 0);
        Grid.SetRow(HistoryPanel, wide ? 0 : 1);
        AccountCard.MaxWidth = wide ? double.PositiveInfinity : 480;
        AccountCard.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}
