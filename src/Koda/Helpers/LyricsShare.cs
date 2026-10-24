using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Koda.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Koda.Helpers;

/// <summary>
/// Pick a few lines of a song and share them as a card, the Mac's lyrics card: the lines in the
/// song's colours (or over its cover, or on near black), with the song and Koda at the foot.
/// The card is drawn by XAML and taken as a picture at twice its size, then copied or saved.
/// </summary>
public static class LyricsShare
{
    /// <summary>How many lines a card holds, as on the Mac.</summary>
    public const int MaxLines = 6;

    private enum Style
    {
        Colours,
        Cover,
        Dark,
    }

    private const double CardWidth = 340;

    public static async Task ShowAsync(Song song, IReadOnlyList<LyricLine> all, int startIndex)
    {
        if (App.Shell?.Content?.XamlRoot is not { } root)
        {
            return;
        }

        // Lines with words only; the "♪" placeholders of instrumental gaps are not lyrics.
        var lines = all.Select((line, index) => (line, index))
            .Where(x => x.line.Text.Trim().Length > 0 && x.line.Text.Trim() != "♪")
            .ToList();
        if (lines.Count == 0)
        {
            App.Shell?.Note("This song has no lines to share.");
            return;
        }

        var start = Math.Max(0, lines.FindIndex(x => x.index >= startIndex));
        var palette = await ArtAccent.PaletteAsync(song.Artwork) ?? [];
        var style = Style.Colours;

        // The card, as it is taken.
        var linesPanel = new StackPanel { Spacing = 6 };
        var card = new Grid { Width = CardWidth, CornerRadius = new CornerRadius(18) };
        var cardBackground = new Grid();
        card.Children.Add(cardBackground);
        var content = new StackPanel { Padding = new Thickness(24, 24, 24, 20), Spacing = 18 };
        content.Children.Add(Header(song));
        content.Children.Add(linesPanel);
        content.Children.Add(new TextBlock
        {
            Text = "Koda",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
        });
        card.Children.Add(content);

        BitmapImage? coverImage = song.Artwork?.Resized(600) is { } url ? new BitmapImage(url) { DecodePixelWidth = 600 } : null;

        void PaintBackground()
        {
            cardBackground.Children.Clear();
            switch (style)
            {
                case Style.Cover when coverImage is not null:
                    cardBackground.Children.Add(new Image { Source = coverImage, Stretch = Stretch.UniformToFill });
                    cardBackground.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) });
                    break;
                case Style.Dark:
                    cardBackground.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x12, 0x14)) });
                    break;
                default:
                    var colours = palette.Length >= 2 ? palette : [Accent(), Darker(Accent())];
                    var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
                    for (var i = 0; i < colours.Length; i++)
                    {
                        brush.GradientStops.Add(new GradientStop { Color = Darker(colours[i]), Offset = colours.Length == 1 ? 0 : (double)i / (colours.Length - 1) });
                    }

                    cardBackground.Children.Add(new Border { Background = brush });
                    break;
            }
        }

        // The song's lines, to choose from.
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = lines.Select(x => x.line.Text).ToArray(),
            Height = 380,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Lines to share");
        var hint = new TextBlock
        {
            Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        void RenderLines()
        {
            linesPanel.Children.Clear();
            var chosen = list.SelectedItems.Count;
            foreach (var index in Enumerable.Range(0, lines.Count).Where(i => list.SelectedRanges.Any(r => i >= r.FirstIndex && i <= r.LastIndex)))
            {
                linesPanel.Children.Add(new TextBlock
                {
                    Text = lines[index].line.Text,
                    FontFamily = new FontFamily("Segoe UI Variable Display"),
                    FontSize = 22,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.White),
                });
            }

            hint.Text = chosen >= MaxLines ? $"Up to {MaxLines} lines" : "Choose up to six lines";
        }

        var applying = false;
        list.SelectionChanged += (_, args) =>
        {
            if (applying) return;
            // A seventh line pushes nothing out: it is simply not taken.
            if (list.SelectedItems.Count > MaxLines)
            {
                applying = true;
                foreach (var added in args.AddedItems) list.SelectedItems.Remove(added);
                applying = false;
            }

            RenderLines();
        };

        var styles = new RadioButtons { MaxColumns = 3, Header = "Background" };
        foreach (var name in new[] { "Colours", "Cover", "Dark" }) styles.Items.Add(name);
        styles.SelectedIndex = 0;
        styles.SelectionChanged += (_, _) =>
        {
            style = (Style)Math.Max(0, styles.SelectedIndex);
            PaintBackground();
        };

        var left = new StackPanel { Spacing = 16, Width = CardWidth };
        left.Children.Add(new ScrollViewer { Content = card, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        left.Children.Add(styles);
        var right = new StackPanel { Spacing = 8, Width = 300 };
        right.Children.Add(hint);
        right.Children.Add(list);
        var layout = new Grid { ColumnSpacing = 24 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(left);
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Share Lyrics",
            Content = layout,
            PrimaryButtonText = "Copy image",
            SecondaryButtonText = "Save image…",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            Resources = { ["ContentDialogMaxWidth"] = 820.0 },
        };

        dialog.Opened += (_, _) =>
        {
            list.SelectedIndex = start;
            list.ScrollIntoView(list.Items[start]);
            RenderLines();
        };
        PaintBackground();

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (await RenderAsync(card) is { } png)
                {
                    var package = new DataPackage();
                    package.SetBitmap(RandomAccessStreamReference.CreateFromStream(await Stream(png)));
                    Clipboard.SetContent(package);
                    App.Shell?.Note("The lyrics card is on the clipboard.");
                }
            }
            catch (Exception error)
            {
                App.Shell?.Note($"The card couldn't be copied: {error.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        };

        dialog.SecondaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (await RenderAsync(card) is not { } png || App.Shell is not { } shell)
                {
                    return;
                }

                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{song.Title} lyrics" };
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
                picker.FileTypeChoices.Add("PNG image", [".png"]);
                // The picker opens over the dialog; the dialog waits for it.
                var file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    await File.WriteAllBytesAsync(file.Path, png);
                    App.Shell?.Note("The lyrics card was saved.");
                }
                else
                {
                    args.Cancel = true; // still choosing: the dialog stays
                }
            }
            catch (Exception error)
            {
                App.Shell?.Note($"The card couldn't be saved: {error.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // Another dialog is up; the share waits for the next time it's asked for.
        }
    }

    private static Grid Header(Song song)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cover = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        };
        if (song.Artwork?.Resized(88) is { } url)
        {
            cover.Child = new Image { Source = new BitmapImage(url) { DecodePixelWidth = 88 }, Stretch = Stretch.UniformToFill };
        }

        header.Children.Add(cover);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = song.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = song.ArtistLine,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        return header;
    }

    /// <summary>The card as a PNG at twice its drawn size, so it stays sharp wherever it is posted.</summary>
    private static async Task<byte[]?> RenderAsync(FrameworkElement card)
    {
        var target = new RenderTargetBitmap();
        var width = (int)Math.Ceiling(card.ActualWidth * 2);
        var height = (int)Math.Ceiling(card.ActualHeight * 2);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        await target.RenderAsync(card, width, height);
        var pixels = await target.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)target.PixelWidth, (uint)target.PixelHeight, 192, 192, pixels.ToArray());
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static async Task<InMemoryRandomAccessStream> Stream(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        return stream;
    }

    private static Color Accent() =>
        Application.Current.Resources["SystemAccentColor"] is Color accent ? accent : Color.FromArgb(0xFF, 0x3A, 0x5B, 0xA0);

    /// <summary>Deep enough that white lines read on it.</summary>
    private static Color Darker(Color colour) =>
        Color.FromArgb(0xFF, (byte)(colour.R * 0.62), (byte)(colour.G * 0.62), (byte)(colour.B * 0.62));
}
