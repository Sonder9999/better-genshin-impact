using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuestVoice.Core;

public static class NativeGlyphRenderer
{
    public static VisualFrame Render(string text, int fontPixels = 22, string font = "Yu Gothic", bool bold = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows GDI font rendering required.");
        if (text.Length > 128 || fontPixels is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(text));
        var dc = CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception();
        nint face = 0, bitmap = 0, oldFace = 0, oldBitmap = 0;
        try
        {
            face = CreateFontW(-fontPixels, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 4, 0, font);
            if (face == 0) throw new Win32Exception();
            oldFace = SelectObject(dc, face);
            if (!GetTextExtentPoint32W(dc, text, text.Length, out var size)) throw new Win32Exception();
            var width = Math.Max(8, size.Width + 8); var height = Math.Max(fontPixels + 8, size.Height + 8);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out var pixels, 0, 0);
            if (bitmap == 0 || pixels == 0) throw new Win32Exception();
            oldBitmap = SelectObject(dc, bitmap);
            var bgra = new byte[width * height * 4]; Marshal.Copy(bgra, 0, pixels, bgra.Length);
            SetBkColor(dc, 0); SetTextColor(dc, 0x00ffffff); SetBkMode(dc, 2);
            if (!TextOutW(dc, 4, 4, text, text.Length)) throw new Win32Exception();
            GdiFlush(); Marshal.Copy(pixels, bgra, 0, bgra.Length);
            var rgb = new byte[width * height * 3];
            for (var i = 0; i < width * height; i++)
            { rgb[i * 3] = bgra[i * 4 + 2]; rgb[i * 3 + 1] = bgra[i * 4 + 1]; rgb[i * 3 + 2] = bgra[i * 4]; }
            return new(width, height, rgb, 0);
        }
        finally
        {
            if (oldBitmap != 0) SelectObject(dc, oldBitmap);
            if (oldFace != 0) SelectObject(dc, oldFace);
            if (bitmap != 0) DeleteObject(bitmap);
            if (face != 0) DeleteObject(face);
            DeleteDC(dc);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int X, Y; public uint Colors, Important, Rgb; }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int e, int o, int weight, uint italic, uint under, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(nint dc, string text, int count, out Size size);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(nint dc, int x, int y, string text, int count);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
