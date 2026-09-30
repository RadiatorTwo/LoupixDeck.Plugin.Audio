using System.Buffers.Binary;
using System.IO.Compression;

namespace LoupixDeck.Plugin.Audio.Rendering;

/// <summary>
/// Minimal PNG writer for <see cref="LoupixDeck.PluginSdk.FolderEntry.Image"/>, which only takes PNG
/// bytes. A mixer tile is 90 x 90 and rebuilt whenever it changes, so the data is deflated with
/// no compression: encoding stays in the microsecond range and the plugin needs no imaging
/// library. The host decodes it once per change.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes 0xAARRGGBB pixels (straight alpha, row-major) as an 8-bit RGBA PNG.</summary>
    public static byte[] Encode(ReadOnlySpan<uint> argb, int width, int height)
    {
        if (argb.Length != width * height)
            throw new ArgumentException("Pixel count does not match the size.", nameof(argb));

        // One filter byte (0 = none) in front of every row.
        byte[] raw = new byte[height * (1 + width * 4)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < width; x++)
            {
                uint p = argb[y * width + x];
                raw[o++] = (byte)(p >> 16);
                raw[o++] = (byte)(p >> 8);
                raw[o++] = (byte)p;
                raw[o++] = (byte)(p >> 24);
            }
        }

        using MemoryStream deflated = new(raw.Length + 64);
        using (ZLibStream z = new(deflated, CompressionLevel.NoCompression, leaveOpen: true))
            z.Write(raw);

        using MemoryStream png = new(deflated.Capacity + 64);
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, deflated.GetBuffer().AsSpan(0, (int)deflated.Length));
        WriteChunk(png, "IEND"u8, []);

        return png.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        s.Write(word);
        s.Write(type);
        s.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFFu);
        s.Write(word);
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
