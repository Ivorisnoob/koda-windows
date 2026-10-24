using System;
using System.Runtime.InteropServices;
using Koda.Core;
using WinRT;
using Windows.ApplicationModel.DataTransfer;

namespace Koda.Interop;

/// <summary>
/// The Windows share sheet, for the one thing a song has that travels well: its
/// YouTube Music link. An unpackaged WinUI app has no CoreWindow, so the share
/// manager is reached through IDataTransferManagerInterop with the window handle;
/// if the shell refuses, the link is copied instead - the same result, one step
/// closer to hand.
/// </summary>
internal static class ShareLink
{
    private static DataTransferManager? _manager;
    private static IDataTransferManagerInterop? _interop;
    private static Song? _song;

    /// <summary>Show the share sheet for a song, on the window's UI thread.</summary>
    public static void Show(nint hwnd, Song song)
    {
        _song = song;
        try
        {
            if (_manager is null)
            {
                _interop = DataTransferManager.As<IDataTransferManagerInterop>();
                var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
                _interop.GetForWindow(hwnd, ref iid, out var abi);
                _manager = MarshalInterface<DataTransferManager>.FromAbi(abi);
                _manager.DataRequested += OnDataRequested;
            }

            _interop!.ShowShareUIForWindow(hwnd);
        }
        catch (Exception)
        {
            Copy(song);
        }
    }

    /// <summary>The link, in the clipboard, for when the share sheet is unavailable.</summary>
    public static void Copy(Song song)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(Link(song));
            Clipboard.SetContent(package);
            App.Shell?.Note("The link is on the clipboard.");
        }
        catch (Exception)
        {
            App.Shell?.Note("This song's link couldn't be shared.");
        }
    }

    private static string Link(Song song) => $"https://music.youtube.com/watch?v={song.Id}";

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var song = _song;
        if (song is null)
        {
            return;
        }

        var link = Link(song);
        var request = args.Request;
        request.Data.Properties.Title = song.Title;
        request.Data.Properties.Description = $"{song.Title} by {song.ArtistLine}";
        request.Data.SetWebLink(new Uri(link));
        request.Data.SetText($"{song.Title} — {song.ArtistLine}\n{link}");
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        void GetForWindow(nint appWindow, ref Guid riid, out nint dataTransferManager);

        void ShowShareUIForWindow(nint appWindow);
    }
}
