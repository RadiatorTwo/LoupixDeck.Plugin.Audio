namespace LoupixDeck.Plugin.Audio.Rendering;

/// <summary>
/// A 90 x 90 ARGB framebuffer a mixer tile is drawn into before it becomes a PNG. Coordinates are
/// canvas pixels, origin top-left, anything outside is clipped. One surface is reused for every
/// tile of a folder, so a redraw allocates nothing but the PNG.
/// </summary>
internal sealed class TileSurface
{
    public const int Size = 90;

    private readonly uint[] _pixels = new uint[Size * Size];

    public ReadOnlySpan<uint> Pixels => _pixels;

    /// <summary>Solid 0xAARRGGBB colour from an opaque 0xRRGGBB value.</summary>
    public static uint Rgb(uint rgb) => 0xFF000000u | rgb;

    public void Clear(uint color) => Array.Fill(_pixels, color);

    /// <summary>Fills a rectangle; a colour with alpha below 255 is blended over what is there.</summary>
    public void Fill(int x, int y, int width, int height, uint color)
    {
        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(Size, x + width);
        int bottom = Math.Min(Size, y + height);

        for (int row = top; row < bottom; row++)
            for (int col = left; col < right; col++)
                Blend(col, row, color);
    }

    /// <summary>Blends one pixel with straight alpha over the current content.</summary>
    public void Blend(int x, int y, uint color)
    {
        if ((uint)x >= Size || (uint)y >= Size) return;

        uint alpha = color >> 24;
        int index = (y * Size) + x;
        if (alpha == 255)
        {
            _pixels[index] = color;
            return;
        }
        if (alpha == 0) return;

        uint under = _pixels[index];
        uint inverse = 255 - alpha;
        uint r = ((((color >> 16) & 0xFF) * alpha) + (((under >> 16) & 0xFF) * inverse)) / 255;
        uint g = ((((color >> 8) & 0xFF) * alpha) + (((under >> 8) & 0xFF) * inverse)) / 255;
        uint b = (((color & 0xFF) * alpha) + ((under & 0xFF) * inverse)) / 255;
        _pixels[index] = 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// Draws bitmap-font text with its top-left at (x, y) and returns the x past the last glyph. The
    /// scale is a whole multiple, so no glyph pixel is ever split.
    /// </summary>
    public int DrawText(string text, int x, int y, int scale, uint color)
    {
        foreach (char c in text)
        {
            byte[] rows = BitmapFont5x7.Rows(c);
            for (int r = 0; r < BitmapFont5x7.GlyphHeight; r++)
                for (int col = 0; col < BitmapFont5x7.GlyphWidth; col++)
                    if (((rows[r] >> (BitmapFont5x7.GlyphWidth - 1 - col)) & 1) != 0)
                        Fill(x + (col * scale), y + (r * scale), scale, scale, color);

            x += BitmapFont5x7.Advance * scale;
        }
        return x;
    }

    /// <summary>
    /// A ring segment centred on (cx, cy): from <paramref name="startDegrees"/> (0 = 3 o'clock, clockwise)
    /// over <paramref name="sweepDegrees"/>. Sampled 4 x 4 per pixel so the edge is smooth while the
    /// rest of the tile stays crisp.
    /// </summary>
    public void DrawArc(float cx, float cy, float radius, float thickness, float startDegrees,
        float sweepDegrees, uint color)
    {
        if (sweepDegrees <= 0) return;

        float inner = radius - (thickness / 2);
        float outer = radius + (thickness / 2);
        int x0 = (int)MathF.Floor(cx - outer);
        int x1 = (int)MathF.Ceiling(cx + outer);
        int y0 = (int)MathF.Floor(cy - outer);
        int y1 = (int)MathF.Ceiling(cy + outer);
        uint baseAlpha = color >> 24;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                {
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float dx = x + ((sx + 0.5f) / 4) - cx;
                        float dy = y + ((sy + 0.5f) / 4) - cy;
                        float d = MathF.Sqrt((dx * dx) + (dy * dy));
                        if (d < inner || d > outer) continue;

                        float angle = MathF.Atan2(dy, dx) * (180f / MathF.PI);
                        float rel = angle - startDegrees;
                        while (rel < 0) rel += 360f;
                        while (rel >= 360f) rel -= 360f;
                        if (rel <= sweepDegrees) hits++;
                    }
                }

                if (hits == 0) continue;
                uint alpha = (uint)((baseAlpha * hits) / 16);
                Blend(x, y, (color & 0x00FFFFFFu) | (alpha << 24));
            }
        }
    }

    /// <summary>
    /// Draws an icon scaled to <paramref name="size"/> with its top-left at (x, y). The source is
    /// straight-alpha 0xAARRGGBB; <paramref name="opacity"/> scales its alpha and
    /// <paramref name="grey"/> replaces the colour by its luma (the muted look).
    /// </summary>
    public void DrawIcon(ReadOnlySpan<uint> source, int sourceSize, int x, int y, int size, float opacity,
        bool grey)
    {
        for (int dy = 0; dy < size; dy++)
        {
            int sy = Math.Min(sourceSize - 1, (dy * sourceSize) / size);
            for (int dx = 0; dx < size; dx++)
            {
                int sx = Math.Min(sourceSize - 1, (dx * sourceSize) / size);
                uint p = source[(sy * sourceSize) + sx];
                uint alpha = (uint)MathF.Round((p >> 24) * opacity);
                if (alpha == 0) continue;

                uint rgb = p & 0x00FFFFFFu;
                if (grey)
                {
                    uint luma = (uint)MathF.Round(
                        (0.299f * ((p >> 16) & 0xFF)) + (0.587f * ((p >> 8) & 0xFF)) + (0.114f * (p & 0xFF)));
                    rgb = (luma << 16) | (luma << 8) | luma;
                }

                Blend(x + dx, y + dy, rgb | (alpha << 24));
            }
        }
    }

    /// <summary>64-bit FNV-1a over all pixels; equal hashes mean the same picture, so an unchanged tile can skip its PNG.</summary>
    public ulong ContentHash()
    {
        ulong hash = 14695981039346656037UL;
        foreach (uint p in _pixels)
        {
            hash = (hash ^ p) * 1099511628211UL;
        }
        return hash;
    }
}
