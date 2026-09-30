using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The look every Audio command brings to a touch button: its icon and a short caption, both set by
/// the plugin instead of the host's "display name below a standard icon". The geometry matches the
/// host's own icon-and-caption look, so an Audio button sits next to any other button without
/// looking out of place; only the glyph and the wording are the plugin's.
/// </summary>
internal static class AudioButtonLayouts
{
    // Material Design Icons code points, one per role so a command and its layout cannot drift apart.
    public const string Speaker = "\U000F04C3";       // mdi-speaker
    public const string Microphone = "\U000F036C";    // mdi-microphone
    public const string Mixer = "\U000F066A";         // mdi-tune-vertical
    public const string VolumeUp = "\U000F075D";      // mdi-volume-plus
    public const string VolumeDown = "\U000F075E";    // mdi-volume-minus
    public const string VolumeSet = "\U000F0580";     // mdi-volume-medium
    public const string Mute = "\U000F075F";          // mdi-volume-mute
    public const string DefaultDevice = "\U000F05E0"; // mdi-check-circle
    public const string Sound = "\U000F075A";         // mdi-music
    public const string Stop = "\U000F04DB";          // mdi-stop

    // Pixel values for a 90 px key; the host scales them onto the key actually being written.
    private const double IconScale = 0.5;
    private const int IconOffsetY = -9;
    private const int CaptionSize = 11;
    private const int CaptionOffsetY = 27;
    private const int CaptionBoxWidth = 88;
    private const int CaptionBoxHeight = 22;

    // The tall variant leaves room for two lines of text, for a caption that is replaced at runtime
    // by something long, such as a device name.
    private const double TallIconScale = 0.36;
    private const int TallIconOffsetY = -19;
    private const int TallCaptionOffsetY = 17;
    private const int TallCaptionBoxHeight = 40;

    /// <summary>
    /// Translates a caption into the host's language. Set once the plugin is initialised; the layout
    /// is stored on the button at assignment, so it is the language at that moment that counts.
    /// </summary>
    internal static Func<string, string> Translate { get; set; } = static english => english;

    /// <summary>The icon centred above a caption.</summary>
    /// <param name="glyph">The icon.</param>
    /// <param name="caption">English caption; null keeps the name the host would show, which is the
    /// right thing when the entry itself carries the meaning, such as a sound's file name.</param>
    /// <param name="tall">Gives the caption two lines and shrinks the icon to make room.</param>
    public static ButtonLayoutDescriptor IconWithCaption(string glyph, string? caption, bool tall = false) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                IconScale = tall ? TallIconScale : IconScale,
                OffsetY = tall ? TallIconOffsetY : IconOffsetY
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = caption == null ? null : Translate(caption),
                TextSize = CaptionSize,
                OffsetY = tall ? TallCaptionOffsetY : CaptionOffsetY,
                BoxWidth = CaptionBoxWidth,
                BoxHeight = tall ? TallCaptionBoxHeight : CaptionBoxHeight
            }
        ]
    };
}
