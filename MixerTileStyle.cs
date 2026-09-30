namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Where the icon, level and name sit on a mixer tile. The names are what the host lists
/// in the parameter dropdown and what ends up in saved bindings, so they must not change.
/// </summary>
public enum MixerTileLayout
{
    /// <summary>Icon on top, then level, then name. The default.</summary>
    Top,

    /// <summary>Icon on the left, level in a narrow column, name over two lines.</summary>
    Left,

    /// <summary>Icon as a faded background, the level is the hero.</summary>
    Background,

    /// <summary>Faded background icon inside a 270 degree arc that shows the level.</summary>
    Arc
}

/// <summary>How the text on a mixer tile is drawn.</summary>
public enum MixerTileFont
{
    /// <summary>Anti-aliased host font. The default, and listed first because the host offers the first value of an enum when nothing is chosen.</summary>
    Smooth,

    /// <summary>5x7 bitmap font, exact on the panel's pixel grid.</summary>
    Pixel
}

/// <summary>
/// The look of the mixer tiles, chosen per command through its parameters. A binding that
/// carries no parameters (every mixer command saved before the tiles were redesigned) gets
/// the defaults, so it keeps working and simply picks up the new look.
/// </summary>
/// <param name="Transparent">No tile background: the wallpaper (or the device's black) shows through.</param>
/// <param name="Outlined">Dark outline around the text, for legibility on a wallpaper.</param>
internal readonly record struct MixerTileStyle(MixerTileLayout Layout, MixerTileFont Font,
    bool Transparent = false, bool Outlined = false)
{
    public static MixerTileStyle Default { get; } = new(MixerTileLayout.Top, MixerTileFont.Smooth);

    /// <summary>Reads layout and font from the command parameters, falling back to the default for anything missing or unknown.</summary>
    public static MixerTileStyle FromParameters(string[]? parameters)
    {
        MixerTileLayout layout = Default.Layout;
        MixerTileFont font = Default.Font;

        if (parameters is { Length: > 0 } &&
            Enum.TryParse(parameters[0], ignoreCase: true, out MixerTileLayout parsedLayout) &&
            Enum.IsDefined(parsedLayout))
        {
            layout = parsedLayout;
        }

        if (parameters is { Length: > 1 } &&
            Enum.TryParse(parameters[1], ignoreCase: true, out MixerTileFont parsedFont) &&
            Enum.IsDefined(parsedFont))
        {
            font = parsedFont;
        }

        bool transparent = parameters is { Length: > 2 } && bool.TryParse(parameters[2], out bool t) && t;
        bool outlined = parameters is { Length: > 3 } && bool.TryParse(parameters[3], out bool o) && o;

        return new MixerTileStyle(layout, font, transparent, outlined);
    }
}
