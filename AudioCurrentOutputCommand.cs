using System.Diagnostics;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Opens the output-device picker and labels itself with the device currently in use, so the
/// button doubles as a read-out. Polled rather than event-driven because the default endpoint
/// can change from outside the app and no cross-platform notification exists for it.
/// </summary>
internal sealed class AudioCurrentOutputCommand(
    IAudioService audio, AudioAliasStore aliasStore, AudioVisibilityStore visibility) : IDisplayCommand
{
    /// <summary>Shown when no output endpoint is currently the default (or none exist).</summary>
    private const string NoDeviceLabel = "No device";

    /// <summary>
    /// Enumerating endpoints does real COM/pactl work per call (see
    /// <see cref="IAudioService.GetEndpoints"/>), and several buttons could carry this command
    /// at once, so the resolved label is cached for one poll interval rather than re-enumerated
    /// on every <see cref="GetText"/> call.
    /// </summary>
    private static readonly Lock CacheLock = new();
    private static string? _cachedLabel;
    private static long _cachedAtTimestamp;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.CurrentOutput",
        DisplayName = "Audio: Current Output",
        Group = "Audio",
        Icon = AudioButtonLayouts.Speaker,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Speaker, "Output", tall: true),
        Description = "Show the active output device, and open the picker when pressed",
        HiddenFromMenu = true,
        // The look of the picker this button opens; the button itself only shows the device name.
        ParameterTemplate = MixerTileStyle.ParameterTemplate,
        Parameters = MixerTileStyle.Parameters()
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public TimeSpan UpdateInterval => LabelLifetime;

    public string GetText(CommandContext ctx) => Label(ctx, audio, aliasStore);

    /// <summary>How long a resolved label is reused.</summary>
    private static readonly TimeSpan LabelLifetime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The name of the current default output, as every button that shows it reads it. Shared with
    /// <see cref="AudioCycleOutputCommand"/>, which shows the same thing.
    /// </summary>
    internal static string Label(CommandContext ctx, IAudioService audio, AudioAliasStore aliasStore)
    {
        string label;
        lock (CacheLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_cachedLabel != null && Stopwatch.GetElapsedTime(_cachedAtTimestamp, now) < LabelLifetime)
            {
                label = _cachedLabel;
            }
            else
            {
                label = ResolveLabel(audio, aliasStore);
                _cachedLabel = label;
                _cachedAtTimestamp = now;
            }
        }

        // Translated on the way out, not into the cache: a language switch has to reach a label
        // that is already cached. Device names come from the OS and stay as they are.
        return label == NoDeviceLabel ? ctx.Host.Tr(NoDeviceLabel) : label;
    }

    /// <summary>Forgets the cached label, so a button refreshed right after a switch shows the new device.</summary>
    internal static void InvalidateLabel()
    {
        lock (CacheLock) _cachedLabel = null;
    }

    private static string ResolveLabel(IAudioService audio, AudioAliasStore aliasStore)
    {
        foreach (AudioEndpointInfo ep in audio.GetEndpoints(AudioEndpointKind.Render))
        {
            if (ep.IsDefault) return aliasStore.Resolve(ep);
        }
        return NoDeviceLabel;
    }

    public Task Execute(CommandContext ctx)
    {
        AudioFolderGrid grid = FolderGridResolver.Resolve(ctx.Host);
        MixerTileStyle style = MixerTileStyle.FromParameters(ctx.Parameters);
        ctx.Host.OpenFolder(new AudioDevicesFolderProvider(audio, AudioEndpointKind.Render, aliasStore, visibility, grid, ctx.Host,
            style));
        return Task.CompletedTask;
    }
}
