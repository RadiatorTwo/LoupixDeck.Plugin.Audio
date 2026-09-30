using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Reads the shell icon of an executable as plain pixels, so the mixer tile can draw it into its own
/// framebuffer. Uses the Win32 shell and GDI directly instead of System.Drawing, which would add a
/// package to a plugin that otherwise has none.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsIconExtractor
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint DibRgbColors = 0;
    private const int MaxIconEdge = 256;

    /// <summary>The large shell icon (normally 32 x 32) as 0xAARRGGBB pixels, or null when it cannot be read.</summary>
    public static (uint[] Pixels, int Size)? TryExtract(string path)
    {
        SHFILEINFO info = default;
        IntPtr result = SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        using SafeIconHandle icon = new(info.hIcon);
        if (!GetIconInfo(icon, out ICONINFO iconInfo)) return null;

        using SafeBitmapHandle color = new(iconInfo.hbmColor);
        using SafeBitmapHandle mask = new(iconInfo.hbmMask);

        // A monochrome icon has no colour bitmap; nothing useful to draw.
        if (color.IsInvalid) return null;
        if (GetObjectW(color, Marshal.SizeOf<BITMAP>(), out BITMAP bitmap) == 0) return null;

        int size = bitmap.bmWidth;
        if (size <= 0 || size != bitmap.bmHeight || size > MaxIconEdge) return null;

        byte[]? bgra = ReadPixels(color, size);
        if (bgra == null) return null;

        bool hasAlpha = false;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        // Icons from before the alpha channel keep their shape in the AND mask instead.
        byte[]? maskBits = hasAlpha || mask.IsInvalid ? null : ReadPixels(mask, size);

        uint[] pixels = new uint[size * size];
        for (int p = 0; p < pixels.Length; p++)
        {
            int i = p * 4;
            uint alpha = hasAlpha
                ? bgra[i + 3]
                : maskBits != null && maskBits[i] == 0 && maskBits[i + 1] == 0 && maskBits[i + 2] == 0 ? 255u : 0u;
            pixels[p] = (alpha << 24) | ((uint)bgra[i + 2] << 16) | ((uint)bgra[i + 1] << 8) | bgra[i];
        }

        return (pixels, size);
    }

    private static byte[]? ReadPixels(SafeBitmapHandle bitmap, int size)
    {
        BITMAPINFOHEADER header = new()
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // negative: rows top to bottom
            biPlanes = 1,
            biBitCount = 32
        };

        byte[] bits = new byte[size * size * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;

        try
        {
            return GetDIBits(screen, bitmap, 0, (uint)size, bits, ref header, DibRgbColors) == size ? bits : null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle(IntPtr icon) : base(true) => SetHandle(icon);

        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    private sealed class SafeBitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeBitmapHandle(IntPtr bitmap) : base(true) => SetHandle(bitmap);

        protected override bool ReleaseHandle() => DeleteObject(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint fileAttributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetIconInfo(SafeIconHandle icon, out ICONINFO info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetObjectW(SafeBitmapHandle handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDIBits(IntPtr dc, SafeBitmapHandle bitmap, uint startLine, uint lines, byte[] bits,
        ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr handle);
}
