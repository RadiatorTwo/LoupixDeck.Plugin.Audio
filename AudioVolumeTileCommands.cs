using System.Diagnostics;
using LoupixDeck.Plugin.Audio.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Shared pieces of the single-key volume tiles.</summary>
internal static class AudioVolumeTile
{
    /// <summary>
    /// The binding: the target (device or app) first, then the look of the tile. The look is the mixer
    /// tile's, without the scroll choice: a single key has no marquee timer, so a long name is cut.
    /// </summary>
    public static IReadOnlyList<CommandParameter> Parameters(CommandParameter target) =>
        [target, .. MixerTileStyle.Parameters().Take(4)];

    public static string ParameterTemplate(string target) => $"({{{target}}},{{layout}},{{font}},{{transparent}},{{outlined}})";

    /// <summary>The tile look from the parameters after the target.</summary>
    public static MixerTileStyle Style(string[]? parameters) =>
        MixerTileStyle.FromParameters(parameters is { Length: > 1 } ? parameters[1..] : []) with
        {
            Scroll = MixerTileScroll.Off
        };

    /// <summary>The command renders the whole key, so it brings no layers of its own.</summary>
    public static ButtonLayoutDescriptor Layout { get; } = new() { Mode = ButtonLayoutMode.None };

    public static int Percent(float volume) => (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100f);

    /// <summary>One renderer for all tiles; it serialises its own drawing.</summary>
    public static MixerTileRenderer Renderer { get; } = new();

    public static bool Draw(IRenderCanvas canvas, MixerTileData data, MixerTileStyle style)
    {
        Renderer.Draw(canvas, data, style, Math.Min(canvas.Width, canvas.Height));
        return true;
    }
}

/// <summary>
/// One device's volume and mute state on a single touch key, drawn like a mixer tile: level, name,
/// greyed out and struck through while muted. Pressing it toggles mute. A new command rather than a
/// new look for <c>Audio.MuteToggle</c>, so buttons saved before keep exactly the look they had.
/// </summary>
internal sealed class AudioVolumeTileCommand(IAudioService audio, AudioAliasStore aliasStore) : IDisplayImageCommand
{
    /// <summary>How long the endpoint names and kinds are reused. Enumerating endpoints is the expensive
    /// part of a frame, the level itself is read every frame.</summary>
    private static readonly TimeSpan EndpointInfoLifetime = TimeSpan.FromSeconds(5);

    private readonly Lock _endpointLock = new();
    private Dictionary<string, (AudioEndpointInfo Endpoint, AudioEndpointKind Kind)> _endpoints = new(StringComparer.Ordinal);
    private long _endpointsStamp;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.VolumeTile",
        DisplayName = "Audio: Volume Tile",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeSet,
        ButtonLayout = AudioVolumeTile.Layout,
        Description = "Show the device volume and mute state, press to mute",
        HiddenFromMenu = true,
        ParameterTemplate = AudioVolumeTile.ParameterTemplate(AudioDeviceParameter.DeviceIdName),
        Parameters = AudioVolumeTile.Parameters(new CommandParameter(AudioDeviceParameter.DeviceIdName, typeof(string)))
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    // Reading an endpoint's level is cheap with the cached endpoint objects, so the tile can follow a
    // dial turned next to it.
    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(250);

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return;

        audio.SetMute(id, !audio.GetMute(id));
        ctx.Host.RequestButtonRefresh(Descriptor.CommandName);
    });

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        try
        {
            string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
            if (id == null) return false;

            (AudioEndpointInfo Endpoint, AudioEndpointKind Kind)? known = Lookup(id);
            string name = known is { } k ? aliasStore.Resolve(k.Endpoint) : ctx.Host.Tr("No device");
            MixerTileGlyph glyph = known?.Kind == AudioEndpointKind.Capture ? MixerTileGlyph.Microphone : MixerTileGlyph.Speaker;

            MixerTileData data = new(name, AudioVolumeTile.Percent(audio.GetVolume(id)), audio.GetMute(id),
                Selected: false, Icon: null, IconSize: 0, MarqueeFrame: 0, glyph, Framed: false);
            return AudioVolumeTile.Draw(canvas, data, AudioVolumeTile.Style(ctx.Parameters));
        }
        catch
        {
            // Runs on the host's render path: an endpoint that vanished costs this frame, not the deck.
            return false;
        }
    }

    /// <summary>The endpoint behind an id with its kind, from a list re-read at most every few seconds.</summary>
    private (AudioEndpointInfo Endpoint, AudioEndpointKind Kind)? Lookup(string id)
    {
        lock (_endpointLock)
        {
            if (_endpointsStamp == 0 || Stopwatch.GetElapsedTime(_endpointsStamp) > EndpointInfoLifetime)
            {
                Dictionary<string, (AudioEndpointInfo, AudioEndpointKind)> fresh = new(StringComparer.Ordinal);
                foreach (AudioEndpointKind kind in new[] { AudioEndpointKind.Render, AudioEndpointKind.Capture })
                {
                    foreach (AudioEndpointInfo ep in audio.GetEndpoints(kind))
                        fresh[ep.Id] = (ep, kind);
                }
                _endpoints = fresh;
                _endpointsStamp = Stopwatch.GetTimestamp();
            }

            return _endpoints.TryGetValue(id, out var entry) ? entry : null;
        }
    }
}

/// <summary>
/// One application's volume and mute state on a single touch key, with its icon where one is found. Pressing
/// it toggles the app's mute. The counterpart of <see cref="AudioVolumeTileCommand"/>, and what the
/// menu offers as an app's "Mute Toggle" on a touch key; <c>Audio.AppMuteToggle</c> stays for buttons
/// and dials, which cannot show a state, and for bindings saved before.
/// </summary>
internal sealed class AudioAppVolumeTileCommand(IAudioService audio, AppIdentityCache identity) : IDisplayImageCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppVolumeTile",
        DisplayName = "Audio: App Mute Toggle",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeSet,
        ButtonLayout = AudioVolumeTile.Layout,
        Description = "Show one application's volume and mute state, press to mute",
        HiddenFromMenu = true,
        ParameterTemplate = AudioVolumeTile.ParameterTemplate(AudioAppParameter.AppIdName),
        Parameters = AudioVolumeTile.Parameters(new CommandParameter(AudioAppParameter.AppIdName, typeof(string)))
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    // Slower than the device tile: finding an app walks the sessions of every output device.
    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(500);

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? appId = AudioAppParameter.ResolveAppId(ctx, audio);
        if (appId == null) return;

        bool? muted = audio.GetSessionMute(null, appId);
        if (muted == null) return;

        audio.SetSessionMute(null, appId, !muted.Value);
        ctx.Host.RequestButtonRefresh(Descriptor.CommandName);
    });

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        try
        {
            string? appId = AudioAppParameter.ResolveAppId(ctx, audio);
            if (appId == null) return false;

            AudioSessionInfo? session = audio.GetSessions(null)
                .FirstOrDefault(s => string.Equals(s.AppId, appId, StringComparison.Ordinal));

            // An app that is not playing has no level; it is drawn greyed out at zero rather than left
            // showing the last level it had.
            AppIdentity app = identity.Resolve(session);
            string name = session == null ? appId : AudioSessionNames.Display(session, app, ctx.Host);
            int percent = session == null ? 0 : AudioVolumeTile.Percent(session.Volume);
            bool muted = session?.Muted ?? true;

            MixerTileData data = new(name, percent, muted, Selected: false, app.Icon, app.IconSize, MarqueeFrame: 0,
                Framed: false);
            return AudioVolumeTile.Draw(canvas, data, AudioVolumeTile.Style(ctx.Parameters));
        }
        catch
        {
            return false;
        }
    }
}
