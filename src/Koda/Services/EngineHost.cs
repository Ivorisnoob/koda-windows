using System.IO;
using Koda.Core;
using Koda.Interop;

namespace Koda.Services;

/// <summary>
/// One engine instance for the whole process: the shell and every page share
/// it. The visitor identity and player signature persist under engine/ in the data root.
/// </summary>
public static class EngineHost
{
    private static YouTubeMusic? _music;
    private static YouTubeAccount? _account;

    public static YouTubeMusic Music =>
        _music ??= new YouTubeMusic(
            visitorStorePath: Path.Combine(App.DataDirectory, "engine", "visitor.json"),
            signatureStorePath: Path.Combine(App.DataDirectory, "engine", "player-signature.json"));

    /// <summary>The library's YouTube Music account: the session in the Windows Credential
    /// Manager, the account's name and the sync mirror in the data root.</summary>
    public static YouTubeAccount Account =>
        _account ??= new YouTubeAccount(
            Music,
            App.Library,
            App.Settings,
            new CredentialStore("Koda/YouTube"),
            App.DataDirectory);
}
