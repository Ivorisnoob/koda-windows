using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Koda.Helpers;

/// <summary>
/// A song's position slider, the same in the player bar and Now Playing: while the listener
/// drags it, the song's ticks leave it alone, and the song moves only when the drag ends.
///
/// A drag starts with the primary button, a finger or a pen, never a right-click: a press the
/// slider never captures leaves no drag to end, and a scrubber stuck "dragging" would later
/// seek the song back to wherever it froze. The arrow keys, Home and End move the song too,
/// so it can be played from the keyboard alone.
/// </summary>
public static class Scrubbing
{
    public static void Attach(Slider slider, Action<bool> scrubbing, Action<double> seek, Func<bool>? canScrub = null)
    {
        var active = false;

        void End(bool commit)
        {
            if (!active)
            {
                return;
            }

            active = false;
            scrubbing(false);
            if (commit)
            {
                seek(slider.Value);
            }
        }

        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(slider);
            var primary = point.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed;
            if (!primary || canScrub?.Invoke() == false)
            {
                return;
            }

            active = true;
            scrubbing(true);
        }), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => End(commit: true)), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => End(commit: true)), true);
        slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => End(commit: false)), true);

        slider.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
                or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown
                && canScrub?.Invoke() != false)
            {
                seek(slider.Value);
            }
        }), true);
    }
}
