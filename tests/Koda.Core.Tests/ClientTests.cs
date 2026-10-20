using Koda.Core;

namespace Koda.Core.Tests;

public class ClientTests
{
    [Fact]
    public void ClientsXmlLoadsFromOutput()
    {
        var clients = ClientProfiles.Default;

        // The public web key, not a secret: it names only the client type.
        Assert.Equal("AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8", clients.ApiKey);
        Assert.Equal("WEB_REMIX", clients.WebRemixName);
        Assert.StartsWith("1.2026", clients.WebRemixVersion);
        Assert.StartsWith("2.2026", clients.WebVersion);
        Assert.Equal("67", clients.WebRemixClientHeader);
        Assert.Contains("Chrome/124.0.0.0", clients.BrowserUserAgent);
    }

    [Fact]
    public void VisionOsClientCarriesThePlayerIdentity()
    {
        var client = ClientProfiles.Default.VisionOs;

        Assert.Equal("VISIONOS", client.Name);
        Assert.Equal("1.02", client.Version);
        Assert.Equal("101", client.HeaderId);
        Assert.StartsWith("com.google.visionos.youtube/", client.UserAgent);
        Assert.Equal("WATCH", client.Fields["clientScreen"]);
        Assert.Equal("MOBILE", client.Fields["platform"]);
        Assert.Equal("Apple", client.Fields["deviceMake"]);
        Assert.Equal("RealityDevice14,1", client.Fields["deviceModel"]);
        Assert.Equal("visionOS", client.Fields["osName"]);
    }

    [Fact]
    public void DataPathsFollowsTheEnvironmentOverride()
    {
        var saved = Environment.GetEnvironmentVariable("KODA_DATA");
        try
        {
            Environment.SetEnvironmentVariable("KODA_DATA", null);
            Assert.Equal(DataPaths.DefaultRoot, DataPaths.Root);

            var overridePath = Path.Combine(Fixtures.Scratch, "data-root");
            Environment.SetEnvironmentVariable("KODA_DATA", overridePath);
            Assert.Equal(overridePath, DataPaths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KODA_DATA", saved);
        }
    }
}
