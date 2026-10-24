using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Koda.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Koda.Helpers;

/// <summary>
/// Koda's three questions, asked the Windows way: a ContentDialog over the window,
/// with the primary button as the one that does the thing. A name cannot be empty,
/// and a destructive action never sits under the Enter key.
/// </summary>
public static class Dialogs
{
    private static XamlRoot? Root => App.Shell?.Content?.XamlRoot;

    /// <summary>Asks for a playlist name and makes it; the sidebar's New Playlist opens it,
    /// a song's Add to playlist adds the songs and stays where the listener is.</summary>
    public static async Task<UserPlaylist?> CreatePlaylistAsync(IReadOnlyList<Song> songs)
    {
        var name = await AskAsync(
            "New Playlist",
            "",
            "Create",
            "Name",
            songs.Count == 0
                ? "Add songs to it from any song's menu."
                : songs.Count == 1
                    ? "The song is added to it."
                    : $"The {songs.Count} songs are added to it.");
        if (name is null)
        {
            return null;
        }

        var playlist = App.Library.CreatePlaylist(name, songs);
        if (songs.Count == 0)
        {
            App.Shell?.NavigateRoot(typeof(Pages.UserPlaylistPage), playlist.Id);
        }
        else
        {
            App.Shell?.Note($"Created “{playlist.Name}”.");
        }

        return playlist;
    }

    /// <summary>Asks for a new name; true when it was saved.</summary>
    public static async Task<bool> RenamePlaylistAsync(UserPlaylist playlist)
    {
        var name = await AskAsync(
            "Rename Playlist",
            playlist.Name,
            "Save",
            "Name",
            "The songs stay in it.");
        if (name is null || name == playlist.Name)
        {
            return false;
        }

        App.Library.RenamePlaylist(playlist.Id, name);
        return true;
    }

    /// <summary>A destructive question with a way out; true only when the action was chosen.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = Build(title, message, action, close: "Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static async Task<string?> AskAsync(string title, string initial, string action, string label, string note)
    {
        var input = new TextBox
        {
            Header = label,
            Text = initial,
            PlaceholderText = "My playlist",
            SelectionStart = initial.Length,
            MaxLength = 120,
        };
        var problem = new TextBlock
        {
            Text = "Give the playlist a name.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(input);
        panel.Children.Add(problem);
        panel.Children.Add(new TextBlock
        {
            Text = note,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = Build(title, panel, action, close: "Cancel");
        var submitted = false;
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (input.Text.Trim().Length == 0)
            {
                args.Cancel = true;
                problem.Visibility = Visibility.Visible;
                input.Focus(FocusState.Programmatic);
            }
        };

        // Enter creates; the dialog's default button takes it from the single-line field.
        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && input.Text.Trim().Length > 0)
            {
                submitted = true;
                dialog.Hide();
                args.Handled = true;
            }
        };

        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary || submitted ? input.Text.Trim() : null;
    }

    private static ContentDialog Build(string title, object content, string action, string close) => new()
    {
        XamlRoot = Root,
        Title = title,
        Content = content,
        PrimaryButtonText = action,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
    };

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog is already up (opened from a menu inside another question):
            // the second one steps aside rather than crashing the app.
            return ContentDialogResult.None;
        }
    }
}
