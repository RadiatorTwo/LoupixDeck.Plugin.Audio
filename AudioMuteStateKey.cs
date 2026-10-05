using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The live look of a mute toggle on a touch key: a speaker (or microphone) with the level below it
/// while live, a crossed-out symbol in red with "Muted" below it while muted. Opt-in per binding
/// through the <c>showState</c> parameter. A binding saved before the parameter existed carries no
/// value, reads as off and keeps the static icon it always had, because the host only adds the
/// plugin's layer once <see cref="IDisplayImageCommand.RenderImage"/> draws something.
/// </summary>
internal static class AudioMuteStateKey
{
    public const string ShowStateName = "showState";

    /// <summary>On for every new assignment: the host pre-fills it when the command is inserted.</summary>
    public static CommandParameter ShowStateParameter { get; } =
        new(ShowStateName, typeof(bool)) { DefaultValue = "True" };

    /// <summary>Whether the binding asked for the live look. Parameter index 1, off when absent.</summary>
    public static bool Enabled(CommandContext ctx) =>
        ctx.Parameters is { Length: > 1 } p && bool.TryParse(p[1], out bool on) && on;

    // MDI names from the host's symbol library.
    private const string SpeakerSymbol = "volume-high";
    private const string SpeakerMutedSymbol = "volume-off";
    private const string MicrophoneSymbol = "microphone";
    private const string MicrophoneMutedSymbol = "microphone-off";

    private static readonly PluginColor Back = PluginColor.Black;
    private static readonly PluginColor LiveColor = PluginColor.White;
    private static readonly PluginColor MutedColor = PluginColor.FromRgb(0xE5, 0x48, 0x4D);

    /// <summary>
    /// Draws the whole key. The background is opaque because the command's own icon-and-caption
    /// layers sit underneath and must not show through. Geometry follows
    /// <see cref="AudioButtonLayouts.IconWithCaption"/>, scaled from its 90 px design.
    /// </summary>
    public static bool Draw(IRenderCanvas canvas, bool muted, bool microphone, string caption)
    {
        int size = Math.Min(canvas.Width, canvas.Height);
        double k = size / 90.0;
        int Px(double design) => (int)Math.Round(design * k);

        canvas.Clear(Back);

        string symbol = microphone
            ? (muted ? MicrophoneMutedSymbol : MicrophoneSymbol)
            : (muted ? SpeakerMutedSymbol : SpeakerSymbol);
        PluginColor color = muted ? MutedColor : LiveColor;

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
