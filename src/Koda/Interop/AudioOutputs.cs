using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Koda.Interop;

/// <summary>One place to play sound: a headset, speakers, a monitor over HDMI.</summary>
internal sealed record AudioOutputInfo(string Id, string Name);

/// <summary>
/// The render devices Windows offers, and the one Koda is using. MediaPlayer takes a
/// DeviceInformation, so the id in the settings is resolved to one at launch; a device
/// that has been unplugged falls back to the system default instead of silencing Koda.
/// </summary>
internal static class AudioOutputs
{
    /// <summary>Every active render device, in the order Windows lists them.</summary>
    public static async Task<IReadOnlyList<AudioOutputInfo>> ListAsync()
    {
        var found = new List<AudioOutputInfo>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            foreach (var device in devices)
            {
                if (!device.IsEnabled)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(device.Name) ? "Audio device" : device.Name;
                found.Add(new AudioOutputInfo(device.Id, name));
            }
        }
        catch (Exception)
        {
            // No audio stack, no list: the system default still plays.
        }

        return found;
    }

    /// <summary>The friendly name of the current system default, for the menu.</summary>
    public static async Task<string?> DefaultNameAsync()
    {
        try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var devices = await ListAsync();
            return devices.FirstOrDefault(d => d.Id == id)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The DeviceInformation behind a saved id, or null when it is gone.</summary>
    public static async Task<DeviceInformation?> FindAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            var device = await DeviceInformation.CreateFromIdAsync(id);
            return device is { IsEnabled: true } ? device : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
