using System.IO;
using Koda.Core;

namespace Koda.Services;

/// <summary>
/// One engine instance for the whole process: the shell and every page share
/// it. The visitor identity persists to engine/visitor.json under the data root.
/// </summary>
public static class EngineHost
{
    private static YouTubeMusic? _music;

    public static YouTubeMusic Music =>
        _music ??= new YouTubeMusic(
            visitorStorePath: Path.Combine(App.DataDirectory, "engine", "visitor.json"));
}
