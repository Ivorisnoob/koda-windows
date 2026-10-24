using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Koda.Core;

namespace Koda.Helpers;

/// <summary>
/// Importing a Koda for Android backup, from the Library page or from Settings: the file
/// picker, the import itself, and one line that says what came across. Nothing here replaces
/// what is already in the library.
/// </summary>
public static class AndroidImport
{
    /// <summary>What Settings says under the row, as the Mac does.</summary>
    public const string Explanation =
        "Adds the liked songs, playlists, saved albums and playlists, plays and searches from a Koda for Android backup. Nothing here is replaced, and importing the same backup twice adds nothing twice.";

    public static async Task RunAsync()
    {
        if (App.Shell is not { } shell)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".koda");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(file.Path);
            var summary = App.Library.ImportAndroid(AndroidBackup.Read(data));
            shell.Note(summary.IsEmpty ? "That backup had nothing new for this library." : Describe(summary));
        }
        catch (AndroidBackup.ReadException error)
        {
            shell.Note(error.Message);
        }
        catch (Exception error)
        {
            shell.Note($"The backup couldn't be read: {error.Message}");
        }
    }

    private static string Describe(AndroidImportSummary s)
    {
        var parts = new List<string>();
        if (s.LikedSongs > 0) parts.Add($"{s.LikedSongs} liked songs");
        if (s.Playlists > 0) parts.Add($"{s.Playlists} playlists");
        if (s.Saved > 0) parts.Add($"{s.Saved} saved albums and playlists");
        if (s.Plays > 0) parts.Add($"{s.Plays} plays");
        if (s.Searches > 0) parts.Add($"{s.Searches} searches");
        return "Imported " + string.Join(", ", parts) + ".";
    }
}
