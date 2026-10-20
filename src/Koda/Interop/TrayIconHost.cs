using System;
using System.Runtime.InteropServices;

namespace Koda.Interop;

/// <summary>
/// The Windows tray icon with the Phase 1 mini transport: play/pause, show, exit.
/// Right-click opens the menu; left-click restores the window. All commands flow
/// through one callback that MainWindow owns.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    /// <summary>The uCallbackMessage MainWindow's subclassed WndProc watches for.</summary>
    public const uint TrayCallbackMessage = Win32.WM_APP_TRAY;

    public const uint MenuPlayPause = 1001;
    public const uint MenuShow = 1002;
    public const uint MenuExit = 1003;

    private const uint TrayId = 1;

    private readonly nint _hwnd;
    private readonly nint _hIcon;
    private readonly Func<string> _playPauseLabel;
    private readonly Action<uint> _onCommand;
    private Win32.NOTIFYICONDATAW _data;
    private bool _added;

    public TrayIconHost(nint hwnd, string iconPath, Func<string> playPauseLabel, Action<uint> onCommand)
    {
        _hwnd = hwnd;
        _playPauseLabel = playPauseLabel;
        _onCommand = onCommand;

        _hIcon = Win32.LoadImageW(IntPtr.Zero, iconPath, Win32.IMAGE_ICON, 32, 32, Win32.LR_LOADFROMFILE);
        if (_hIcon == 0)
        {
            // The window and taskbar already carry the icon; a tray without it beats
            // an app that refuses to run over a missing asset.
            _hIcon = 0;
        }

        _data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = (_hIcon != 0 ? Win32.NIF_ICON : 0) | Win32.NIF_MESSAGE | Win32.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _hIcon,
            szTip = "Koda",
        };

        _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref _data);
    }

    /// <summary>Dispatch the tray callback. lParam carries the mouse message.</summary>
    public void HandleMessage(nint lParam)
    {
        var message = unchecked((uint)lParam.ToInt32());
        switch (message)
        {
            case 0x0202: // WM_LBUTTONUP - restore/focus the window
                _onCommand(MenuShow);
                break;
            case 0x0205: // WM_RBUTTONUP
            case 0x007B: // WM_CONTEXTMENU
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = Win32.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        Win32.AppendMenuW(menu, Win32.MF_STRING, (nint)MenuPlayPause, _playPauseLabel());
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);
        Win32.AppendMenuW(menu, Win32.MF_STRING, (nint)MenuShow, "Show Koda");
        Win32.AppendMenuW(menu, Win32.MF_STRING, (nint)MenuExit, "Exit");

        Win32.GetCursorPos(out Win32.POINT p);
        Win32.SetForegroundWindow(_hwnd);
        var command = Win32.TrackPopupMenuEx(
            menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_NONOTIFY, p.X, p.Y, _hwnd, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        Win32.PostMessageW(_hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (command != 0)
        {
            _onCommand(unchecked((uint)command));
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref _data);
            _added = false;
        }

        if (_hIcon != 0)
        {
            Win32.DestroyIcon(_hIcon);
        }
    }
}
