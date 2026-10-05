using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

internal static class AudioAppParameter
{
    public const string AppIdName = "appId";
    public const string StepName = "step";
    public const string PercentName = "percent";

    /// <summary>Reserved AppId resolved to the foreground window's process at execution time.</summary>
    public const string ForegroundAppId = "@foreground";

    public const int DefaultStepPercent = 5;
    public const int DefaultPercent = 50;

    public static IReadOnlyList<CommandParameter> StepParameters { get; } =
    [
        new CommandParameter(AppIdName, typeof(string)),
        new CommandParameter(StepName, typeof(int))
        {
            DefaultValue = DefaultStepPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    public static IReadOnlyList<CommandParameter> PercentParameters { get; } =
    [
        new CommandParameter(AppIdName, typeof(string)),
        new CommandParameter(PercentName, typeof(int))
        {
            DefaultValue = DefaultPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    public static IReadOnlyList<CommandParameter> AppIdParameters { get; } =
        [new CommandParameter(AppIdName, typeof(string))];

    /// <summary>
    /// Resolves the bound AppId, expanding the foreground sentinel. Returns null when the
    /// binding carries no app or the foreground app cannot be determined.
    /// </summary>
    public static string? ResolveAppId(CommandContext ctx, IAudioService audio)
    {
        string[]? p = ctx.Parameters;
        if (p == null || p.Length == 0) return null;

        string id = p[0];
        if (string.IsNullOrWhiteSpace(id)) return null;

        return string.Equals(id, ForegroundAppId, StringComparison.Ordinal)
            ? audio.GetForegroundAppId()
            : id;
    }

    /// <summary>
    /// The bound app, reporting on the dial when there is none. Silence is the wrong answer here:
    /// the foreground sentinel resolves to nothing whenever the window in front belongs to no
    /// process we can read — including LoupixDeck's own window while the user is configuring — and
    /// a dial that does nothing without saying why is indistinguishable from a broken binding.
    /// </summary>
    public static string? ResolveAppIdOrReport(CommandContext ctx, IAudioService audio)
    {
        string? appId = ResolveAppId(ctx, audio);
        if (appId == null)
            AudioDeviceParameter.ShowOverlay(ctx, "No app in front");

        return appId;
    }

    /// <summary>
    /// Says that the app has no audio session to change, which is what a resolved app that is not
    /// playing anything looks like from here.
    /// </summary>
    public static void ReportNoSession(CommandContext ctx, string appId) =>
        // The app id is a value, so only the fixed part is a key.
        AudioDeviceParameter.ShowOverlay(ctx, string.Format(ctx.Host.Tr("{0}: no audio"), appId));

    /// <summary>App id plus the mute state to set, muting by default.</summary>
    public static IReadOnlyList<CommandParameter> SetMuteParameters { get; } =
    [
        new CommandParameter(AppIdName, typeof(string)),
        new CommandParameter(AudioDeviceParameter.MutedName, typeof(bool)) { DefaultValue = "True" }
    ];

    /// <summary>Sets the mute flag of every session of the bound app. Idempotent, unlike the toggle.</summary>
    public static Task SetMute(CommandContext ctx, IAudioService audio, string commandName, bool muted) =>
        AudioDeviceParameter.Guard(ctx, commandName, () =>
        {
            string? appId = ResolveAppIdOrReport(ctx, audio);
            if (appId == null) return;

            if (audio.GetSessionMute(null, appId) == null)
            {
                ReportNoSession(ctx, appId);
                return;
            }

            audio.SetSessionMute(null, appId, muted);
            AudioDeviceParameter.ShowOverlay(ctx, muted ? "🔇" : $"🔊 {appId}");
        });

    public static int ResolveInt(CommandContext ctx, int fallback)
    {
        string[]? p = ctx.Parameters;
        if (p != null && p.Length > 1 &&
            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }
        return fallback;
    }
}

internal sealed class AudioAppVolumeUpCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppVolumeUp",
        DisplayName = "Audio: App Volume Up",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeUp,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.VolumeUp, "App Volume +"),
        Description = "Raise the volume of one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{step})",
        Parameters = AudioAppParameter.StepParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return;

        float? current = audio.GetSessionVolume(null, appId);
        if (current == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return;
        }

        float step = Math.Abs(AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultStepPercent)) / 100f;
        float next = Math.Clamp(current.Value + step, 0f, 1f);
        audio.SetSessionVolume(null, appId, next);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(next)}");
    });
}

internal sealed class AudioAppVolumeDownCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppVolumeDown",
        DisplayName = "Audio: App Volume Down",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeDown,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.VolumeDown, "App Volume -"),
        Description = "Lower the volume of one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{step})",
        Parameters = AudioAppParameter.StepParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return;

        float? current = audio.GetSessionVolume(null, appId);
        if (current == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return;
        }

        float step = Math.Abs(AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultStepPercent)) / 100f;
        float next = Math.Clamp(current.Value - step, 0f, 1f);
        audio.SetSessionVolume(null, appId, next);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(next)}");
    });
}

internal sealed class AudioAppMuteToggleCommand(IAudioService audio) : IDisplayImageCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppMuteToggle",
        DisplayName = "Audio: App Mute Toggle",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mute,
        ButtonLayout = AudioMuteStateKey.Layout,
        Description = "Toggle mute for one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{showState})",
        Parameters = [new CommandParameter(AudioAppParameter.AppIdName, typeof(string)), AudioMuteStateKey.ShowStateParameter]
    };

    // Slower than the device toggle: finding an app walks the sessions of every output device.
    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(500);

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        if (AudioMuteStateKey.IsLegacy(ctx)) return false;
        if (!AudioMuteStateKey.Enabled(ctx)) return AudioMuteStateKey.DrawStatic(canvas, ctx.Host.Tr("App Mute"));

        try
        {
            string? appId = AudioAppParameter.ResolveAppId(ctx, audio);
            if (appId == null) return false;

            AudioSessionInfo? session = audio.GetSessions(null)
                .FirstOrDefault(s => string.Equals(s.AppId, appId, StringComparison.Ordinal));

            // An app that is not playing has nothing to mute; it reads as live with no level.
            bool muted = session?.Muted ?? false;
            string caption = muted ? ctx.Host.Tr("Muted")
                : session == null ? appId
                : AudioDeviceParameter.FormatVolume(session.Volume);
            return AudioMuteStateKey.Draw(canvas, muted, microphone: false, caption);
        }
        catch
        {
            return false;
        }
    }

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return;

        bool? muted = audio.GetSessionMute(null, appId);
        if (muted == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return;
        }

        audio.SetSessionMute(null, appId, !muted.Value);
        AudioDeviceParameter.ShowOverlay(ctx, !muted.Value ? "🔇" : $"🔊 {appId}");
        ctx.Host.RequestButtonRefresh(Descriptor.CommandName);
    });
}

internal sealed class AudioAppMuteCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppMute",
        DisplayName = "Audio: App Mute",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mute,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Mute, "App Mute"),
        Description = "Mute one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId})",
        Parameters = AudioAppParameter.AppIdParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) =>
        AudioAppParameter.SetMute(ctx, audio, Descriptor.CommandName, true);
}

internal sealed class AudioAppUnmuteCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppUnmute",
        DisplayName = "Audio: App Unmute",
        Group = "Audio",
        Icon = AudioButtonLayouts.Unmute,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Unmute, "App Unmute"),
        Description = "Unmute one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId})",
        Parameters = AudioAppParameter.AppIdParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) =>
        AudioAppParameter.SetMute(ctx, audio, Descriptor.CommandName, false);
}

internal sealed class AudioAppSetMuteCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppSetMute",
        DisplayName = "Audio: Set App Mute",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mute,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Mute, "Set App Mute"),
        Description = "Mute or unmute one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{muted})",
        Parameters = AudioAppParameter.SetMuteParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) =>
        AudioAppParameter.SetMute(ctx, audio, Descriptor.CommandName, AudioDeviceParameter.ResolveMuted(ctx));
}

internal sealed class AudioAppSetVolumeCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppSetVolume",
        DisplayName = "Audio: Set App Volume",
        Group = "Audio",
        Icon = AudioButtonLayouts.VolumeSet,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.VolumeSet, "Set App Volume"),
        Description = "Set one application to a fixed volume",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{percent})",
        Parameters = AudioAppParameter.PercentParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return;

        float target = Math.Clamp(
            AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultPercent), 0, 100) / 100f;
        audio.SetSessionVolume(null, appId, target);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(target)}");
    });
}
