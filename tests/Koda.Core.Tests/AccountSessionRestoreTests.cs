using System.Text.Json;

namespace Koda.Core.Tests;

/// <summary>A saved session has to come back: a restart and a profile switch both read it.</summary>
public class AccountSessionRestoreTests
{
    [Fact]
    public void SavedCookiesReadBackAsASignedInSession()
    {
        var cookies = new YouTubeCookies(
        [
            new YouTubeCookies.Cookie("SID", "sid-value"),
            new YouTubeCookies.Cookie("HSID", "hsid-value"),
            new YouTubeCookies.Cookie("SSID", "ssid-value"),
            new YouTubeCookies.Cookie("APISID", "apisid-value"),
            new YouTubeCookies.Cookie("SAPISID", "sapisid-value"),
        ]);

        var stored = JsonSerializer.Serialize(cookies, AccountJson.Options);
        var restored = JsonSerializer.Deserialize<YouTubeCookies>(stored, AccountJson.Options);

        Assert.NotNull(restored);
        Assert.True(restored!.LooksSignedIn);
        Assert.Equal(cookies, restored);
    }
}
