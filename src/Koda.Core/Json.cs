using System.Globalization;
using System.Text.Json;

namespace Koda.Core;

/// <summary>
/// A read-only view over an InnerTube JSON tree.
/// InnerTube nests the same renderer at different depths across response
/// variants, so parsers walk the tree by key rather than decode fixed models.
/// Every accessor is total: a missing key reads as <see cref="Null"/>.
/// Ported from the KodaCore JSON.swift contract. [judgement: System.Text.Json
/// walks document order natively, which the Swift port's sorted-key walk
/// approximated for determinism]
/// </summary>
public readonly struct Json : IEquatable<Json>
{
    private readonly JsonElement _raw;

    public Json(JsonElement raw) => _raw = raw;

    public static Json Null => default;

    /// <summary>The root is cloned once, so it owns its document; every view below it
    /// stays valid without cloning again (cloning at each step of a walk is O(n * depth)).</summary>
    public static Json Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return new Json(doc.RootElement.Clone());
    }

    public Json this[string key]
    {
        get
        {
            if (_raw.ValueKind != JsonValueKind.Object) return Null;
            return _raw.TryGetProperty(key, out var value) ? new Json(value) : Null;
        }
    }

    public Json this[int index]
    {
        get
        {
            if (_raw.ValueKind != JsonValueKind.Array || (uint)index >= (uint)_raw.GetArrayLength()) return Null;
            return new Json(_raw[index]);
        }
    }

    /// <summary>The underlying element; Undefined when this is a detached (missing) view.</summary>
    public JsonElement Raw => _raw;

    /// <summary>Present and not JSON null.</summary>
    public bool Exists => _raw.ValueKind != JsonValueKind.Undefined && _raw.ValueKind != JsonValueKind.Null;

    public bool IsObject => _raw.ValueKind == JsonValueKind.Object;

    public string? String => _raw.ValueKind == JsonValueKind.String ? _raw.GetString() : null;

    public int? Int => Long is { } l && l is >= int.MinValue and <= int.MaxValue ? (int?)l : null;

    /// <summary>An exact integer: <c>GetInt64</c> for numbers (no double round-trip), or a parseable numeric string.</summary>
    public long? Long => _raw.ValueKind switch
    {
        JsonValueKind.Number => _raw.TryGetInt64(out var value) ? value : null,
        JsonValueKind.String when String is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
        _ => null,
    };

    /// <summary>A number, or a numeric string (InnerTube uses both freely).</summary>
    public double? Double
    {
        get
        {
            switch (_raw.ValueKind)
            {
                case JsonValueKind.Number:
                    return _raw.GetDouble();
                case JsonValueKind.String when String is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v):
                    return v;
                default:
                    return null;
            }
        }
    }

    public bool? Bool => _raw.ValueKind == JsonValueKind.True ? true : _raw.ValueKind == JsonValueKind.False ? false : null;

    public Json[] Array
    {
        get
        {
            if (_raw.ValueKind != JsonValueKind.Array) return [];
            var items = new Json[_raw.GetArrayLength()];
            for (var i = 0; i < items.Length; i++) items[i] = new Json(_raw[i]);
            return items;
        }
    }

    /// <summary>A non-empty, whitespace-trimmed string, else null.</summary>
    public string? NonEmpty
    {
        get
        {
            var s = String?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }

    /// <summary>The first key of an object, for renderer wrappers like <c>{musicTwoRowItemRenderer}</c>.</summary>
    public string? FirstKey
    {
        get
        {
            if (_raw.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in _raw.EnumerateObject()) return prop.Name;
            return null;
        }
    }

    /// <summary>
    /// Every object stored under <paramref name="key"/> anywhere below this node,
    /// in document order. The search does not descend into a match, so nested
    /// renderers of the same kind are not repeated.
    /// </summary>
    public Json[] Objects(string key)
    {
        var found = new List<Json>();
        CollectObjects(_raw, key, found);
        return [.. found];
    }

    /// <summary>The first object under <paramref name="key"/> anywhere below this node.</summary>
    public Json First(string key)
    {
        var found = Objects(key);
        return found.Length > 0 ? found[0] : Null;
    }

    /// <summary>Every string value stored under <paramref name="key"/> anywhere below this node.</summary>
    public string[] Strings(string key)
    {
        var found = new List<string>();
        CollectStrings(_raw, key, found);
        return [.. found];
    }

    private static void CollectObjects(JsonElement node, string key, List<Json> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Name == key && prop.Value.ValueKind == JsonValueKind.Object)
                        found.Add(new Json(prop.Value));
                    else
                        CollectObjects(prop.Value, key, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    CollectObjects(item, key, found);
                break;
        }
    }

    private static void CollectStrings(JsonElement node, string key, List<string> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Name == key && prop.Value.ValueKind == JsonValueKind.String)
                        found.Add(prop.Value.GetString()!);
                    else
                        CollectStrings(prop.Value, key, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    CollectStrings(item, key, found);
                break;
        }
    }

    private bool Present() => _raw.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    public bool Equals(Json other)
    {
        if (Present() != other.Present()) return false;
        if (!Present()) return true;
        // Structural whitespace-insensitive compare, as the Swift port drew over
        // decoded dictionaries; document order is compared as-is.
        return JsonNodeDeepEquals(_raw, other._raw);
    }

    private bool JsonNodeDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var bProps = new Dictionary<string, JsonElement>();
                    foreach (var prop in b.EnumerateObject()) bProps[prop.Name] = prop.Value;
                    var count = 0;
                    foreach (var prop in a.EnumerateObject())
                    {
                        count++;
                        if (!bProps.TryGetValue(prop.Name, out var bValue)) return false;
                        if (!JsonNodeDeepEquals(prop.Value, bValue)) return false;
                    }
                    return count == bProps.Count;
                }
            case JsonValueKind.Array:
                {
                    if (a.GetArrayLength() != b.GetArrayLength()) return false;
                    var bArray = b.EnumerateArray().ToArray();
                    var i = 0;
                    foreach (var item in a.EnumerateArray())
                        if (!JsonNodeDeepEquals(item, bArray[i++])) return false;
                    return true;
                }
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    public override int GetHashCode()
    {
        if (!Present()) return 0;
        var canonical = new System.Text.StringBuilder();
        Canonicalize(_raw, canonical);
        return StringExtensions.StableHash(canonical.ToString());
    }

    /// <summary>Whitespace-free rebuild so the hash matches the structural equality.</summary>
    private static void Canonicalize(JsonElement element, System.Text.StringBuilder into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                into.Append('{');
                var first = true;
                foreach (var prop in element.EnumerateObject())
                {
                    if (!first) into.Append(',');
                    first = false;
                    into.Append(System.Text.Json.JsonEncodedText.Encode(prop.Name).ToString()).Append(':');
                    Canonicalize(prop.Value, into);
                }
                into.Append('}');
                break;
            case JsonValueKind.Array:
                into.Append('[');
                var itemFirst = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!itemFirst) into.Append(',');
                    itemFirst = false;
                    Canonicalize(item, into);
                }
                into.Append(']');
                break;
            default:
                into.Append(element.GetRawText());
                break;
        }
    }

    public override string ToString() => Present() ? _raw.GetRawText() : "<null>";
}

// MARK: - InnerTube text, images and links

public static class JsonText
{
    /// <summary>The text runs of a formatted string.</summary>
    public static Json[] Runs(this Json json) => json["runs"].Array;

    /// <summary>A formatted string's plain text: <c>simpleText</c>, or its runs joined.</summary>
    public static string Text(this Json json)
    {
        var simple = json["simpleText"].String;
        if (simple is not null) return simple;
        return string.Concat(json["runs"].Array.Select(r => r["text"].String));
    }

    /// <summary>The widest image of the first <c>thumbnails</c> array found below this node.</summary>
    public static Artwork? BestThumbnail(this Json json)
    {
        var list = FirstThumbnailList(json);
        if (list is not { Length: > 0 }) return null;
        var best = list.MaxBy(t => (t["width"].Int ?? 0));
        var url = best["url"].String is { } s ? Urls.Image(s) : null;
        return url is null ? null : new Artwork(url, best["width"].Int ?? 0, best["height"].Int ?? 0);
    }

    private static Json[]? FirstThumbnailList(Json json)
    {
        if (json.IsObject)
        {
            if (json["thumbnails"].Array is { Length: > 0 } thumbs) return thumbs;
            foreach (var prop in json.Raw.EnumerateObject())
            {
                if (prop.Name == "overlay") continue; // a nested video overlay is not this item's art
                if (FirstThumbnailList(new Json(prop.Value)) is { } found) return found;
            }
            return null;
        }
        if (json.Raw.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.Raw.EnumerateArray())
                if (FirstThumbnailList(new Json(item)) is { } found) return found;
        }
        return null;
    }
}

public static class Urls
{
    /// <summary>InnerTube image URLs, which are sometimes protocol-relative.</summary>
    public static Uri? Image(string url)
    {
        var s = url.StartsWith("//") ? "https:" + url : url;
        return Uri.TryCreate(s, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? parsed : null;
    }
}

public static class StringExtensions
{
    public static string? TrimmedOrNull(this string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>Python-style polynomial rolling hash: stable across runs without string.GetHashCode randomization.</summary>
    public static int StableHash(string s)
    {
        unchecked
        {
            var h = 0;
            foreach (var c in s) h = 31 * h + c;
            return h;
        }
    }
}
