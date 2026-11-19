using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// How a mute toggle draws its touch key, chosen by <c>showState</c> (<see cref="MuteKeyStyle"/>).
/// <see cref="MuteKeyStyle.Live"/>: a speaker (or microphone) with the level below it while live, a
/// crossed-out symbol in red with "Muted" below it while muted. <see cref="MuteKeyStyle.Static"/>: the
/// static mute icon and caption the command always had. <see cref="MuteKeyStyle.Layers"/>: nothing,
/// so the user's own layers style the key and show the state through the command's value.
/// <para>
/// The toggles bring no layers of their own any more (<see cref="Layout"/>), so the key is drawn here on
/// a transparent background and the wallpaper shows through. A binding saved before the parameter
/// existed carries no value and is left alone (<see cref="IsLegacy"/>): its button still has the icon
/// and caption layers it was created with, and the host only adds the plugin's layer once
/// <see cref="IDisplayImageCommand.RenderImage"/> draws something.
/// </para>
/// </summary>
internal static class AudioMuteStateKey
{
    public const string ShowStateName = "showState";

    /// <summary>
    /// <see cref="MuteKeyStyle.Live"/> for every new assignment: the host pre-fills it when the command
    /// is inserted. The parameter was a checkbox before <see cref="MuteKeyStyle.Layers"/> existed, so a
    /// saved <c>True</c> or <c>False</c> is still read (<see cref="StyleOf"/>).
    /// </summary>
    public static CommandParameter ShowStateParameter { get; } =
        new(ShowStateName, typeof(MuteKeyStyle)) { DefaultValue = nameof(MuteKeyStyle.Live) };

    /// <summary>The key is drawn entirely by <see cref="Draw"/>, so the host adds no layers.</summary>
    public static ButtonLayoutDescriptor Layout { get; } = new() { Mode = ButtonLayoutMode.None };

    /// <summary>A binding from before <c>showState</c>: its button has its own layers and is not drawn over.</summary>
    public static bool IsLegacy(CommandContext ctx) => ctx.Parameters is not { Length: > 1 };

    /// <summary>
    /// The look the binding asked for (parameter index 1). <c>True</c> and <c>False</c> are the values
    /// of the former checkbox and mean <see cref="MuteKeyStyle.Live"/> and <see cref="MuteKeyStyle.Static"/>;
    /// anything unreadable keeps the static look, as an unreadable checkbox did.
    /// </summary>
    public static MuteKeyStyle StyleOf(CommandContext ctx)
    {
        if (ctx.Parameters is not { Length: > 1 } p) return MuteKeyStyle.Static;

        string value = p[1].Trim();
        if (bool.TryParse(value, out bool on)) return on ? MuteKeyStyle.Live : MuteKeyStyle.Static;
        return Enum.TryParse(value, ignoreCase: true, out MuteKeyStyle style) && Enum.IsDefined(style)
            ? style
            : MuteKeyStyle.Static;
    }

    /// <summary>The look the toggle had before the live state existed: mute icon and caption in white.</summary>
    public static bool DrawStatic(IRenderCanvas canvas, string caption) =>
        DrawKey(canvas, StaticSymbol, LiveColor, caption);

    // MDI names from the host's symbol library.
    private const string SpeakerSymbol = "volume-high";
    private const string SpeakerMutedSymbol = "volume-off";
    private const string MicrophoneSymbol = "microphone";
    private const string MicrophoneMutedSymbol = "microphone-off";
    private const string StaticSymbol = "volume-mute"; // the glyph of AudioButtonLayouts.Mute

    private static readonly PluginColor LiveColor = PluginColor.White;
    private static readonly PluginColor MutedColor = PluginColor.FromRgb(0xE5, 0x48, 0x4D);

    /// <summary>Draws the live state of the key.</summary>
    public static bool Draw(IRenderCanvas canvas, bool muted, bool microphone, string caption)
    {
        string symbol = microphone
            ? (muted ? MicrophoneMutedSymbol : MicrophoneSymbol)
            : (muted ? SpeakerMutedSymbol : SpeakerSymbol);
        return DrawKey(canvas, symbol, muted ? MutedColor : LiveColor, caption);
    }

    /// <summary>
    /// Icon above a caption on a transparent key. Geometry follows
    /// <see cref="AudioButtonLayouts.IconWithCaption"/>, scaled from its 90 px design.
    /// </summary>
    private static bool DrawKey(IRenderCanvas canvas, string symbol, PluginColor color, string caption)
    {
        int size = Math.Min(canvas.Width, canvas.Height);
        double k = size / 90.0;
        int Px(double design) => (int)Math.Round(design * k);

        int icon = Px(45);
        canvas.DrawSymbol(symbol, (size - icon) / 2, ((size - icon) / 2) + Px(-9), icon, icon, color);

        int boxWidth = Px(88);
        int boxHeight = Px(22);
        canvas.DrawText(caption, (size - boxWidth) / 2, ((size - boxHeight) / 2) + Px(27), boxWidth, boxHeight,
            color, (float)(11 * k));
        return true;
    }

    /// <summary>
    /// Whether an endpoint is an input. Read from the id instead of an enumeration, which would be far
    /// too slow for a key redrawn several times a second: Windows capture ids start with "{0.0.1.",
    /// the Linux backend prefixes sources with "source:".
    /// </summary>
    public static bool IsCapture(string? boundId, string resolvedId) =>
        string.Equals(boundId, AudioDeviceParameter.DefaultInputDeviceId, StringComparison.Ordinal) ||
        resolvedId.StartsWith("{0.0.1.", StringComparison.Ordinal) ||
        resolvedId.StartsWith("source:", StringComparison.Ordinal);
}

/// <summary>How a mute toggle draws its touch key.</summary>
public enum MuteKeyStyle
{
    /// <summary>The live state: the symbol and the level, or a red crossed-out symbol and "Muted".</summary>
    Live,

    /// <summary>The fixed mute icon and caption, whatever the state.</summary>
    Static,

    /// <summary>
    /// Nothing drawn by the plugin: the key shows only its own layers, which can show the state
    /// through a text layer set to the value and an indicator layer.
    /// </summary>
    Layers
}
