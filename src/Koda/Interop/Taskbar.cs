using System;
using System.Runtime.InteropServices;

namespace Koda.Interop;

/// <summary>
/// What Windows shows of Koda on the taskbar: the thumbnails' Previous/Play/Next
/// buttons and the progress bar under the taskbar button. The shell owns both
/// through ITaskbarList3; Win32 is still the only way to reach it from a desktop app.
/// </summary>
internal sealed class Taskbar : IDisposable
{
    private const uint ThbIcon = 0x00000002;
    private const uint ThbTooltip = 0x00000004;
    private const uint ThbFlags = 0x00000008;

    private const uint ThbfEnabled = 0x00000000;
    private const uint ThbfDisabled = 0x00000001;

    private const uint PreviousId = 1;
    private const uint PlayPauseId = 2;
    private const uint NextId = 3;

    /// <summary>HIWORD of WM_COMMAND for a thumbnail-toolbar button.</summary>
    public const int ThumbnailClicked = 0x1800;

    private readonly nint _hwnd;
    private readonly Action _previous;
    private readonly Action _toggle;
    private readonly Action _next;
    private ITaskbarList3? _list;
    private nint _previousIcon;
    private nint _playIcon;
    private nint _pauseIcon;
    private nint _nextIcon;
    private bool _added;
    private bool? _lastPlaying;
    private bool? _lastHasSong;
    private TaskbarProgress? _lastState;

    public Taskbar(nint hwnd, Action previous, Action toggle, Action next)
    {
        _hwnd = hwnd;
        _previous = previous;
        _toggle = toggle;
        _next = next;

        try
        {
            _list = (ITaskbarList3)new TaskbarList();
            _list.HrInit();
        }
        catch (Exception)
        {
            _list = null;
            return;
        }

        _previousIcon = GlyphIcons.Create("\uE892");
        _playIcon = GlyphIcons.Create("\uE768");
        _pauseIcon = GlyphIcons.Create("\uE769");
        _nextIcon = GlyphIcons.Create("\uE893");

        try
        {
            _added = _list.ThumbBarAddButtons(_hwnd, 3, BuildButtons(playing: false, hasSong: false)) == 0;
        }
        catch (Exception)
        {
            _added = false;
        }
    }

    /// <summary>Progress and transport state, cheap enough to call on the position tick.</summary>
    public void Update(bool hasSong, bool playing, bool loading, double positionSeconds, double durationSeconds)
    {
        if (_list is null)
        {
            return;
        }

        try
        {
            var state = !hasSong
                ? TaskbarProgress.NoProgress
                : loading
                    ? TaskbarProgress.Indeterminate
                    : playing
                        ? TaskbarProgress.Normal
                        : TaskbarProgress.Paused;

            if (_lastState != state)
            {
                _list.SetProgressState(_hwnd, state);
                _lastState = state;
            }

            if (hasSong && durationSeconds > 0)
            {
                const ulong scale = 1000;
                var completed = (ulong)Math.Clamp(positionSeconds / durationSeconds, 0, 1) * scale;
                _list.SetProgressValue(_hwnd, completed, scale);
            }

            if (_added && (_lastPlaying != playing || _lastHasSong != hasSong))
            {
                _list.ThumbBarUpdateButtons(_hwnd, 3, BuildButtons(playing, hasSong));
                _lastPlaying = playing;
                _lastHasSong = hasSong;
            }
        }
        catch (Exception)
        {
            // The shell can go away before the window does (Explorer restarts); the
            // buttons are a convenience, never a reason to fail a track change.
        }
    }

    /// <summary>Run the button a WM_COMMAND names. False when the id is not ours.</summary>
    public bool HandleCommand(int id)
    {
        switch ((uint)id)
        {
            case PreviousId:
                _previous();
                return true;
            case PlayPauseId:
                _toggle();
                return true;
            case NextId:
                _next();
                return true;
            default:
                return false;
        }
    }

    private THUMBBUTTON[] BuildButtons(bool playing, bool hasSong)
    {
        var flags = hasSong ? ThbfEnabled : ThbfDisabled;
        return
        [
            new THUMBBUTTON
            {
                Mask = ThbIcon | ThbTooltip | ThbFlags,
                Id = PreviousId,
                Icon = _previousIcon,
                Tip = "Previous",
                Flags = flags,
            },
            new THUMBBUTTON
            {
                Mask = ThbIcon | ThbTooltip | ThbFlags,
                Id = PlayPauseId,
                Icon = playing ? _pauseIcon : _playIcon,
                Tip = playing ? "Pause" : "Play",
                Flags = flags,
            },
            new THUMBBUTTON
            {
                Mask = ThbIcon | ThbTooltip | ThbFlags,
                Id = NextId,
                Icon = _nextIcon,
                Tip = "Next",
                Flags = flags,
            },
        ];
    }

    public void Dispose()
    {
        try
        {
            _list?.SetProgressState(_hwnd, TaskbarProgress.NoProgress);
        }
        catch (Exception)
        {
            // Nothing to clear on a dead shell.
        }

        _list = null;
        if (_previousIcon != 0) NativeMethods.DestroyIcon(_previousIcon);
        if (_playIcon != 0) NativeMethods.DestroyIcon(_playIcon);
        if (_pauseIcon != 0) NativeMethods.DestroyIcon(_pauseIcon);
        if (_nextIcon != 0) NativeMethods.DestroyIcon(_nextIcon);
        _previousIcon = _playIcon = _pauseIcon = _nextIcon = 0;
    }

    private enum TaskbarProgress
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Tip;
        public uint Flags;
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        void SetProgressValue(nint hwnd, ulong completed, ulong total);

        void SetProgressState(nint hwnd, TaskbarProgress state);

        void RegisterTab(nint hwndTab, nint hwndMdi);

        void UnregisterTab(nint hwndTab);

        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);

        void SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);

        [PreserveSig]
        int ThumbBarAddButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);

        [PreserveSig]
        int ThumbBarUpdateButtons(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);

        void ThumbBarSetImageList(nint hwnd, nint imageList);

        void SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string description);

        void SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);

        void SetThumbnailClip(nint hwnd, nint clip);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(nint icon);
    }
}
