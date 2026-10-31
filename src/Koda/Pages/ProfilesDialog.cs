using System;
using System.Threading.Tasks;
using Koda.Core.Profiles;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Koda.Pages;

/// <summary>
/// Managing profiles: every profile with who it is, switching, renaming, deleting and adding,
/// signed in to Google or kept on this PC. Only ever opened by asking for it.
/// </summary>
public static class ProfilesDialog
{
    private static XamlRoot? Root => App.Shell?.Content?.XamlRoot;

    /// <summary>One dialog shows at a time, so an action closes this one, does its work (which may
    /// be a dialog of its own), and comes back here.</summary>
    public static async Task ShowAsync()
    {
        while (Root is { } root)
        {
            Func<Task>? next = null;
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Profiles",
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close,
            };

            void Then(Func<Task> action)
            {
                next = action;
                dialog.Hide();
            }

            dialog.Content = Build(Then);
            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                return; // Another dialog is up.
            }

            if (next is null) return;
            await next();
        }
    }

    public static PersonPicture Picture(KodaProfile profile, double size)
    {
        var picture = new PersonPicture
        {
            Width = size,
            Height = size,
            DisplayName = profile.DisplayName,
        };
        if (profile.AccountPhoto is { } photo && Uri.TryCreate(photo, UriKind.Absolute, out var url))
        {
            picture.ProfilePicture = new BitmapImage(url) { DecodePixelWidth = (int)size * 2 };
        }

        return picture;
    }

    private static StackPanel Build(Action<Func<Task>> then)
    {
        var panel = new StackPanel { Spacing = 4, MinWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = "Each profile has its own likes, playlists, history and Home.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        foreach (var profile in Profiles.Roster.Profiles)
        {
            panel.Children.Add(Row(profile, then));
        }

        var add = new Button { Content = "Add profile…", Margin = new Thickness(0, 12, 0, 0) };
        add.Click += (_, _) => then(AddAsync);
        panel.Children.Add(add);
        return panel;
    }

    private static Grid Row(KodaProfile profile, Action<Func<Task>> then)
    {
        var active = profile.Id == Profiles.Roster.ActiveId;
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Picture(profile, 40));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = profile.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = active ? $"{profile.Subtitle} · Active" : profile.Subtitle,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (!active)
        {
            var use = new Button { Content = "Switch" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(use, $"Switch to {profile.DisplayName}");
            use.Click += (_, _) => then(() => Profiles.SwitchAsync(profile.Id));
            actions.Children.Add(use);
        }

        var more = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 14 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"More for {profile.DisplayName}");
        ToolTipService.SetToolTip(more, "More");
        var menu = new MenuFlyout();
        menu.Items.Add(SongMenu.Item("Rename…", "", () => then(() => RenameAsync(profile))));
        if (Profiles.Roster.HasSeveral)
        {
            menu.Items.Add(SongMenu.Item("Delete profile…", "", () => then(() => DeleteAsync(profile))));
        }

        more.Flyout = menu;
        actions.Children.Add(more);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return row;
    }

    private static async Task AddAsync()
    {
        if (Root is not { } root) return;
        var name = new TextBox { PlaceholderText = "Name", MinWidth = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, "Profile name");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "A profile on this PC needs no account. Everything stays on this device.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(name);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Add Profile",
            Content = content,
            PrimaryButtonText = "Add",
            SecondaryButtonText = "Sign in with Google instead",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await Profiles.SwitchAsync(Profiles.Roster.AddDeviceProfile(name.Text).Id);
                break;
            case ContentDialogResult.Secondary:
                await Profiles.AddSignedInAsync();
                break;
        }
    }

    private static async Task RenameAsync(KodaProfile profile)
    {
        if (Root is not { } root) return;
        var name = new TextBox { Text = profile.Name, PlaceholderText = "Name", MinWidth = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, "Profile name");
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Rename Profile",
            Content = name,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Profiles.Roster.Update(profile.Id, p => p.Name = name.Text.Trim());
        }
    }

    private static async Task DeleteAsync(KodaProfile profile)
    {
        var account = profile.IsSignedIn ? " Your YouTube Music account isn't affected." : "";
        if (await Dialogs.ConfirmAsync(
                $"Delete “{profile.DisplayName}”?",
                $"Its liked songs, playlists, history and stats are deleted from this PC.{account}",
                "Delete Profile"))
        {
            await Profiles.DeleteAsync(profile.Id);
        }
    }
}
