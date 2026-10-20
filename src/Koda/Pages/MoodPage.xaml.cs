using System;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>A mood or genre: its own page of shelves.</summary>
public sealed partial class MoodPage : Page
{
    private Mood? _mood;

    public MoodPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is Mood mood)
        {
            _mood = mood;
            TitleText.Text = mood.Title;
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_mood is not { } mood)
        {
            return;
        }

        Error.Visibility = Visibility.Collapsed;
        SkeletonHost.Content = Skeletons.Shelves(3);
        try
        {
            var page = await EngineHost.Music.MoodAsync(mood);
            Shelves.Shelves = page.Shelves.Select(ShelfView.From).ToList();
            if (page.Shelves.Length == 0)
            {
                Error.Show("Nothing here right now", "YouTube Music has no shelves for this mood at the moment.", "");
            }
        }
        catch (Exception error)
        {
            Error.ShowError(error, () => _ = LoadAsync());
        }
        finally
        {
            SkeletonHost.Content = null;
        }
    }
}
