using Koda.Core;
using Koda.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace Koda.Controls;

/// <summary>
/// A song row's download mark: nothing when it is not downloaded, a dotted arrow while it
/// waits, a filling ring while it comes, and a filled arrow once it is here. Mirrors the Mac's
/// DownloadIndicator; redrawn on App.Downloads changes, never storing state of its own.
/// </summary>
public sealed class DownloadIndicator : Grid
{
    private readonly ProgressRing _ring = new()
    {
        Width = 14,
        Height = 14,
        IsIndeterminate = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly FontIcon _icon = new()
    {
        FontSize = 14,
        Visibility = Visibility.Collapsed,
    };
    private bool _attached;
    private Song? _song;

    public DownloadIndicator()
    {
        Width = 16;
        Height = 16;
        Children.Add(_ring);
        Children.Add(_icon);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { _song = DataContext as Song; Render(); };
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _song = DataContext as Song;
        App.Downloads.Changed += OnChanged;
        Render();
    }

    private void Detach()
    {
        _attached = false;
        App.Downloads.Changed -= OnChanged;
    }

    private void OnChanged() => DispatcherQueue?.TryEnqueue(Render);

    private void Render()
    {
        if (_song is null)
        {
            _ring.Visibility = _icon.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(this, "");
            return;
        }

        var state = App.Downloads.StateOf(_song);
        switch (state.Phase)
        {
            case Downloads.Phase.None:
                _ring.Visibility = Visibility.Collapsed;
                _icon.Visibility = Visibility.Collapsed;
                AutomationProperties.SetName(this, "");
                break;
            case Downloads.Phase.Waiting:
                _ring.Visibility = Visibility.Collapsed;
                _icon.Glyph = "\uE896"; // download
                _icon.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                _icon.Visibility = Visibility.Visible;
                AutomationProperties.SetName(this, "Waiting to download");
                break;
            case Downloads.Phase.Downloading:
                _ring.Value = Math.Clamp(state.Progress * 100, 0, 100);
                _ring.Visibility = Visibility.Visible;
                _icon.Visibility = Visibility.Collapsed;
                AutomationProperties.SetName(this, $"Downloading, {_ring.Value:F0} percent");
                break;
            case Downloads.Phase.Downloaded:
                _ring.Visibility = Visibility.Collapsed;
                _icon.Glyph = "\uE8FB"; // check mark
                _icon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                _icon.Visibility = Visibility.Visible;
                AutomationProperties.SetName(this, "Downloaded");
                break;
        }
    }
}