using System.Text.RegularExpressions;

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

/// <summary>The finished tile: the picture, plus the text the host still has to draw over it (smooth font only).</summary>
internal readonly record struct MixerTileImage(byte[] Png, string HostText);

/// <summary>
/// Draws the redesigned per-application mixer tile, following the Claude Design spec: a 90 x 90 tile with a
/// 72 x 72 safe area, four layouts, and text either in a 5x7 bitmap font (drawn here, exact on the panel's
/// pixel grid) or in the host's anti-aliased font (drawn by the host over the picture).
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
    private const uint MutedFill = 0xFF4A5158;
    private const uint GlyphColor = 0xFF8A939C;
    private const uint BadgeColor = 0xFFD9DEE3;

    /// <summary>Characters of a name that fit the 72 px line at 6 px per glyph.</summary>
    internal const int PixelNameChars = 12;

    /// <summary>Fixed part of the marquee: 1 s at 50 ms per frame before the name starts to move.</summary>
    internal const int MarqueeHoldFrames = 20;

    // Speaker with a cross, 11 x 7, drawn inside the 15 x 11 mute badge at (+2, +2).
    private static readonly string[] MuteBitmap =
    [
        "00010000000", "00110010001", "11110001010", "11110000100", "11110001010", "00110010001", "00010000000"
    ];

    private static readonly Regex TrailingParentheses = TrailingParenthesesRegex();
    private static readonly Regex Whitespace = WhitespaceRegex();

    private readonly TileSurface _surface = new();
    private readonly object _gate = new();

    [GeneratedRegex(@"\s*\(.*\)\s*$")]
    private static partial Regex TrailingParenthesesRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Strips a trailing "( ... )" and collapses whitespace, so "Microsoft Teams (work or school)" reads "Microsoft Teams".</summary>
    public static string NormalizeName(string name) =>
        Whitespace.Replace(TrailingParentheses.Replace(name, string.Empty), " ").Trim();

    /// <summary>
    /// Whether the name of this tile scrolls: only in the pixel font, only for a selected tile, only when it
    /// does not fit. The caller uses this to decide whether the tile needs the fast timer.
    /// </summary>
    public static bool Scrolls(string name, bool selected, MixerTileStyle style)
    {
        if (!selected || style.Font != MixerTileFont.Pixel || style.Layout == MixerTileLayout.Left) return false;

        string prepared = BitmapFont5x7.Prepare(NormalizeName(name));
        return BitmapFont5x7.CanRender(prepared) && prepared.Length > PixelNameChars;
    }

    /// <summary>Frames after which the marquee is back at its start, for the caller's frame counter.</summary>
    public static int MarqueePeriodFrames(string name)
    {
        int n = BitmapFont5x7.Prepare(NormalizeName(name)).Length;
        return (6 * (n + 3)) + MarqueeHoldFrames;
    }

    public MixerTileImage Render(MixerTileData tile, MixerTileStyle style)
    {
        lock (_gate)
        {
            string name = NormalizeName(tile.Name);
            string prepared = BitmapFont5x7.Prepare(name);

            // A name the pixel font cannot spell (CJK, Cyrillic) falls back to the host font for this tile.
            bool pixel = style.Font == MixerTileFont.Pixel && BitmapFont5x7.CanRender(prepared);

            DrawFrame(tile, style.Layout);
            string hostText = string.Empty;

            if (pixel)
                DrawPixelText(tile, style.Layout, prepared);
            else
                hostText = HostText(tile, style.Layout, name);

            DrawBadgeAndSelection(tile, style.Layout);

            _surface.ResetClip();
            return new MixerTileImage(PngEncoder.Encode(_surface.Pixels, TileSurface.Size, TileSurface.Size), hostText);
        }
    }

    private void DrawFrame(MixerTileData tile, MixerTileLayout layout)
    {
        TileLayout l = TileLayout.For(layout);
        _surface.ResetClip();
        _surface.Clear(tile.Selected ? BackgroundSelected : Background);

        float opacity = tile.Muted ? l.MutedIconOpacity : l.IconOpacity;
        if (tile.Icon != null)
        {
            _surface.DrawIcon(tile.Icon, tile.IconSize, l.IconX, l.IconY, l.IconSize, opacity, grey: tile.Muted);
        }
        else
        {
            DrawFallbackGlyph(l, tile.Muted, opacity);
        }

        uint fill = tile.Muted ? MutedFill : Accent;
        if (l.Arc)
        {
            const float StartDegrees = 135f;
            const float Sweep = 270f;
            _surface.DrawArc(45, 42, 31, 3, StartDegrees, Sweep, Track);
            if (tile.Percent > 0)
                _surface.DrawArc(45, 42, 31, 3, StartDegrees, Sweep * tile.Percent / 100f, fill);
        }
        else
        {
            _surface.Fill(9, 77, 72, 4, Track);
            _surface.Fill(9, 77, (int)Math.Round(72 * tile.Percent / 100.0), 4, fill);
        }
    }

    /// <summary>The speaker shown when an application has no icon: a 32 x 32 bitmap, scaled by whole multiples.</summary>
    private void DrawFallbackGlyph(TileLayout l, bool muted, float opacity)
    {
        uint[] glyph = SpeakerGlyph(muted);
        int scale = Math.Max(1, l.IconSize / 32);
        int offset = (l.IconSize - (32 * scale)) / 2;
        _surface.DrawIcon(glyph, 32, l.IconX + offset, l.IconY + offset, 32 * scale, opacity, grey: false);
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

    private void DrawPixelText(MixerTileData tile, MixerTileLayout layout, string name)
    {
        TileLayout l = TileLayout.For(layout);
        uint percentColor = tile.Muted ? MutedColor : TextColor;
        uint nameColor = tile.Muted ? MutedColor : tile.Selected ? TextColor : NameColor;
        string number = tile.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (layout == MixerTileLayout.Left)
        {
            int w = BitmapFont5x7.Measure(number, 2);
            int x = 81 - w;
            _surface.DrawText(number, x, 13, 2, percentColor);
            _surface.DrawText("%", 76, 30, 1, percentColor);
            if (tile.Muted) _surface.Fill(x - 2, 19, w + 4, 2, StrikeColor);

            List<string> lines = WrapTwoLines(name);
            for (int i = 0; i < lines.Count; i++)
                _surface.DrawText(lines[i], 9, i == 0 ? 51 : 60, 1, nameColor);
            return;
        }

        string percent = number + "%";
        int pw = BitmapFont5x7.Measure(percent, l.PercentScale);
        int px = 9 + ((72 - pw) / 2);
        _surface.DrawText(percent, px, l.PercentY, l.PercentScale, percentColor);
        if (tile.Muted)
            _surface.Fill(px - l.PercentScale, l.PercentY + (3 * l.PercentScale), pw + (2 * l.PercentScale), l.PercentScale, StrikeColor);

        DrawNameLine(name, l.NameY, nameColor, tile);
    }

    private void DrawNameLine(string name, int y, uint color, MixerTileData tile)
    {
        int width = BitmapFont5x7.Measure(name);
        if (width <= 72)
        {
            _surface.DrawText(name, 9 + ((72 - width) / 2), y, 1, color);
            return;
        }

        if (!tile.Selected)
        {
            string cut = TruncatePixel(name);
            _surface.DrawText(cut, 9 + ((72 - BitmapFont5x7.Measure(cut)) / 2), y, 1, color);
            return;
        }

        // Selected and overlong: scroll. The loop is the name plus three spaces, drawn twice so the seam is
        // seamless; the first 20 frames hold the start, then it moves 1 px per frame.
        int period = width + 19;
        int frame = tile.MarqueeFrame % (period + MarqueeHoldFrames);
        int offset = Math.Max(0, frame - MarqueeHoldFrames);

        _surface.SetClip(9, y - 1, 72, 9);
        _surface.DrawText(name, 9 - offset, y, 1, color);
        _surface.DrawText(name, 9 - offset + period, y, 1, color);
        _surface.ResetClip();
    }

    /// <summary>First 11 characters and an ellipsis, so a cut name never ends mid-glyph.</summary>
    internal static string TruncatePixel(string name) =>
        name.Length > PixelNameChars ? name[..(PixelNameChars - 1)].TrimEnd() + BitmapFont5x7.Ellipsis : name;

    /// <summary>Breaks a name over two 12-character lines at a space or after a hyphen, cutting the second line if needed.</summary>
    internal static List<string> WrapTwoLines(string name)
    {
        static bool Fits(string s) => BitmapFont5x7.Measure(s) <= 72;

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

        return [lines[0], TruncatePixel(string.Join(' ', lines.Skip(1)))];
    }

    private void DrawBadgeAndSelection(MixerTileData tile, MixerTileLayout layout)
    {
        _surface.ResetClip();

        if (tile.Muted && tile.Icon != null)
        {
            TileLayout l = TileLayout.For(layout);
            _surface.Fill(l.BadgeX, l.BadgeY, 15, 11, Background);
            for (int r = 0; r < MuteBitmap.Length; r++)
                for (int c = 0; c < MuteBitmap[r].Length; c++)
                    if (MuteBitmap[r][c] == '1')
                        _surface.Fill(l.BadgeX + 2 + c, l.BadgeY + 2 + r, 1, 1, BadgeColor);
        }

        if (tile.Selected)
        {
            _surface.Fill(2, 2, 86, 2, Accent);
            _surface.Fill(2, 86, 86, 2, Accent);
            _surface.Fill(2, 4, 2, 82, Accent);
            _surface.Fill(86, 4, 2, 82, Accent);
        }
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
        string padding = string.Concat(Enumerable.Repeat(" \n", TileLayout.For(layout).HostPaddingLines));
        return $"{padding}{tile.Percent} %\n{shown}";
    }

    /// <summary>The coordinates of one layout, straight from the design spec.</summary>
    private readonly record struct TileLayout(
        int IconX, int IconY, int IconSize, float IconOpacity, float MutedIconOpacity,
        int BadgeX, int BadgeY, int PercentScale, int PercentY, int NameY, bool Arc, int HostPaddingLines)
    {
        public static TileLayout For(MixerTileLayout layout) => layout switch
        {
            MixerTileLayout.Left => new TileLayout(9, 9, 32, 1f, 0.35f, 28, 31, 2, 13, 51, false, 2),
            MixerTileLayout.Background => new TileLayout(13, 5, 64, 0.3f, 0.12f, 66, 9, 3, 28, 60, false, 2),
            MixerTileLayout.Arc => new TileLayout(25, 22, 40, 0.3f, 0.12f, 38, 55, 2, 35, 74, true, 1),
            _ => new TileLayout(29, 9, 32, 1f, 0.35f, 48, 31, 2, 45, 64, false, 2)
        };
    }
}
