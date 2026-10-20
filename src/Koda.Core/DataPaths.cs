namespace Koda.Core;

/// <summary>
/// Where Koda keeps its state: settings, library, caches, downloaded audio.
/// <c>KODA_DATA</c> points it somewhere else - a portable install keeps its data
/// next to the app - and without it the app lands in the user's local app data.
/// </summary>
public static class DataPaths
{
    public static string Root =>
        Environment.GetEnvironmentVariable("KODA_DATA") is { Length: > 0 } env ? env : DefaultRoot;

    /// <summary>The default home: per-user local app data, on whichever drive that is.</summary>
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Koda");

    public static string EngineDirectory
    {
        get
        {
            var dir = Path.Combine(Root, "engine");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
