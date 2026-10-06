using System.Buffers.Binary;
using System.IO.Compression;

namespace LoupixDeck.Plugin.Audio.Rendering;

/// <summary>
/// Minimal PNG reader for application icons on Linux, the counterpart of <see cref="PngEncoder"/>:
/// the mixer tile draws icons into its own framebuffer, so it needs pixels, and the plugin carries
/// no imaging library. Covers what icon themes ship — every colour type at every bit depth, with
/// tRNS transparency — but not Adam7 interlacing, which icons practically never use.
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Larger images are refused rather than decoded, since an icon never is this big.</summary>
    private const int MaxEdge = 2048;

    /// <summary>
    /// Decodes <paramref name="png"/> into 0xAARRGGBB pixels (straight alpha, row-major), or
    /// returns null when the data is not a PNG this reader handles.
    /// </summary>
    public static (uint[] Pixels, int Width, int Height)? TryDecode(ReadOnlySpan<byte> png)
    {
        try
        {
            return Decode(png);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException
                                       or OverflowException)
        {
            return null;
        }
    }

    private static (uint[] Pixels, int Width, int Height)? Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature)) return null;

        int width = 0, height = 0, bitDepth = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? transparency = null;
        using MemoryStream idat = new();

        int offset = Signature.Length;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length) return null;

            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            offset += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                // Compression and filter method are always 0; anything else is not a PNG we know.
                if (data[10] != 0 || data[11] != 0 || data[12] != 0) return null;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (width <= 0 || height <= 0 || width > MaxEdge || height > MaxEdge) return null;

        int channels = colorType switch
        {
            0 => 1, // grey
            2 => 3, // RGB
            3 => 1, // palette index
            4 => 2, // grey + alpha
            6 => 4, // RGBA
            _ => 0
        };
        if (channels == 0 || !ValidDepth(colorType, bitDepth)) return null;
        if (colorType == 3 && palette == null) return null;

        int bitsPerPixel = channels * bitDepth;
        int stride = (width * bitsPerPixel + 7) / 8;
        // The filters work on whole bytes: the byte "to the left" is one pixel back, at least one byte.
        int filterStep = Math.Max(1, bitsPerPixel / 8);

        byte[] raw = new byte[height * (stride + 1)];
        idat.Position = 0;
        using (ZLibStream inflate = new(idat, CompressionMode.Decompress))
        {
            inflate.ReadExactly(raw);
        }

        uint[] pixels = new uint[width * height];
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            raw.AsSpan(rowStart + 1, stride).CopyTo(current);
            Unfilter(raw[rowStart], current, previous, filterStep);

            for (int x = 0; x < width; x++)
                pixels[y * width + x] = Pixel(current, x, colorType, bitDepth, channels, palette, transparency);

            (previous, current) = (current, previous);
        }

        return (pixels, width, height);
    }

    private static bool ValidDepth(int colorType, int bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => bitDepth is 8 or 16
    };

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int step)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1: // Sub
                for (int i = step; i < row.Length; i++) row[i] += row[i - step];
                return;
            case 2: // Up
                for (int i = 0; i < row.Length; i++) row[i] += previous[i];
                return;
            case 3: // Average
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= step ? row[i - step] : 0;
                    row[i] += (byte)((left + previous[i]) / 2);
                }
                return;
            case 4: // Paeth
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= step ? row[i - step] : 0;
                    int upLeft = i >= step ? previous[i - step] : 0;
                    row[i] += (byte)Paeth(left, previous[i], upLeft);
                }
                return;
            default:
                throw new InvalidDataException($"Unknown PNG filter {filter}.");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>The <paramref name="index"/>-th sample of a row at its full bit depth.</summary>
    private static int Sample(ReadOnlySpan<byte> row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8:
                return row[index];
            case 16:
                return (row[index * 2] << 8) | row[index * 2 + 1];
            default:
                int bit = index * bitDepth;
                int shift = 8 - bitDepth - (bit % 8);
                return (row[bit / 8] >> shift) & ((1 << bitDepth) - 1);
        }
    }

    /// <summary>A sample scaled to 0..255.</summary>
    private static byte To8(int sample, int bitDepth) => bitDepth switch
    {
        8 => (byte)sample,
        16 => (byte)(sample >> 8),
        _ => (byte)(sample * 255 / ((1 << bitDepth) - 1))
    };

    private static uint Pixel(ReadOnlySpan<byte> row, int x, int colorType, int bitDepth, int channels,
        byte[]? palette, byte[]? transparency)
    {
        int first = x * channels;
        byte r, g, b, a = 255;

        switch (colorType)
        {
            case 0:
            {
                int grey = Sample(row, first, bitDepth);
                r = g = b = To8(grey, bitDepth);
                if (transparency is { Length: >= 2 } && grey == BinaryPrimitives.ReadUInt16BigEndian(transparency))
                    a = 0;
                break;
            }
            case 2:
            {
                int sr = Sample(row, first, bitDepth), sg = Sample(row, first + 1, bitDepth), sb = Sample(row, first + 2, bitDepth);
                r = To8(sr, bitDepth);
                g = To8(sg, bitDepth);
                b = To8(sb, bitDepth);
                if (transparency is { Length: >= 6 } &&
                    sr == BinaryPrimitives.ReadUInt16BigEndian(transparency) &&
                    sg == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) &&
                    sb == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)))
                {
                    a = 0;
                }
                break;
            }
            case 3:
            {
                int index = Sample(row, first, bitDepth);
                if (index * 3 + 2 >= palette!.Length) return 0;
                r = palette[index * 3];
                g = palette[index * 3 + 1];
                b = palette[index * 3 + 2];
                if (transparency != null && index < transparency.Length) a = transparency[index];
                break;
            }
            case 4:
                r = g = b = To8(Sample(row, first, bitDepth), bitDepth);
                a = To8(Sample(row, first + 1, bitDepth), bitDepth);
                break;
            default: // 6
                r = To8(Sample(row, first, bitDepth), bitDepth);
                g = To8(Sample(row, first + 1, bitDepth), bitDepth);
                b = To8(Sample(row, first + 2, bitDepth), bitDepth);
                a = To8(Sample(row, first + 3, bitDepth), bitDepth);
                break;
        }

        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>
    /// Makes an icon square — <see cref="TileSurface.DrawIcon"/> takes a square source — by centring
    /// it on a transparent canvas, and shrinks one wider than <paramref name="maxEdge"/> by
    /// averaging blocks of pixels, so a 512 px icon is not kept around at full size.
    /// </summary>
    public static (uint[] Pixels, int Size) ToSquareIcon(uint[] pixels, int width, int height, int maxEdge)
    {
        int edge = Math.Max(width, height);
        uint[] square = pixels;
        if (width != height)
        {
            square = new uint[edge * edge];
            int left = (edge - width) / 2, top = (edge - height) / 2;
            for (int y = 0; y < height; y++)
                Array.Copy(pixels, y * width, square, (top + y) * edge + left, width);
        }

        int factor = (edge + maxEdge - 1) / maxEdge;
        if (factor <= 1) return (square, edge);

        int target = edge / factor;
        uint[] reduced = new uint[target * target];
        for (int ty = 0; ty < target; ty++)
        for (int tx = 0; tx < target; tx++)
        {
            // Alpha-weighted, so a transparent pixel's colour does not bleed into the edge.
            long a = 0, r = 0, g = 0, b = 0;
            for (int dy = 0; dy < factor; dy++)
            for (int dx = 0; dx < factor; dx++)
            {
                uint p = square[(ty * factor + dy) * edge + tx * factor + dx];
                long pa = p >> 24;
                a += pa;
                r += ((p >> 16) & 0xFF) * pa;
                g += ((p >> 8) & 0xFF) * pa;
                b += (p & 0xFF) * pa;
            }

            int count = factor * factor;
            reduced[ty * target + tx] = a == 0
                ? 0
                : ((uint)(a / count) << 24) | ((uint)(r / a) << 16) | ((uint)(g / a) << 8) | (uint)(b / a);
        }

        return (reduced, target);
    }
}
