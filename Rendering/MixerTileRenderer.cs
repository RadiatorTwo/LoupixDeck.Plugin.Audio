using System.Globalization;
using System.Text.RegularExpressions;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio.Rendering;

/// <summary>What one mixer tile is drawn from.</summary>
/// <param name="Name">Display name of the application, before any font preparation.</param>
/// <param name="Percent">Volume, 0 to 100.</param>
/// <param name="Icon">Application icon as square 0xAARRGGBB pixels, or null for the fallback speaker.</param>
/// <param name="IconSize">Edge of <paramref name="Icon"/> in pixels.</param>
/// <param name="MarqueeFrame">Frame counter of the scrolling name; only read for a selected tile with an overlong name.</param>
internal readonly record struct MixerTileData(
    string Name,
    int Percent,
    bool Muted,
    bool Selected,
    uint[]? Icon,
    int IconSize,
    int MarqueeFrame);

/// <summary>Receives the finished pixels of a tile: 0xAARRGGBB, row-major, <paramref name="size"/> x <paramref name="size"/>.</summary>
internal delegate void TilePixelSink(ReadOnlySpan<uint> pixels, int size);

/// <summary>
/// Draws the redesigned per-application mixer tile, following the Claude Design spec: four layouts, and text
/// either in a 5x7 bitmap font (drawn here, exact on the panel's pixel grid) or in the host's anti-aliased font
/// (drawn by the host over the picture).
///
/// <para>The tile is drawn at the size of the key it is for, and the whole layout is derived from that size: the
/// content sits in a box inside the selection frame whose margin is a tenth of the key, and every position and
/// icon size is the spec's 90 px coordinate scaled to that box. On a 90 px key the box is 72 px and the result is
/// the spec exactly. Text keeps whole-pixel scales, so what shrinks with the key is the room around it: the
/// number of characters a name line holds, and the scale of the percentage when it would not fit.</para>
/// </summary>
internal sealed partial class MixerTileRenderer
{
    private const uint Background = 0xFF0A0B0D;
    private const uint BackgroundSelected = 0xFF0D171D;
    private const uint TextColor = 0xFFFFFFFF;
    private const uint NameColor = 0xFFC4CBD2;
    private const uint MutedColor = 0xFF6E767E;
    private const uint StrikeColor = 0xFF8A939C;
    private const uint Accent = 0xFF56C2F5;
    private const uint Track = 0xFF24282D;

    // On a transparent tile the track of the level is translucent black, so the wallpaper still shows through it.
    private const uint TrackTransparent = 0x99000000;
    private const uint OutlineColor = 0xFF000000;
    private const uint MutedFill = 0xFF4A5158;
    private const uint GlyphColor = 0xFF8A939C;
    private const uint BadgeColor = 0xFFD9DEE3;

    /// <summary>Frames the name rests at its start before it moves. 0: it runs at a constant speed without pausing.</summary>
    internal const int MarqueeHoldFrames = 0;

    private const int Glyph = BitmapFont5x7.GlyphHeight;

    // Speaker with a cross, 11 x 7, drawn inside the 15 x 11 mute badge at (+2, +2).
    private static readonly string[] MuteBitmap =
    [
        "00010000000", "00110010001", "11110001010", "11110000100", "11110001010", "00110010001", "00010000000"
    ];

    private static readonly Regex TrailingParentheses = TrailingParenthesesRegex();
    private static readonly Regex Whitespace = WhitespaceRegex();

    private readonly object _gate = new();
    private bool _transparent;
    private bool _outlined;
    private MixerTileScroll _scroll;
    private TileSurface _surface = new(TileSurface.DesignSize);

    [GeneratedRegex(@"\s*\(.*\)\s*$")]
    private static partial Regex TrailingParenthesesRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Strips a trailing "( ... )" and collapses whitespace, so "Microsoft Teams (work or school)" reads "Microsoft Teams".</summary>
    public static string NormalizeName(string name) =>
        Whitespace.Replace(TrailingParentheses.Replace(name, string.Empty), " ").Trim();

    /// <summary>Width of the content box of a key of this size: the key minus a margin of a tenth on each side.</summary>
    internal static int ContentWidth(int keySize) => keySize - (2 * Margin(keySize));

    /// <summary>Characters a name line of the pixel font holds on a key of this size (12 on a 90 px key).</summary>
    internal static int NameChars(int keySize) => (ContentWidth(keySize) + 1) / BitmapFont5x7.Advance;

    private static int Margin(int keySize) => (int)Math.Round(keySize * 0.1);

    /// <summary>Whether a tile is allowed to scroll a name that does not fit, by the style's scroll setting.</summary>
    internal static bool MayScroll(bool selected, MixerTileStyle style) => MayScroll(selected, style.Scroll);

    private static bool MayScroll(bool selected, MixerTileScroll scroll) =>
        scroll == MixerTileScroll.All || (scroll == MixerTileScroll.Selected && selected);

    /// <summary>
    /// Whether the name of this tile scrolls in the pixel font: when the style lets this tile scroll and the name
    /// does not fit. The caller uses this to decide whether the tile needs the fast timer. The smooth font cannot
    /// tell up front, it needs the canvas to measure; see <see cref="Draw"/>.
    /// </summary>
    public static bool Scrolls(string name, bool selected, MixerTileStyle style, int keySize)
    {
        if (!MayScroll(selected, style) || style.Font != MixerTileFont.Pixel || style.Layout == MixerTileLayout.Left)
            return false;

        string prepared = BitmapFont5x7.Prepare(NormalizeName(name));
        return BitmapFont5x7.CanRender(prepared) && BitmapFont5x7.Measure(prepared) > ContentWidth(keySize);
    }

    /// <summary>
    /// Draws the tile at <paramref name="size"/> x <paramref name="size"/> pixels and hands the pixels to
    /// <paramref name="sink"/> while the drawing surface is still held, so nothing is copied.
    /// </summary>
    public void Render(MixerTileData tile, MixerTileStyle style, int size, TilePixelSink sink)
    {
        lock (_gate)
        {
            if (_surface.Size != size) _surface = new TileSurface(size);

            _transparent = style.Transparent;
            _outlined = style.Outlined;
            _scroll = style.Scroll;

            Box box = new(size);
            string prepared = BitmapFont5x7.Prepare(NormalizeName(tile.Name));

            DrawBackdrop(tile, box);
            DrawIndicator(tile, style.Layout, box);
            DrawIcon(tile, style.Layout, box);
            if (UsesPixelFont(tile, style)) DrawPixelText(tile, style.Layout, prepared, box);
            DrawBadge(tile, style.Layout, box);

            _surface.ResetClip();
            sink(_surface.Pixels, _surface.Size);
        }
    }

    /// <summary>
    /// Draws a whole tile onto the host canvas: the picture through <c>DrawPixels</c>, then the smooth-font text on
    /// top of it. Returns whether this tile's name is scrolling, so the caller knows it has to keep
    /// redrawing. Held under one lock so the text is placed against the picture it belongs to.
    /// </summary>
    public bool Draw(IRenderCanvas canvas, MixerTileData tile, MixerTileStyle style, int size)
    {
        lock (_gate)
        {
            Render(tile, style, size, (pixels, edge) => canvas.DrawPixels(pixels, edge, edge));
            return DrawSmoothText(canvas, tile, style, size);
        }
    }

    /// <summary>The tile as a PNG at the design size, for a host that cannot take pixels directly.</summary>
    public byte[] RenderPng(MixerTileData tile, MixerTileStyle style)
    {
        byte[] png = [];
        Render(tile, style, TileSurface.DesignSize,
            (pixels, size) => png = PngEncoder.Encode(pixels, size, size));
        return png;
    }

    /// <summary>
    /// Whether the picture carries the text. False for the smooth font, and for a name the bitmap font cannot
    /// spell (CJK, Cyrillic), which falls back to the host font for this tile.
    /// </summary>
    public static bool UsesPixelFont(MixerTileData tile, MixerTileStyle style) =>
        style.Font == MixerTileFont.Pixel && BitmapFont5x7.CanRender(BitmapFont5x7.Prepare(NormalizeName(tile.Name)));

    /// <summary>
    /// The smooth font: draws the percentage and the name on the host canvas, in the host's anti-aliased font,
    /// at the positions the pixel font uses, after the picture has been put on it. Does nothing when the picture
    /// carries the text itself. The canvas must be the one <see cref="Render"/> just drew onto. Returns true when
    /// the name is wider than the tile and the tile therefore scrolls it.
    /// </summary>
    private bool DrawSmoothText(IRenderCanvas canvas, MixerTileData tile, MixerTileStyle style, int size)
    {
        if (UsesPixelFont(tile, style)) return false;

        Box box = new(size);
        MixerTileLayout layout = style.Layout;
        string name = NormalizeName(tile.Name);
        PluginColor percentColor = ToColor(tile.Muted ? MutedColor : TextColor);
        PluginColor nameColor = ToColor(tile.Muted ? MutedColor : tile.Selected ? TextColor : NameColor);
        string number = tile.Percent.ToString(CultureInfo.InvariantCulture);
        float nameSize = Math.Max(8f, (float)(13 * box.Scale));

        void Line(string text, int x, int centerY, int width, float fontSize, TextHAlign align, PluginColor color,
            bool bold) =>
            SmoothLine(canvas, text, x, centerY, width, fontSize, align, color, bold, style.Outlined);

        if (layout == MixerTileLayout.Left)
        {
            float numberSize = SmoothSize(canvas, "100", 16 * box.Scale, box.Width - box.IconEdge(32) - 2);
            int top = box.At(13);
            int w = (int)Math.Ceiling(canvas.MeasureText(number, numberSize, bold: true));
            Line(number, box.Right - w, top + 7, w, numberSize, TextHAlign.Right, percentColor, bold: true);
            Line("%", box.Right - 16, top + 14 + 2 + 4, 16, nameSize, TextHAlign.Right, percentColor, bold: true);
            if (tile.Muted)
                canvas.FillRectangle(box.Right - w - 1, top + 7, w + 2, 2, ToColor(StrikeColor));

            List<string> lines = WrapTwoLines(name, box.Width,
                t => canvas.MeasureText(t, nameSize), t => TruncateToFit(canvas, t, nameSize, box.Width));
            for (int i = 0; i < lines.Count; i++)
                // 14 px apart in the design, the host font being taller than the bitmap font's 9 px pitch.
                Line(lines[i], box.Left, box.At(i == 0 ? 54 : 68), box.Width, nameSize,
                    TextHAlign.Left, nameColor, bold: tile.Selected);
            return false;
        }

        (int designScale, int designName, double designFont) = layout switch
        {
            MixerTileLayout.Background => (3, 60, 22.0),
            MixerTileLayout.Arc => (2, 74, 16.0),
            _ => (2, 64, 16.0)
        };

        // The band the pixel font would use for the percentage; the host font is centred in the same band.
        string percent = number + "%";
        float percentSize = SmoothSize(canvas, "100%", designFont * box.Scale, box.Width);
        int band = 7 * PercentScale(layout, box);
        int centerY = PercentTop(tile, layout, box) + (band / 2);
        int percentWidth = (int)Math.Ceiling(canvas.MeasureText(percent, percentSize, bold: true));
        Line(percent, box.Left, centerY, box.Width, percentSize, TextHAlign.Center, percentColor, bold: true);
        if (tile.Muted)
            canvas.FillRectangle(box.Centre(percentWidth) - 1, centerY, percentWidth + 2, 2, ToColor(StrikeColor));

        int nameCenter = box.TextTop(designName, 1, 1) + 4;
        float nameWidth = canvas.MeasureText(name, nameSize, bold: tile.Selected);

        // A tile the style lets scroll runs a name that does not fit; any other cuts it with an ellipsis.
        if (MayScroll(tile.Selected, style) && nameWidth > box.Width)
        {
            DrawSmoothMarquee(canvas, name, (int)Math.Ceiling(nameWidth), nameCenter, nameSize, nameColor, tile, style, box);
            return true;
        }

        Line(TruncateToFit(canvas, name, nameSize, box.Width, tile.Selected), box.Left,
            nameCenter, box.Width, nameSize, TextHAlign.Center, nameColor, bold: tile.Selected);
        return false;
    }

    /// <summary>
    /// The name running left at a constant speed and coming back in from the right: the name and a gap, drawn
    /// twice so the seam is closed. The canvas has no clip, so the text is drawn wider than its window and the
    /// margins are then put back from the tile's own pixels; a transparent tile has nothing to put back and lets
    /// the text run over its margins to the key's edge.
    /// </summary>
    private void DrawSmoothMarquee(IRenderCanvas canvas, string name, int textWidth, int centerY, float fontSize,
        PluginColor color, MixerTileData tile, MixerTileStyle style, Box box)
    {
        int gap = Math.Max(12, (int)Math.Round(fontSize * 1.2));
        int period = textWidth + gap;
        int offset = tile.MarqueeFrame % period;
        int height = (int)Math.Ceiling(fontSize * 1.5);
        int top = centerY - (height / 2);

        for (int copy = 0; copy < 2; copy++)
        {
            canvas.DrawText(name, box.Left - offset + (copy * period), top, textWidth + 8, height, color, fontSize,
                TextHAlign.Left, TextVAlign.Middle, bold: tile.Selected, italic: false, outlined: style.Outlined,
                outlineColor: ToColor(OutlineColor));
        }

        if (style.Transparent) return;

        int y0 = Math.Max(0, top);
        int rows = Math.Min(_surface.Size, top + height) - y0;
        RestoreStrip(canvas, 0, y0, box.Left, rows);
        RestoreStrip(canvas, box.Right, y0, _surface.Size - box.Right, rows);
    }

    /// <summary>Puts a strip of the tile's own picture back on the canvas, over whatever was drawn there since.</summary>
    private void RestoreStrip(IRenderCanvas canvas, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        uint[] strip = new uint[width * height];
        ReadOnlySpan<uint> pixels = _surface.Pixels;
        for (int row = 0; row < height; row++)
            pixels.Slice(((y + row) * _surface.Size) + x, width).CopyTo(strip.AsSpan(row * width, width));

        canvas.DrawPixels(strip, width, height, x, y);
    }

    /// <summary>One line of host text, vertically centred on <paramref name="centerY"/>.</summary>
    private static void SmoothLine(IRenderCanvas canvas, string text, int x, int centerY, int width, float fontSize,
        TextHAlign align, PluginColor color, bool bold, bool outlined)
    {
        int height = (int)Math.Ceiling(fontSize * 1.5);
        canvas.DrawText(text, x, centerY - (height / 2), width, height, color, fontSize, align, TextVAlign.Middle,
            bold, italic: false, outlined, outlineColor: ToColor(OutlineColor));
    }

    /// <summary>The name cut to fit <paramref name="room"/> pixels in the host font, with an ellipsis.</summary>
    private static string TruncateToFit(IRenderCanvas canvas, string name, float fontSize, int room, bool bold = false)
    {
        if (canvas.MeasureText(name, fontSize, bold) <= room) return name;

        string cut = name;
        while (cut.Length > 1 && canvas.MeasureText(cut.TrimEnd() + BitmapFont5x7.Ellipsis, fontSize, bold) > room)
            cut = cut[..^1];
        return cut.TrimEnd() + BitmapFont5x7.Ellipsis;
    }

    private static PluginColor ToColor(uint argb) =>
        new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    /// <summary>What the host still draws over the picture: empty when the picture already carries the text.</summary>
    public static string HostText(MixerTileData tile, MixerTileStyle style) =>
        UsesPixelFont(tile, style) ? string.Empty : HostText(tile, style.Layout, NormalizeName(tile.Name));

    /// <summary>
    /// The key split into the selection frame's ring and the content box inside it. Every layout position is a
    /// coordinate of the 90 px design (content box 9..81) mapped into <see cref="Left"/>..<see cref="Right"/>.
    /// </summary>
    private readonly struct Box
    {
        public Box(int size)
        {
            Size = size;
            Margin = MixerTileRenderer.Margin(size);
            Width = size - (2 * Margin);
            Scale = Width / 72.0;
            FrameInset = Math.Max(2, (int)Math.Round(size * 0.045));
            FrameThickness = Math.Max(1, (int)Math.Round(size / 45.0));
        }

        public int Size { get; }
        public int Margin { get; }
        public int Width { get; }
        public double Scale { get; }
        public int FrameInset { get; }
        public int FrameThickness { get; }

        public int Left => Margin;
        public int Right => Margin + Width;
        public int Bottom => Right;

        /// <summary>Design x or y (9 = content edge) as a position on the key.</summary>
        public int At(int design) => Margin + (int)Math.Round((design - 9) * Scale);

        /// <summary>A length of the design as a length on the key, never below <paramref name="min"/>.</summary>
        public int Len(double design, int min = 1) => Math.Max(min, (int)Math.Round(design * Scale));

        /// <summary>The design's icon edge on this key; rounded down so it never grows past its room.</summary>
        public int IconEdge(int design) => Math.Max(8, (int)Math.Floor(design * Scale));

        /// <summary>Left edge of something <paramref name="width"/> wide, centred in the content box.</summary>
        public int Centre(int width) => Left + ((Width - width) / 2);

        /// <summary>
        /// Top of a text line whose bottom sits where the design's line does. Text does not shrink, so its
        /// bottom is what stays in place, and the gap above it takes up the difference.
        /// </summary>
        public int TextTop(int designTop, int designScale, int scale) =>
            At(designTop + (Glyph * designScale)) - (Glyph * scale);
    }

    private void DrawBackdrop(MixerTileData tile, Box box)
    {
        _surface.ResetClip();
        // A transparent tile keeps no backdrop at all; the selection frame alone marks the selected one.
        _surface.Clear(_transparent ? 0u : tile.Selected ? BackgroundSelected : Background);

        // The selection frame goes down first, so the tile's content lies over it. It sits well inside the
        // edge: the key cap and the viewing angle hide the outermost pixels, and a frame at the very edge
        // reads as cut off.
        if (tile.Selected)
            _surface.DrawFrame(box.FrameInset, box.FrameThickness, Accent);
    }

    /// <summary>The level: a bar along the bottom of the content box, or an arc around the icon.</summary>
    private void DrawIndicator(MixerTileData tile, MixerTileLayout layout, Box box)
    {
        uint fill = tile.Muted ? MutedFill : Accent;

        if (layout == MixerTileLayout.Arc)
        {
            const float StartDegrees = 135f;
            const float Sweep = 270f;
            float cx = box.Left + (box.Width / 2f);
            float cy = box.At(42);
            float radius = (float)(31 * box.Scale);
            float thickness = Math.Max(2, (float)(3 * box.Scale));

            _surface.DrawArc(cx, cy, radius, thickness, StartDegrees, Sweep, _transparent ? TrackTransparent : Track);
            if (tile.Percent > 0)
                _surface.DrawArc(cx, cy, radius, thickness, StartDegrees, Sweep * tile.Percent / 100f, fill);
            return;
        }

        int height = box.Len(4, 2);
        int y = box.Bottom - height;
        _surface.Fill(box.Left, y, box.Width, height, _transparent ? TrackTransparent : Track);
        _surface.Fill(box.Left, y, (int)Math.Round(box.Width * tile.Percent / 100.0), height, fill);
    }

    private void DrawIcon(MixerTileData tile, MixerTileLayout layout, Box box)
    {
        (int edge, int x, int y, float opacity, float mutedOpacity) = layout switch
        {
            MixerTileLayout.Left => IconSlot(box, 32, box.Left, box.At(9), 1f, 0.35f),
            MixerTileLayout.Background => IconSlot(box, 64, null, box.At(5), 0.3f, 0.12f),
            MixerTileLayout.Arc => IconSlot(box, 40, null, box.At(22), 0.3f, 0.12f),
            _ => IconSlot(box, 32, null, box.At(9), 1f, 0.35f)
        };

        // Above the level the icon may only take what the number leaves it: the number keeps its size while the
        // key shrinks, so on a small key the icon is what gives way.
        if (layout == MixerTileLayout.Top)
        {
            int percentTop = PercentTop(tile, layout, box);
            int room = percentTop - 2 - y;
            if (room < edge)
            {
                edge = Math.Max(8, room);
                x = box.Centre(edge);
            }
        }

        float alpha = tile.Muted ? mutedOpacity : opacity;
        if (tile.Icon != null)
            _surface.DrawIcon(tile.Icon, tile.IconSize, x, y, edge, alpha, grey: tile.Muted);
        else
            _surface.DrawIcon(SpeakerGlyph(tile.Muted), 32, x, y, edge, alpha, grey: false);
    }

    private static (int Edge, int X, int Y, float Opacity, float MutedOpacity) IconSlot(
        Box box, int designEdge, int? x, int y, float opacity, float mutedOpacity)
    {
        int edge = box.IconEdge(designEdge);
        return (edge, x ?? box.Centre(edge), y, opacity, mutedOpacity);
    }

    private static uint[] SpeakerGlyph(bool muted)
    {
        uint[] pixels = new uint[32 * 32];

        void Px(int x, int y, int w = 1, int h = 1)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++)
                    if ((uint)xx < 32 && (uint)yy < 32)
                        pixels[(yy * 32) + xx] = GlyphColor;
        }

        Px(4, 12, 6, 8);
        for (int i = 0; i < 8; i++) Px(10 + i, 12 - i, 1, 8 + (2 * i));

        if (muted)
        {
            for (int i = 0; i < 8; i++)
            {
                foreach (int a in new[] { 21 + i, 22 + i, 28 - i, 29 - i })
                    Px(a, 12 + i);
            }
        }
        else
        {
            (int R, int Degrees)[] arcs = [(5, 45), (6, 45), (10, 50), (11, 50)];
            foreach ((int r, int deg) in arcs)
            {
                for (int d = -deg; d <= deg; d++)
                {
                    double t = d * Math.PI / 180;
                    Px((int)Math.Round(17 + (r * Math.Cos(t))), (int)Math.Round(16 + (r * Math.Sin(t))));
                }
            }
        }

        return pixels;
    }

    private void DrawPixelText(MixerTileData tile, MixerTileLayout layout, string name, Box box)
    {
        uint percentColor = tile.Muted ? MutedColor : TextColor;
        uint nameColor = tile.Muted ? MutedColor : tile.Selected ? TextColor : NameColor;
        string number = tile.Percent.ToString(CultureInfo.InvariantCulture);

        if (layout == MixerTileLayout.Left)
        {
            // The number takes what the icon leaves of the row, and drops a scale before it would touch it.
            int room = box.Width - box.IconEdge(32) - 2;
            // Sized for the widest number, so every value of a tile is drawn at the same size.
            int scale = FitScale("100", 2, room);
            int w = BitmapFont5x7.Measure(number, scale);
            int x = box.Right - w;
            int top = box.At(13);
            Text(number, x, top, scale, percentColor);
            Text("%", box.Right - BitmapFont5x7.GlyphWidth, top + (Glyph * scale) + 2, 1, percentColor);
            if (tile.Muted)
                _surface.Fill(x - scale, top + (3 * scale), w + (2 * scale), scale, StrikeColor);

            List<string> lines = WrapTwoLines(name, box.Width, t => BitmapFont5x7.Measure(t), t => TruncatePixel(t, box.Width));
            // The second line keeps the design's place and the first sits a full line above it. The pitch is the
            // glyph plus a gap that never closes up, which the design's 9 px only gave on a full-size key.
            int lastTop = box.TextTop(60, 1, 1);
            int pitch = Glyph + Math.Max(3, box.Len(3));
            for (int i = 0; i < lines.Count; i++)
                Text(lines[i], box.Left, lines.Count == 1 ? lastTop : (i == 0 ? lastTop - pitch : lastTop), 1, nameColor);
            return;
        }

        (int designTop, int designScale, int designName) = layout switch
        {
            MixerTileLayout.Background => (28, 3, 60),
            MixerTileLayout.Arc => (35, 2, 74),
            _ => (45, 2, 64)
        };

        string percent = number + "%";
        int percentScale = PercentScale(layout, box);
        int pw = BitmapFont5x7.Measure(percent, percentScale);
        int px = box.Centre(pw);
        int py = PercentTop(tile, layout, box);
        Text(percent, px, py, percentScale, percentColor);
        if (tile.Muted)
            _surface.Fill(px - percentScale, py + (3 * percentScale), pw + (2 * percentScale), percentScale, StrikeColor);

        DrawNameLine(name, box.TextTop(designName, 1, 1), nameColor, tile, box);
    }

    /// <summary>Top of the percentage line of the layouts that centre it, from the design's position and the scale that fits.</summary>
    private static int PercentTop(MixerTileData tile, MixerTileLayout layout, Box box)
    {
        (int designTop, int designScale) = layout switch
        {
            MixerTileLayout.Background => (28, 3),
            MixerTileLayout.Arc => (35, 2),
            _ => (45, 2)
        };

        return box.TextTop(designTop, designScale, PercentScale(layout, box));
    }

    /// <summary>
    /// The scale of the centred percentage. It is chosen for "100%", the widest value, so 100 % is not drawn
    /// smaller than 72 % on a key that has no room for the design's scale.
    /// </summary>
    private static int PercentScale(MixerTileLayout layout, Box box) =>
        FitScale("100%", layout == MixerTileLayout.Background ? 3 : 2, box.Width);

    /// <summary>
    /// The host-font size for a number, at most <paramref name="wanted"/>, stepped down until the widest value
    /// (<paramref name="widest"/>) fits <paramref name="room"/>, so all values of a tile share one size.
    /// </summary>
    private static float SmoothSize(IRenderCanvas canvas, string widest, double wanted, int room)
    {
        float size = Math.Max(9f, (float)wanted);
        while (size > 9f && canvas.MeasureText(widest, size, bold: true) > room) size -= 0.5f;
        return size;
    }

    /// <summary>Bitmap-font text, with the dark outline when the tile asks for one.</summary>
    private void Text(string text, int x, int y, int scale, uint color) =>
        _surface.DrawText(text, x, y, scale, color, _outlined ? OutlineColor : null);

    /// <summary>The largest whole scale up to <paramref name="wanted"/> at which <paramref name="text"/> fits <paramref name="room"/>.</summary>
    private static int FitScale(string text, int wanted, int room)
    {
        int scale = wanted;
        while (scale > 1 && BitmapFont5x7.Measure(text, scale) > room) scale--;
        return scale;
    }

    private void DrawNameLine(string name, int y, uint color, MixerTileData tile, Box box)
    {
        int width = BitmapFont5x7.Measure(name);
        if (width <= box.Width)
        {
            Text(name, box.Centre(width), y, 1, color);
            return;
        }

        if (!MayScroll(tile.Selected, _scroll))
        {
            string cut = TruncatePixel(name, box.Width);
            Text(cut, box.Centre(BitmapFont5x7.Measure(cut)), y, 1, color);
            return;
        }

        // Overlong and allowed to scroll. The loop is the name plus three spaces, drawn twice so the seam is
        // seamless; the first 20 frames hold the start, then it moves 1 px per frame.
        int period = width + (3 * BitmapFont5x7.Advance) + 1;
        int frame = tile.MarqueeFrame % (period + MarqueeHoldFrames);
        int offset = Math.Max(0, frame - MarqueeHoldFrames);

        _surface.SetClip(box.Left, y - 1, box.Width, Glyph + 2);
        Text(name, box.Left - offset, y, 1, color);
        Text(name, box.Left - offset + period, y, 1, color);
        _surface.ResetClip();
    }

    /// <summary>The name cut to fit <paramref name="room"/> pixels, with an ellipsis, so a cut name never ends mid-glyph.</summary>
    internal static string TruncatePixel(string name, int room)
    {
        int chars = Math.Max(2, (room + 1) / BitmapFont5x7.Advance);
        return name.Length > chars ? name[..(chars - 1)].TrimEnd() + BitmapFont5x7.Ellipsis : name;
    }

    /// <summary>
    /// Breaks a name over two lines of <paramref name="room"/> pixels at a space or after a hyphen, cutting the
    /// second line if needed. <paramref name="measure"/> and <paramref name="truncate"/> stand for the font in use.
    /// </summary>
    internal static List<string> WrapTwoLines(string name, int room, Func<string, float> measure,
        Func<string, string> truncate)
    {
        bool Fits(string s) => measure(s) <= room;

        List<string> lines = [];
        string current = string.Empty;

        foreach (string word in name.Split(' '))
        {
            string w = word;
            string candidate = current.Length > 0 ? current + " " + w : w;
            if (Fits(candidate))
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
                current = string.Empty;
            }

            if (Fits(w))
            {
                current = w;
                continue;
            }

            int hyphen = w.IndexOf('-');
            if (hyphen > 0 && Fits(w[..(hyphen + 1)]))
            {
                lines.Add(w[..(hyphen + 1)]);
                w = w[(hyphen + 1)..];
                if (Fits(w))
                {
                    current = w;
                    continue;
                }
            }

            string part = string.Empty;
            foreach (char c in w)
            {
                if (Fits(part + c))
                {
                    part += c;
                }
                else
                {
                    lines.Add(part);
                    part = c.ToString();
                }
            }
            current = part;
        }

        if (current.Length > 0) lines.Add(current);
        if (lines.Count <= 2) return lines;

        return [lines[0], truncate(string.Join(' ', lines.Skip(1)))];
    }

    private void DrawBadge(MixerTileData tile, MixerTileLayout layout, Box box)
    {
        _surface.ResetClip();
        if (!tile.Muted || tile.Icon == null) return;

        (int designX, int designY) = layout switch
        {
            MixerTileLayout.Left => (28, 31),
            MixerTileLayout.Background => (66, 9),
            MixerTileLayout.Arc => (38, 55),
            _ => (48, 31)
        };

        // 15 x 11 like the design, kept inside the content box.
        int bx = Math.Min(box.At(designX), box.Right - 15);
        int by = Math.Min(box.At(designY), box.Bottom - 11);
        _surface.Fill(bx, by, 15, 11, Background);
        for (int r = 0; r < MuteBitmap.Length; r++)
            for (int c = 0; c < MuteBitmap[r].Length; c++)
                if (MuteBitmap[r][c] == '1')
                    _surface.Fill(bx + 2 + c, by + 2 + r, 1, 1, BadgeColor);
    }

    /// <summary>
    /// Smooth font: the host draws one centred block of text over the picture, so the percentage and the
    /// name go into <see cref="LoupixDeck.PluginSdk.FolderEntry.Text"/> and blank lines above them push
    /// the block below the icon. Long names are cut here because the host would wrap them.
    /// </summary>
    private static string HostText(MixerTileData tile, MixerTileLayout layout, string name)
    {
        const int HostNameChars = 12;
        string shown = name.Length > HostNameChars ? name[..(HostNameChars - 1)].TrimEnd() + '…' : name;
        int padding = layout == MixerTileLayout.Arc ? 1 : 2;
        return $"{string.Concat(Enumerable.Repeat(" \n", padding))}{tile.Percent} %\n{shown}";
    }
}
