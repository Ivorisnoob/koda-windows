using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Koda.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Koda.Helpers;

/// <summary>
/// The art-adaptive accent: one colour drawn from a cover, the
/// most vivid of its well-lit pixels, so the Now Playing page takes the album's tint.
/// Null when the art cannot be read; the system accent stands in then.
/// </summary>
public static class ArtAccent
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, Color?> Cache = new();

    public static async Task<Color?> FromAsync(Artwork? artwork)
    {
        if (artwork is null)
        {
            return null;
        }

        var url = artwork.Resized(48);
        if (Cache.TryGetValue(url.ToString(), out var known))
        {
            return known;
        }

        Color? color;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            color = Pick(pixels.DetachPixelData());
        }
        catch (Exception)
        {
            color = null;
        }

        Cache[url.ToString()] = color;
        return color;
    }

    private static readonly ConcurrentDictionary<string, Color[]> Palettes = new();

    /// <summary>
    /// Three colours from a cover for the Now Playing background, strongest first: the
    /// pixels binned by hue, each bin weighted by how many pixels and how vivid, and
    /// the top three bins averaged. All are kept dark enough for white text on them.
    /// A plain cover gives three shades of its own colour.
    /// </summary>
    public static async Task<Color[]?> PaletteAsync(Artwork? artwork)
    {
        if (artwork is null) return null;
        var url = artwork.Resized(48).ToString();
        if (Palettes.TryGetValue(url, out var known)) return known;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = (await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

            const int bins = 12;
            var weight = new double[bins + 1];
            var sum = new (double R, double G, double B)[bins + 1];
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var light = (max + min) / 2;
                var sat = max == min ? 0 : (max - min) / (1 - Math.Abs(2 * light - 1));
                // Greys share one bin of their own so a monochrome cover still has a palette.
                var bin = sat < 0.15 ? bins : (int)(Hue(r, g, b, max, min) / 360 * bins) % bins;
                var w = 0.2 + sat;
                weight[bin] += w;
                sum[bin] = (sum[bin].R + r * w, sum[bin].G + g * w, sum[bin].B + b * w);
            }

            var top = Enumerable.Range(0, bins + 1).Where(i => weight[i] > 0)
                .OrderByDescending(i => weight[i]).Take(3)
                .Select(i => Dark(sum[i].R / weight[i], sum[i].G / weight[i], sum[i].B / weight[i])).ToList();
            while (top.Count < 3 && top.Count > 0)
            {
                var c = top[^1];
                top.Add(Color.FromArgb(255, (byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7)));
            }

            var palette = top.Count == 0 ? null : top.ToArray();
            if (palette is not null) Palettes[url] = palette;
            return palette;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        if (max == min) return 0;
        var d = max - min;
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    /// <summary>The colour with its lightness held at or under 0.42, so white text reads on it.</summary>
    private static Color Dark(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var scale = max > 0.62 ? 0.62 / max : 1;
        return Color.FromArgb(255, (byte)(r * scale * 255), (byte)(g * scale * 255), (byte)(b * scale * 255));
    }

    /// <summary>Weighted toward saturated mid-tones; a grey cover yields its own grey.</summary>
    private static Color? Pick(byte[] bgra)
    {
        double r = 0, g = 0, b = 0, weight = 0, plainR = 0, plainG = 0, plainB = 0;
        var count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            double bl = bgra[i] / 255.0, gr = bgra[i + 1] / 255.0, re = bgra[i + 2] / 255.0;
            var max = Math.Max(re, Math.Max(gr, bl));
            var min = Math.Min(re, Math.Min(gr, bl));
            var light = (max + min) / 2;
            var saturation = max == min ? 0 : (max - min) / (1 - Math.Abs(2 * light - 1));
            var w = saturation * saturation * (1 - Math.Abs(light - 0.5) * 1.6);
            if (w > 0)
            {
                r += re * w;
                g += gr * w;
                b += bl * w;
                weight += w;
            }

            plainR += re;
            plainG += gr;
            plainB += bl;
            count++;
        }

        if (count == 0) return null;
        return weight > 0.5
            ? Color.FromArgb(255, (byte)(r / weight * 255), (byte)(g / weight * 255), (byte)(b / weight * 255))
            : Color.FromArgb(255, (byte)(plainR / count * 255), (byte)(plainG / count * 255), (byte)(plainB / count * 255));
    }

    private static IBuffer AsBuffer(this byte[] bytes) =>
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes);
}
