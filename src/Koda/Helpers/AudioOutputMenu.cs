using System;
using System.Threading.Tasks;
using Koda.Interop;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Helpers;

/// <summary>
/// The Windows form of an AirPlay picker: the render devices Windows knows, with
/// the system default first. The menu is built when it opens, so a headset plugged
/// in a minute ago is there; the chosen id is kept for the next launch.
/// </summary>
public static class AudioOutputMenu
{
    public static async void Show(FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        try
        {
            var devices = await AudioOutputs.ListAsync();
            var current = PlaybackService.Instance.AudioDeviceId;
            var defaultName = await AudioOutputs.DefaultNameAsync();

            var system = new ToggleMenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(defaultName) ? "System default" : $"System default ({defaultName})",
                IsChecked = string.IsNullOrEmpty(current),
            };
            system.Click += (_, _) => _ = PlaybackService.Instance.SetAudioDeviceAsync(null);
            menu.Items.Add(system);

            if (devices.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            foreach (var device in devices)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = device.Name,
                    IsChecked = device.Id == current,
                    Icon = new FontIcon { Glyph = "\uE7F5" },
                };
                item.Click += (_, _) => _ = PlaybackService.Instance.SetAudioDeviceAsync(device.Id);
                menu.Items.Add(item);
            }

            if (devices.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "No audio devices found", IsEnabled = false });
            }
        }
        catch (Exception)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "The audio devices couldn't be read", IsEnabled = false });
        }

        if (anchor.XamlRoot is not null)
        {
            menu.ShowAt(anchor);
        }
    }
}
