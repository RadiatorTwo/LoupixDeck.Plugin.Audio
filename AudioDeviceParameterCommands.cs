using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

internal static class AudioDeviceParameter
{
    public const string DeviceIdName = "deviceId";
    public const string StepName = "step";

    /// <summary>Default volume step in percent, pre-filled into the command's settings
    /// flyout (SDK 1.17 command-defined parameter defaults). 2% matches the former fixed step.</summary>
    public const int DefaultStepPercent = 2;

    /// <summary>Reserved endpoint id resolved to the current default render device at execution
    /// time. A binding that carries it keeps meaning the right thing after the user switches
    /// their default output, and travels to another machine, which a raw endpoint id does not.
    /// </summary>
    public const string DefaultDeviceId = "@default";

    /// <summary>The capture counterpart of <see cref="DefaultDeviceId"/>: the current default
    /// input device, so a microphone button keeps working when the microphone changes.</summary>
    public const string DefaultInputDeviceId = "@defaultInput";

    public static string? ResolveDeviceId(CommandContext ctx) => ResolveDeviceId(ctx, null);

    public static string? ResolveDeviceId(CommandContext ctx, IAudioService? audio)
    {
        var p = ctx.Parameters;
        if (p == null || p.Length == 0) return null;
        var id = p[0];
        if (string.IsNullOrWhiteSpace(id)) return null;

        return ResolveEndpointId(id, audio);
    }

    /// <summary>
    /// Expands a default-device sentinel to the endpoint it currently means. Every consumer
    /// that talks to the audio backend has to go through this: <c>@default</c> and
    /// <c>@defaultInput</c> are not endpoint ids, so passing them on unresolved silently reads
    /// and writes nothing.
    /// </summary>
    public static string? ResolveEndpointId(string? id, IAudioService? audio)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        return SentinelKind(id) is { } kind ? ResolveDefaultId(kind, audio) : id;
    }

    /// <summary>The endpoint kind a default-device sentinel stands for, or null for a real endpoint id.</summary>
    private static AudioEndpointKind? SentinelKind(string? id)
    {
        if (string.Equals(id, DefaultDeviceId, StringComparison.Ordinal)) return AudioEndpointKind.Render;
        if (string.Equals(id, DefaultInputDeviceId, StringComparison.Ordinal)) return AudioEndpointKind.Capture;
        return null;
    }

    /// <summary>
    /// The current default endpoint of one kind, memoised for <see cref="DefaultCacheMs"/>.
    /// <para>
    /// This runs on the side-strip render path: a dial's adjustment value is pulled once per
    /// frame per dial, and both the host's indicator and the plugin's own bar pull it. Asking
    /// the backend every time costs a full endpoint enumeration per pull - on Windows that
    /// reads every endpoint's friendly name from the property store (~220 ms for eight
    /// endpoints), on Linux it spawns two pactl processes - which is what made a dial turn
    /// stall the strip queue for seconds. The dedicated default-id query is ~1 ms, and the
    /// window collapses the several pulls of one frame onto a single backend call.
    /// </para>
    /// </summary>
    private static string? ResolveDefaultId(AudioEndpointKind kind, IAudioService? audio)
    {
        if (audio == null) return null;

        lock (_defaultLock)
        {
            if (_defaultIds.TryGetValue(kind, out var cached) &&
                Environment.TickCount64 - cached.Tick < DefaultCacheMs)
            {
                return cached.Id;
            }

            string? resolved = audio.GetDefaultEndpointId(kind);
            if (resolved == null) return null;

            _defaultIds[kind] = (resolved, Environment.TickCount64);
            return resolved;
        }
    }

    /// <summary>
    /// Drops the memoised default endpoints, so the next resolution asks the backend again.
    /// Called by a consumer that just learned the default may have moved and needs the new
    /// endpoint now rather than at the end of the window.
    /// </summary>
    public static void InvalidateDefaultEndpoint()
    {
        lock (_defaultLock) _defaultIds.Clear();
    }

    /// <summary>How long a resolved default endpoint is reused. Short enough that a default
    /// switched outside the app is picked up on its own, long enough that the several pulls of
    /// one strip frame cost one backend call.</summary>
    private const long DefaultCacheMs = 1000;

    private static readonly object _defaultLock = new();
    private static readonly Dictionary<AudioEndpointKind, (string Id, long Tick)> _defaultIds = [];

    /// <summary>True when the bound id is a default-device sentinel rather than an endpoint.</summary>
    public static bool IsDefaultSentinel(string? id) => SentinelKind(id) != null;

    /// <summary>Resolves the configured volume step (parameter index 1, in percent) as a
    /// 0..1 scalar, falling back to <see cref="DefaultStepPercent"/> when absent/invalid.</summary>
    public static float ResolveStepScalar(CommandContext ctx)
    {
        var p = ctx.Parameters;
        var percent = DefaultStepPercent;
        if (p != null && p.Length > 1 &&
            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed != 0)
        {
            percent = Math.Abs(parsed);
        }

        return percent / 100f;
    }

    public static IReadOnlyList<CommandParameter> DeviceIdParameters { get; } =
        [new CommandParameter(DeviceIdName, typeof(string))];

    /// <summary>Device id (menu-provided target) plus a configurable, pre-filled step. Used by
    /// the volume up/down commands so the step size is editable per assignment.</summary>
    public static IReadOnlyList<CommandParameter> VolumeStepParameters { get; } =
    [
        new CommandParameter(DeviceIdName, typeof(string)),
        new CommandParameter(StepName, typeof(int))
        {
            DefaultValue = DefaultStepPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    public const string PercentName = "percent";

    /// <summary>Default target level for the set-to-percent command, pre-filled in the flyout.</summary>
    public const int DefaultPercent = 50;

    /// <summary>Device id plus an absolute target level in percent.</summary>
    public static IReadOnlyList<CommandParameter> VolumePercentParameters { get; } =
    [
        new CommandParameter(DeviceIdName, typeof(string)),
        new CommandParameter(PercentName, typeof(int))
        {
            DefaultValue = DefaultPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    /// <summary>Resolves the absolute target level (parameter index 1, percent) as a 0..1 scalar.</summary>
    public static float ResolvePercentScalar(CommandContext ctx)
    {
        string[]? p = ctx.Parameters;
        int percent = DefaultPercent;
        if (p != null && p.Length > 1 &&
            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            percent = Math.Clamp(parsed, 0, 100);
        }
        return percent / 100f;
    }

    public const string MutedName = "muted";

    /// <summary>Device id plus the mute state to set. Muting is the pre-filled choice, as it is
    /// what a button bound to "set mute" usually does.</summary>
    public static IReadOnlyList<CommandParameter> SetMuteParameters { get; } =
    [
        new CommandParameter(DeviceIdName, typeof(string)),
        new CommandParameter(MutedName, typeof(bool)) { DefaultValue = "True" }
    ];

    /// <summary>Resolves the mute state to set (parameter index 1), muting when absent or unreadable.</summary>
    public static bool ResolveMuted(CommandContext ctx)
    {
        string[]? p = ctx.Parameters;
        return p is not { Length: > 1 } || !bool.TryParse(p[1], out bool parsed) || parsed;
    }

    /// <summary>Sets the device's mute flag and says so on the dial. Unlike the toggle it does the
    /// same thing however often it runs, which is what a macro or multi-action needs.</summary>
    public static Task SetMute(CommandContext ctx, IAudioService audio, string commandName, bool muted) =>
        Guard(ctx, commandName, () =>
        {
            string? id = ResolveDeviceId(ctx, audio);
            if (id == null) return;

            audio.SetMute(id, muted);
            ShowOverlay(ctx, muted ? "🔇" : $"🔊 {FormatVolume(audio.GetVolume(id))}");
        });

    public static void ShowOverlay(CommandContext ctx, string text)
    {
        if (ctx.SourceIndex is not int rotaryIdx) return;
        var slot = ctx.Host.GetTouchSlotForRotary(rotaryIdx);
        if (slot < 0) return;
        ctx.Host.OverlayTouchText(slot, text, AudioPlugin.VolumeOverlayDuration);
    }

    public static string FormatVolume(float scalar01) => $"{(int)Math.Round(scalar01 * 100f)}%";

    /// <summary>
    /// Runs a command body that talks to the audio backend. An endpoint that vanished, a COM error
    /// while the audio service restarts or a failed pactl call is logged and shown as "Failed" on
    /// the dial instead of escaping into the host.
    /// </summary>
    public static Task Guard(CommandContext ctx, string commandName, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            ctx.Host.Logger?.Warn($"{commandName} failed: {ex.Message}");
            ShowOverlay(ctx, ctx.Host.Tr("Failed"));
        }

        return Task.CompletedTask;
    }
}

internal sealed class AudioVolumeUpCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.VolumeUp",
        DisplayName = "Audio: Volume Up",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeUp,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.VolumeUp, "Volume +"),
        Description = "Raise the device volume",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId},{step})",
        Parameters = AudioDeviceParameter.VolumeStepParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return;
        var next = Math.Clamp(audio.GetVolume(id) + AudioDeviceParameter.ResolveStepScalar(ctx), 0f, 1f);
        audio.SetVolume(id, next);
        AudioDeviceParameter.ShowOverlay(ctx, AudioDeviceParameter.FormatVolume(next));
    });
}

internal sealed class AudioVolumeDownCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.VolumeDown",
        DisplayName = "Audio: Volume Down",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeDown,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.VolumeDown, "Volume -"),
        Description = "Lower the device volume",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId},{step})",
        Parameters = AudioDeviceParameter.VolumeStepParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return;
        var next = Math.Clamp(audio.GetVolume(id) - AudioDeviceParameter.ResolveStepScalar(ctx), 0f, 1f);
        audio.SetVolume(id, next);
        AudioDeviceParameter.ShowOverlay(ctx, AudioDeviceParameter.FormatVolume(next));
    });
}

internal sealed class AudioMuteToggleCommand(IAudioService audio) : IDisplayImageCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.MuteToggle",
        DisplayName = "Audio: Mute Toggle",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mute,
        ButtonLayout = AudioMuteStateKey.Layout,
        Description = "Toggle mute for the device",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId},{showState})",
        Parameters = [new CommandParameter(AudioDeviceParameter.DeviceIdName, typeof(string)), AudioMuteStateKey.ShowStateParameter]
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    // Reading a level is cheap with the cached endpoint objects, so a mute from elsewhere shows quickly.
    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(250);

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return;
        var muted = !audio.GetMute(id);
        audio.SetMute(id, muted);
        AudioDeviceParameter.ShowOverlay(ctx, muted ? "🔇" : $"🔊 {AudioDeviceParameter.FormatVolume(audio.GetVolume(id))}");
        ctx.Host.RequestButtonRefresh(Descriptor.CommandName);
    });

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        if (AudioMuteStateKey.IsLegacy(ctx)) return false;
        if (!AudioMuteStateKey.Enabled(ctx)) return AudioMuteStateKey.DrawStatic(canvas, ctx.Host.Tr("Mute"));

        try
        {
            string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
            if (id == null) return false;

            bool muted = audio.GetMute(id);
            string caption = muted ? ctx.Host.Tr("Muted") : AudioDeviceParameter.FormatVolume(audio.GetVolume(id));
            return AudioMuteStateKey.Draw(canvas, muted, AudioMuteStateKey.IsCapture(ctx.Parameters[0], id), caption);
        }
        catch
        {
            // Runs on the host's render path: an endpoint that vanished costs this frame, not the deck.
            return false;
        }
    }
}

internal sealed class AudioMuteCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.Mute",
        DisplayName = "Audio: Mute",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mute,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Mute, "Mute"),
        Description = "Mute the device",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId})",
        Parameters = AudioDeviceParameter.DeviceIdParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) =>
        AudioDeviceParameter.SetMute(ctx, audio, Descriptor.CommandName, true);
}

internal sealed class AudioUnmuteCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.Unmute",
        DisplayName = "Audio: Unmute",
        Group = "Audio",
        Icon = AudioButtonLayouts.Unmute,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Unmute, "Unmute"),
        Description = "Unmute the device",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId})",
        Parameters = AudioDeviceParameter.DeviceIdParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) =>
        AudioDeviceParameter.SetMute(ctx, audio, Descriptor.CommandName, false);
}

internal sealed class AudioSetMuteCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.SetMute",
        DisplayName = "Audio: Set Mute",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mute,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Mute, "Set Mute"),
        Description = "Mute or unmute the device",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId},{muted})",
        Parameters = AudioDeviceParameter.SetMuteParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) =>
        AudioDeviceParameter.SetMute(ctx, audio, Descriptor.CommandName, AudioDeviceParameter.ResolveMuted(ctx));
}

internal sealed class AudioSetVolumeCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.SetVolume",
        DisplayName = "Audio: Set Volume",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeSet,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.VolumeSet, "Set Volume"),
        Description = "Set the device volume to a fixed level",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId},{percent})",
        Parameters = AudioDeviceParameter.VolumePercentParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return;

        float target = AudioDeviceParameter.ResolvePercentScalar(ctx);
        audio.SetVolume(id, target);
        AudioDeviceParameter.ShowOverlay(ctx, AudioDeviceParameter.FormatVolume(target));
    });
}
