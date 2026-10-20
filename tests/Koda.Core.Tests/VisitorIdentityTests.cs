using Koda.Core.InnerTube;

namespace Koda.Core.Tests;

public class VisitorIdentityTests : IDisposable
{
    private readonly string _storePath;

    public VisitorIdentityTests()
    {
        // Beside the test binary, never Path.GetTempPath() (that is the user's temp folder).
        _storePath = Path.Combine(Fixtures.Scratch, $"koda-visitor-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { File.Delete(_storePath); }
        catch (IOException)
        {
        }
    }

    private VisitorIdentity OfflineIdentity() =>
        new(ClientProfiles.Default, http: new HttpClient(FailingHttpHandler.Instance), storePath: _storePath);

    [Fact]
    public async Task CachedAnswersTheStoredTokenWithoutMinting()
    {
        using var identity = OfflineIdentity();
        Assert.Null(await identity.CachedAsync());
    }

    [Fact]
    public async Task ReplaceOnAnUnreachableNetworkAnswersNullAndClears()
    {
        // A stored token that gets refused must not resurrect on the next launch:
        // the replacement mint failing still clears the store.
        File.WriteAllText(_storePath,
            """{"token":"refused-token","mintedAtUtc":"2026-10-01T00:00:00Z","vetted":"refused-token"}""");
        using var identity = OfflineIdentity();
        Assert.Equal("refused-token", await identity.CachedAsync());
        Assert.Null(await identity.ReplaceAsync("refused-token"));
        var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(
            await File.ReadAllTextAsync(_storePath));
        var token = state?["token"];
        Assert.True(token is null || token.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void VettedMarksPersist()
    {
        using var identity = OfflineIdentity();
        Assert.False(identity.IsVetted("token-a"));
        identity.MarkVetted("token-a");
        Assert.True(identity.IsVetted("token-a"));
        Assert.False(identity.IsVetted("token-b"));

        using var reloaded = OfflineIdentity();
        Assert.True(reloaded.IsVetted("token-a"));
    }

    [Fact]
    public async Task MintReadsVisitorDataAndCleansThePadding()
    {
        using var http = new HttpClient(new RecordingHttpHandler());
        var identity = new VisitorIdentity(ClientProfiles.Default, http, storePath: _storePath);
        try
        {
            var token = await identity.CurrentAsync();
            Assert.Equal("TESTTOKEN==", token);
            Assert.Equal("TESTTOKEN==", await identity.CachedAsync());
        }
        finally
        {
            identity.Dispose();
        }
    }
}
