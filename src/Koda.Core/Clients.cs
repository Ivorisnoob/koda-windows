using System.Xml.Linq;

namespace Koda.Core;

/// <summary>
/// The InnerTube identities Koda speaks as, loaded from <c>Clients.xml</c> —
/// the single file to bump when YouTube retires versions. Clients.cs here is
/// the loader; the file is the data, exactly as the KodaCore Clients.swift
/// comment demands: versions pair with the bootstrap HTML of
/// music.youtube.com and www.youtube.com.
/// </summary>
public sealed class ClientProfiles
{
    public string ApiKey { get; }
    public string BrowserUserAgent { get; }
    public string WebVersion { get; }
    public string WebRemixName { get; }
    public string WebRemixVersion { get; }
    public string WebRemixClientHeader { get; }
    public StreamClient VisionOs { get; }

    public sealed record StreamClient(
        string Name,
        string Version,
        string HeaderId,
        string UserAgent,
        IReadOnlyDictionary<string, string> Fields);

    private static readonly Lazy<ClientProfiles> Lazy = new(() => Load
    (
        Path.Combine(AppContext.BaseDirectory, "Clients.xml")
    ));

    public static ClientProfiles Default => Lazy.Value;

    public static ClientProfiles Load(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException("Clients.xml has no root");
        return new ClientProfiles(
            apiKey: Required(root, "apiKey"),
            browserUserAgent: Required(root, "browserUserAgent"),
            webVersion: (string?)root.Element("web")?.Attribute("version") ?? throw new InvalidDataException("Clients.xml: missing web version"),
            webRemixName: (string?)root.Element("webRemix")?.Attribute("name") ?? "WEB_REMIX",
            webRemixVersion: (string?)root.Element("webRemix")?.Attribute("version") ?? throw new InvalidDataException("Clients.xml: missing webRemix version"),
            webRemixClientHeader: (string?)root.Element("webRemix")?.Attribute("clientNameHeader") ?? "67",
            visionOs: ReadVisionOs(root.Element("visionos")));
    }

    private ClientProfiles(string apiKey, string browserUserAgent, string webVersion,
        string webRemixName, string webRemixVersion, string webRemixClientHeader, StreamClient visionOs)
    {
        ApiKey = apiKey;
        BrowserUserAgent = browserUserAgent;
        WebVersion = webVersion;
        WebRemixName = webRemixName;
        WebRemixVersion = webRemixVersion;
        WebRemixClientHeader = webRemixClientHeader;
        VisionOs = visionOs;
    }

    private static StreamClient ReadVisionOs(XElement? element)
    {
        if (element is null) throw new InvalidDataException("Clients.xml: missing visionos client");
        var fields = new Dictionary<string, string>();
        foreach (var field in element.Elements("field"))
        {
            var key = (string?)field.Attribute("key");
            var value = (string?)field.Attribute("value");
            if (key is not null && value is not null) fields[key] = value;
        }
        return new StreamClient(
            Name: (string?)element.Attribute("name") ?? throw new InvalidDataException("Clients.xml: visionos name"),
            Version: (string?)element.Attribute("version") ?? throw new InvalidDataException("Clients.xml: visionos version"),
            HeaderId: (string?)element.Attribute("id") ?? throw new InvalidDataException("Clients.xml: visionos id"),
            UserAgent: (string?)element.Attribute("userAgent") ?? throw new InvalidDataException("Clients.xml: visionos userAgent"),
            Fields: fields);
    }

    private static string Required(XElement root, string attribute)
    {
        var value = (string?)root.Attribute(attribute);
        return value is not null ? value : throw new InvalidDataException($"Clients.xml: missing {attribute}");
    }
}
