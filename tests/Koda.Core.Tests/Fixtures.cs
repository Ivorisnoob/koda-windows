using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>Fixture files live beside the test binary, copied from recorded responses.</summary>
public static class Fixtures
{
    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Bytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static Core.Json Load(string name) => Core.Json.Parse(Text(name));

    /// <summary>Throwaway files for a test, beside the test binary.</summary>
    public static string Scratch
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "scratch");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
