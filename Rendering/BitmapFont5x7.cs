namespace LoupixDeck.Plugin.Audio.Rendering;

/// <summary>
/// 5x7 bitmap font for the pixel text mode of the mixer tiles. Glyphs are 1-bit and drawn at whole
/// multiples, so every stroke lands on the panel's pixel grid. Uppercase ASCII only; see
/// <see cref="Prepare"/> for what happens to everything else.
/// </summary>
internal static class BitmapFont5x7
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;

    /// <summary>Advance per glyph at scale 1: the glyph plus one blank column.</summary>
    public const int Advance = 6;

    /// <summary>The ellipsis glyph, three dots on the last row.</summary>
    public const char Ellipsis = '…';

    // One string per glyph, seven rows of five columns, top to bottom, '1' = lit.
    private static readonly Dictionary<char, byte[]> Glyphs = Build(
    [
        ('A', "01110 10001 10001 11111 10001 10001 10001"), ('B', "11110 10001 10001 11110 10001 10001 11110"),
        ('C', "01110 10001 10000 10000 10000 10001 01110"), ('D', "11110 10001 10001 10001 10001 10001 11110"),
        ('E', "11111 10000 10000 11110 10000 10000 11111"), ('F', "11111 10000 10000 11110 10000 10000 10000"),
        ('G', "01110 10001 10000 10111 10001 10001 01111"), ('H', "10001 10001 10001 11111 10001 10001 10001"),
        ('I', "01110 00100 00100 00100 00100 00100 01110"), ('J', "00111 00010 00010 00010 00010 10010 01100"),
        ('K', "10001 10010 10100 11000 10100 10010 10001"), ('L', "10000 10000 10000 10000 10000 10000 11111"),
        ('M', "10001 11011 10101 10101 10001 10001 10001"), ('N', "10001 10001 11001 10101 10011 10001 10001"),
        ('O', "01110 10001 10001 10001 10001 10001 01110"), ('P', "11110 10001 10001 11110 10000 10000 10000"),
        ('Q', "01110 10001 10001 10001 10101 10010 01101"), ('R', "11110 10001 10001 11110 10100 10010 10001"),
        ('S', "01111 10000 10000 01110 00001 00001 11110"), ('T', "11111 00100 00100 00100 00100 00100 00100"),
        ('U', "10001 10001 10001 10001 10001 10001 01110"), ('V', "10001 10001 10001 10001 10001 01010 00100"),
        ('W', "10001 10001 10001 10101 10101 10101 01010"), ('X', "10001 10001 01010 00100 01010 10001 10001"),
        ('Y', "10001 10001 10001 01010 00100 00100 00100"), ('Z', "11111 00001 00010 00100 01000 10000 11111"),
        ('0', "01110 10001 10001 10001 10001 10001 01110"), ('1', "00100 01100 00100 00100 00100 00100 01110"),
        ('2', "01110 10001 00001 00010 00100 01000 11111"), ('3', "11111 00010 00100 00010 00001 10001 01110"),
        ('4', "00010 00110 01010 10010 11111 00010 00010"), ('5', "11111 10000 11110 00001 00001 10001 01110"),
        ('6', "00110 01000 10000 11110 10001 10001 01110"), ('7', "11111 00001 00010 00100 01000 01000 01000"),
        ('8', "01110 10001 10001 01110 10001 10001 01110"), ('9', "01110 10001 10001 01111 00001 00010 01100"),
        ('%', "11000 11001 00010 00100 01000 10011 00011"), ('-', "00000 00000 00000 11111 00000 00000 00000"),
        ('.', "00000 00000 00000 00000 00000 01100 01100"), (Ellipsis, "00000 00000 00000 00000 00000 00000 10101"),
        ('(', "00010 00100 01000 01000 01000 00100 00010"), (')', "01000 00100 00010 00010 00010 00100 01000"),
        ('\'', "01100 00100 01000 00000 00000 00000 00000"), ('/', "00000 00001 00010 00100 01000 10000 00000"),
        ('&', "01100 10010 10100 01000 10101 10010 01101"), ('+', "00000 00100 00100 11111 00100 00100 00000"),
        ('_', "00000 00000 00000 00000 00000 00000 11111"), (':', "00000 01100 01100 00000 01100 01100 00000"),
        ('!', "00100 00100 00100 00100 00100 00000 00100"), ('?', "01110 10001 00001 00010 00100 00000 00100"),
        (' ', "00000 00000 00000 00000 00000 00000 00000")
    ]);

    /// <summary>Width of <paramref name="text"/> in pixels; the trailing blank column is not counted.</summary>
    public static int Measure(string text, int scale = 1) =>
        text.Length == 0 ? 0 : (text.Length * Advance * scale) - scale;

    /// <summary>True when every character of <paramref name="text"/> has a glyph.</summary>
    public static bool CanRender(string text)
    {
        foreach (char c in text)
            if (!Glyphs.ContainsKey(c)) return false;
        return true;
    }

    /// <summary>The glyph rows (bit 4 = leftmost column); an unknown character draws as '?'.</summary>
    public static byte[] Rows(char c) => Glyphs.TryGetValue(c, out byte[]? rows) ? rows : Glyphs['?'];

    /// <summary>
    /// Brings a name into the font's alphabet: diacritics stripped (é becomes E), then uppercase. What
    /// is still missing afterwards (CJK, Cyrillic) is for the caller to route to the smooth font.
    /// </summary>
    public static string Prepare(string text)
    {
        string decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        System.Text.StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            builder.Append(c);
        }
        return builder.ToString().ToUpperInvariant();
    }

    private static Dictionary<char, byte[]> Build((char Char, string Rows)[] table)
    {
        Dictionary<char, byte[]> map = new(table.Length);
        foreach ((char c, string rows) in table)
        {
            string[] lines = rows.Split(' ');
            byte[] bytes = new byte[GlyphHeight];
            for (int r = 0; r < GlyphHeight; r++)
                bytes[r] = Convert.ToByte(lines[r], 2);
            map[c] = bytes;
        }
        return map;
    }
}
