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
    /// <summary>5x7 bitmap font, exact on the panel's pixel grid.</summary>
    Pixel,

    /// <summary>Anti-aliased host font.</summary>
    Smooth
}

/// <summary>
/// The look of the mixer tiles, chosen per command through its parameters. A binding that
/// carries no parameters (every mixer command saved before the tiles were redesigned) gets
/// the defaults, so it keeps working and simply picks up the new look.
/// </summary>
internal readonly record struct MixerTileStyle(MixerTileLayout Layout, MixerTileFont Font)
{
    public static MixerTileStyle Default { get; } = new(MixerTileLayout.Top, MixerTileFont.Pixel);

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

        return new MixerTileStyle(layout, font);
    }
}
