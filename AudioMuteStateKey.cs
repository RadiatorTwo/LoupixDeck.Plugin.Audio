using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// How a mute toggle draws its touch key. With <c>showState</c> on: a speaker (or microphone) with the
/// level below it while live, a crossed-out symbol in red with "Muted" below it while muted. With it
/// off: the static mute icon and caption the command always had.
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

    /// <summary>On for every new assignment: the host pre-fills it when the command is inserted.</summary>
    public static CommandParameter ShowStateParameter { get; } =
        new(ShowStateName, typeof(bool)) { DefaultValue = "True" };

    /// <summary>The key is drawn entirely by <see cref="Draw"/>, so the host adds no layers.</summary>
    public static ButtonLayoutDescriptor Layout { get; } = new() { Mode = ButtonLayoutMode.None };

    /// <summary>A binding from before <c>showState</c>: its button has its own layers and is not drawn over.</summary>
    public static bool IsLegacy(CommandContext ctx) => ctx.Parameters is not { Length: > 1 };

    /// <summary>Whether the binding asked for the live look (parameter index 1).</summary>
    public static bool Enabled(CommandContext ctx) =>
        ctx.Parameters is { Length: > 1 } p && bool.TryParse(p[1], out bool on) && on;

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
