using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Controls;

/// <summary>
/// An album's or playlist's download, as one button: Download, then how much is here while it
/// comes, then Downloaded (which, clicked, offers to remove it). The state is read from
/// <see cref="App.Downloads"/> and redrawn on its changes, never kept here.
/// </summary>
public sealed class DownloadButton : Button
{
    private readonly FontIcon _icon = new() { Glyph = "", FontSize = 14 };
    private readonly TextBlock _label = new() { Text = "Download" };
    private IReadOnlyList<Song> _songs = [];
    private bool _attached;

    public DownloadButton()
    {
        Padding = new Thickness(14, 7, 14, 7);
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _icon, _label },
        };
        Click += OnClick;
        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _attached = true;
                App.Downloads.Changed += OnDownloadsChanged;
            }

            Render();
        };
        Unloaded += (_, _) =>
        {
            _attached = false;
            App.Downloads.Changed -= OnDownloadsChanged;
        };
        Render();
    }

    /// <summary>The songs the button speaks for; what it shows is computed from these.</summary>
    public IReadOnlyList<Song> Songs
    {
        get => _songs;
        set
        {
            _songs = value;
            Render();
        }
    }

    /// <summary>Every song of the collection, for one that loads in pages: fetched when Download is
    /// pressed. Null downloads <see cref="Songs"/>.</summary>
    public Func<Task<IReadOnlyList<Song>>>? AllSongs { get; set; }

    /// <summary>What the collection is called, for the confirmation and the note.</summary>
    public string Title { get; set; } = "";

    private void OnDownloadsChanged() => DispatcherQueue?.TryEnqueue(Render);

    private void Render()
    {
        var state = App.Downloads.StateOf(_songs);
        IsEnabled = _songs.Count > 0;
        string text;
        string glyph;
        string help;
        switch (state.Phase)
        {
            case Downloads.Phase.Downloaded:
                (text, glyph, help) = ("Downloaded", "", "Downloaded: plays without a connection. Click to remove.");
                break;
            case Downloads.Phase.Downloading:
            case Downloads.Phase.Waiting:
                var percent = (int)Math.Round(state.Progress * 100);
                (text, glyph, help) = ($"Downloading {percent}%", "", "Downloading. Click to stop and remove.");
                break;
            default:
                (text, glyph, help) = ("Download", "", "Download to play without a connection");
                break;
        }

        _label.Text = text;
        _icon.Glyph = glyph;
        ToolTipService.SetToolTip(this, help);
        AutomationProperties.SetName(this, text);
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        if (App.Downloads.StateOf(_songs).Phase != Downloads.Phase.None)
        {
            var name = string.IsNullOrEmpty(Title) ? "these songs" : Title;
            if (await Helpers.Dialogs.ConfirmAsync($"Remove the download of {name}?",
                    "They'll play from YouTube again, which needs a connection.", "Remove Download"))
            {
                App.Downloads.Remove(_songs);
            }

            return;
        }

        IReadOnlyList<Song> songs = _songs;
        if (AllSongs is { } all)
        {
            IsEnabled = false;
            _label.Text = "Getting songs…";
            try
            {
                songs = await all();
                _songs = songs;
            }
            catch (Exception)
            {
                // The rest of the playlist didn't come; what is shown is downloaded.
            }
        }

        App.Downloads.Download(songs);
        App.Shell?.Note(songs.Count == 1
            ? $"Downloading “{songs[0].Title}”."
            : $"Downloading {songs.Count} songs.");
        Render();
    }
}
