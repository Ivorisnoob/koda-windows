using System;
using Koda.Core;

namespace Koda.Interop;

/// <summary>
/// Windows' own answer to "remember where I was": WINDOWPLACEMENT stores the normal
/// rectangle separately from the maximised state, so a window closed maximised comes
/// back maximised and still remembers the size to restore to. The saved rectangle is
/// physical pixels, like the placement API; it is only trusted when it still lands on
/// a monitor, so undocking or changing the display layout never strands the window.
/// </summary>
internal static class WindowPlacement
{
    private const int MinimumWidth = 700;
    private const int MinimumHeight = 520;

    /// <summary>Put the window back where the listener left it, or leave the default.</summary>
    public static void Restore(nint hwnd)
    {
        var settings = Settings.Current;
        if (settings.WindowWidth < MinimumWidth || settings.WindowHeight < MinimumHeight)
        {
            return;
        }

        var normal = new Win32.RECT
        {
            Left = settings.WindowLeft,
            Top = settings.WindowTop,
            Right = settings.WindowLeft + settings.WindowWidth,
            Bottom = settings.WindowTop + settings.WindowHeight,
        };

        if (!IsVisibleOnSomeMonitor(normal))
        {
            // The display that held it is gone: open at the default size, centred on
            // the primary work area rather than at coordinates that no longer exist.
            normal = Centered(normal);
        }

        var placement = new Win32.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>(),
            showCmd = settings.WindowMaximized ? Win32.SW_SHOWMAXIMIZED : Win32.SW_SHOWNORMAL,
            rcNormalPosition = normal,
        };

        try
        {
            Win32.SetWindowPlacement(hwnd, ref placement);
        }
        catch (Exception)
        {
            // The default size is a fine fallback.
        }
    }

    /// <summary>Remember the normal rectangle and whether it was maximised.</summary>
    public static void Save(nint hwnd)
    {
        var placement = new Win32.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>(),
        };

        try
        {
            if (!Win32.GetWindowPlacement(hwnd, ref placement))
            {
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        var normal = placement.rcNormalPosition;
        if (normal.Width < MinimumWidth || normal.Height < MinimumHeight)
        {
            return;
        }

        var settings = Settings.Current;
        var maximized = placement.showCmd == Win32.SW_SHOWMAXIMIZED;
        if (settings.WindowLeft == normal.Left
            && settings.WindowTop == normal.Top
            && settings.WindowWidth == normal.Width
            && settings.WindowHeight == normal.Height
            && settings.WindowMaximized == maximized)
        {
            return;
        }

        // Placement is shell state, not a preference the pages listen to: write the
        // file without raising Changed, or a drag would repaint every shelf.
        settings.WindowLeft = normal.Left;
        settings.WindowTop = normal.Top;
        settings.WindowWidth = normal.Width;
        settings.WindowHeight = normal.Height;
        settings.WindowMaximized = maximized;
        settings.Save();
    }

    private static bool IsVisibleOnSomeMonitor(Win32.RECT rect)
    {
        // Shrunk to a point at the window's top-left: a rect that only clips the
        // screen edge still counts as visible, which is what a listener expects.
        var probe = new Win32.RECT
        {
            Left = rect.Left + rect.Width / 2,
            Top = rect.Top + 24,
            Right = rect.Left + rect.Width / 2 + 1,
            Bottom = rect.Top + 25,
        };

        var monitor = Win32.MonitorFromRect(ref probe, Win32.MONITOR_DEFAULTTONULL);
        return monitor != 0;
    }

    private static Win32.RECT Centered(Win32.RECT rect)
    {
        var probe = new Win32.RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        var monitor = Win32.MonitorFromRect(ref probe, Win32.MONITOR_DEFAULTTONEAREST);
        var info = Win32.MONITORINFO.Create();
        if (monitor == 0 || !Win32.GetMonitorInfoW(monitor, ref info))
        {
            return rect;
        }

        var work = info.rcWork;
        var width = Math.Min(rect.Width, work.Width);
        var height = Math.Min(rect.Height, work.Height);
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (work.Height - height) / 2;

        // rcNormalPosition is in workspace coordinates, whose origin is the top-left of
        // the primary work area, not the screen: a top or left taskbar shifts it.
        var primary = new Win32.RECT();
        if (Win32.SystemParametersInfoW(Win32.SPI_GETWORKAREA, 0, ref primary, 0))
        {
            left -= primary.Left;
            top -= primary.Top;
        }

        return new Win32.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }
}
