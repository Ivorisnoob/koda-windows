using System;
using Koda.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Koda.Helpers;

/// <summary>
/// Cover art on an Image: asked from Google at the size it is drawn (decoded at that
/// size too, so long lists stay light), faded in once it arrives, and left transparent
/// over the placeholder until then or when it fails. Recycled rows drop a late image
/// that belongs to the row's previous item.
///
/// <c>helpers:Art.Source="{Binding Artwork}"</c>, optional <c>helpers:Art.Pixels</c>
/// (drawn size in DIPs) and <c>helpers:Art.Soft</c> (decoded tiny and stretched: the
/// blurred backdrop of an artist header, with no effects library).
/// </summary>
public static class Art
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(object), typeof(Art), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty PixelsProperty = DependencyProperty.RegisterAttached(
        "Pixels", typeof(int), typeof(Art), new PropertyMetadata(96));

    public static readonly DependencyProperty SoftProperty = DependencyProperty.RegisterAttached(
        "Soft", typeof(bool), typeof(Art), new PropertyMetadata(false));

    public static object? GetSource(DependencyObject d) => d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, object? value) => d.SetValue(SourceProperty, value);
    public static int GetPixels(DependencyObject d) => (int)d.GetValue(PixelsProperty);
    public static void SetPixels(DependencyObject d, int value) => d.SetValue(PixelsProperty, value);
    public static bool GetSoft(DependencyObject d) => (bool)d.GetValue(SoftProperty);
    public static void SetSoft(DependencyObject d, bool value) => d.SetValue(SoftProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is not Image image)
        {
            return;
        }

        // [scar] WinUI throws (UnauthorizedAccess) setting OpacityTransition on an element that
        // has a RenderTransform; such an image simply appears without the fade.
        var fades = image.RenderTransform is null;
        if (fades)
        {
            image.OpacityTransition ??= new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        }

        image.Opacity = fades ? 0 : 1;
        if (args.NewValue is not Artwork artwork)
        {
            image.Source = null;
            return;
        }

        var soft = GetSoft(image);
        var pixels = soft ? 24 : Math.Max(24, GetPixels(image));
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = pixels,
        };
        bitmap.ImageOpened += (_, _) =>
        {
            if (image.Source == bitmap)
            {
                image.Opacity = 1;
            }
        };
        // Twice the drawn size for high-DPI screens; Google scales on its side.
        bitmap.UriSource = artwork.Resized(soft ? 60 : pixels * 2);
        image.Source = bitmap;
    }
}
