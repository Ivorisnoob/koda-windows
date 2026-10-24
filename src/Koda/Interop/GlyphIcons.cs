using System;
using System.Runtime.InteropServices;

namespace Koda.Interop;

/// <summary>
/// Small HICONs drawn from Segoe Fluent Icons for the taskbar's thumbnail toolbar.
/// The shell wants icons, not XAML, so the glyphs are rendered into a colour bitmap
/// and a matching mask: white glyph on the colour bitmap, black glyph on the mask,
/// which is the shape Windows reads as "this pixel is the icon".
/// </summary>
internal static class GlyphIcons
{
    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_NOPREFIX = 0x00000800;
    private const int TRANSPARENT = 1;
    private const uint WHITENESS = 0x00FF0062;
    private const uint FW_NORMAL = 400;
    private const uint DEFAULT_CHARSET = 1;
    private const uint OUT_TT_PRECIS = 4;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint ANTIALIASED_QUALITY = 4;
    private const uint DEFAULT_PITCH = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Color;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, nint bits);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rect, nint brush);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint hdc, string text, int count, ref RECT rect, uint format);

    [DllImport("gdi32.dll")]
    private static extern int PatBlt(nint hdc, int x, int y, int width, int height, uint rop);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO info);

    /// <summary>Draw <paramref name="glyph"/> into a 32px icon; the caller owns the HICON.</summary>
    public static nint Create(string glyph, int size = 32)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            return 0;
        }

        var colorDc = CreateCompatibleDC(screen);
        var maskDc = CreateCompatibleDC(screen);
        var color = CreateCompatibleBitmap(screen, size, size);
        var mask = CreateBitmap(size, size, 1, 1, 0);
        var font = CreateFontW(-(int)Math.Round(size * 0.7), 0, 0, 0, (int)FW_NORMAL, 0, 0, 0,
            DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
            DEFAULT_PITCH, "Segoe Fluent Icons");

        if (colorDc == 0 || maskDc == 0 || color == 0 || mask == 0 || font == 0)
        {
            if (font != 0) DeleteObject(font);
            if (color != 0) DeleteObject(color);
            if (mask != 0) DeleteObject(mask);
            if (colorDc != 0) DeleteDC(colorDc);
            if (maskDc != 0) DeleteDC(maskDc);
            ReleaseDC(0, screen);
            return 0;
        }

        var oldColor = SelectObject(colorDc, color);
        var oldMask = SelectObject(maskDc, mask);
        var oldColorFont = SelectObject(colorDc, font);
        var oldMaskFont = SelectObject(maskDc, font);
        SetBkMode(colorDc, TRANSPARENT);
        SetBkMode(maskDc, TRANSPARENT);
        PatBlt(colorDc, 0, 0, size, size, 0x00000042 /* BLACKNESS */);
        PatBlt(maskDc, 0, 0, size, size, WHITENESS);

        var rect = new RECT { Left = 0, Top = 0, Right = size, Bottom = size };
        SetTextColor(colorDc, 0x00FFFFFF);
        DrawTextW(colorDc, glyph, -1, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SetTextColor(maskDc, 0x00000000);
        DrawTextW(maskDc, glyph, -1, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        SelectObject(colorDc, oldColorFont);
        SelectObject(maskDc, oldMaskFont);
        SelectObject(colorDc, oldColor);
        SelectObject(maskDc, oldMask);

        var info = new ICONINFO
        {
            IsIcon = true,
            Mask = mask,
            Color = color,
        };
        var icon = CreateIconIndirect(ref info);

        DeleteObject(font);
        DeleteObject(color);
        DeleteObject(mask);
        DeleteDC(colorDc);
        DeleteDC(maskDc);
        ReleaseDC(0, screen);
        return icon;
    }
}
