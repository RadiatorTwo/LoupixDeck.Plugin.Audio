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
        Icon = "\U000F057E",
        Description = "Show the active output device, and open the picker when pressed"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public string GetText(CommandContext ctx)
    {
        lock (CacheLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_cachedLabel != null && Stopwatch.GetElapsedTime(_cachedAtTimestamp, now) < UpdateInterval)
                return _cachedLabel;

            string label = ResolveLabel();
            _cachedLabel = label;
            _cachedAtTimestamp = now;
            return label;
        }
    }

    private string ResolveLabel()
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
        ctx.Host.OpenFolder(new AudioDevicesFolderProvider(audio, AudioEndpointKind.Render, aliasStore, visibility, grid));
        return Task.CompletedTask;
    }
}
