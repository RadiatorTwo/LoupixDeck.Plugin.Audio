using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The device volume as one rotary adjustment: turn to change it, press to mute. Replaces the
/// three-command dial (<c>Audio.VolumeUp</c> / <c>Audio.VolumeDown</c> / <c>Audio.MuteToggle</c>),
/// which stays registered so existing bindings, macros and buttons keep working.
/// <para>
/// Beyond the single assignment this is what lets the dial show its level: the host reads
/// <see cref="GetValue"/> for the indicator, and the side-strip volume bars read the same value
/// instead of resolving the endpoint from the bound command a second time.
/// </para>
/// </summary>
internal sealed class AudioVolumeCommand(IAudioService audio) : IAdjustmentCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.Volume",
        DisplayName = "Audio: Volume",
        Group = "Audio",
        Icon = "\U000F057E",
        Description = "Turn to change the device volume, press to mute",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId},{step})",
        Parameters = AudioDeviceParameter.VolumeStepParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    public Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return Task.CompletedTask;

        // ticks carries the sign and the count, so one call covers a fast turn that the old
        // per-detent commands would have run several times.
        float next = Math.Clamp(audio.GetVolume(id) + (ticks * AudioDeviceParameter.ResolveStepScalar(ctx)), 0f, 1f);
        audio.SetVolume(id, next);
        AudioDeviceParameter.ShowOverlay(ctx, AudioDeviceParameter.FormatVolume(next));
        return Task.CompletedTask;
    }

    public Task ApplyReset(CommandContext ctx)
    {
        string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
        if (id == null) return Task.CompletedTask;

        // Mute is what the knob press did before this command existed, and it is the one
        // "reset" a volume has that a user would expect from a press.
        bool muted = !audio.GetMute(id);
        audio.SetMute(id, muted);
        AudioDeviceParameter.ShowOverlay(ctx,
            muted ? "🔇" : $"🔊 {AudioDeviceParameter.FormatVolume(audio.GetVolume(id))}");
        return Task.CompletedTask;
    }

    /// <summary>Off a dial — a macro, the CLI — the command acts as the press does.</summary>
    public Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        try
        {
            string? id = AudioDeviceParameter.ResolveDeviceId(ctx, audio);
            if (id == null) return null;

            float volume = audio.GetVolume(id);
            return new AdjustmentValue(volume,
                audio.GetMute(id) ? "🔇" : AudioDeviceParameter.FormatVolume(volume));
        }
        catch
        {
            // Runs on the render path: an endpoint that disappeared between the frame and the
            // query costs the indicator, not the strip.
            return null;
        }
    }
}

/// <summary>
/// One application's volume as a rotary adjustment: turn to change it, press to mute. The
/// counterpart of <see cref="AudioVolumeCommand"/> for the per-app dial, replacing
/// <c>Audio.AppVolumeUp</c> / <c>Audio.AppVolumeDown</c> / <c>Audio.AppMuteToggle</c>.
/// </summary>
internal sealed class AudioAppVolumeCommand(IAudioService audio) : IAdjustmentCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppVolume",
        DisplayName = "Audio: App Volume",
        Group = "Audio",
        Icon = "\U000F057E",
        Description = "Turn to change one application's volume, press to mute",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{step})",
        Parameters = AudioAppParameter.StepParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    public Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return Task.CompletedTask;

        float? current = audio.GetSessionVolume(null, appId);
        if (current == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return Task.CompletedTask;
        }

        float step = Math.Abs(AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultStepPercent)) / 100f;
        float next = Math.Clamp(current.Value + (ticks * step), 0f, 1f);
        audio.SetSessionVolume(null, appId, next);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(next)}");
        return Task.CompletedTask;
    }

    public Task ApplyReset(CommandContext ctx)
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return Task.CompletedTask;

        bool? muted = audio.GetSessionMute(null, appId);
        if (muted == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return Task.CompletedTask;
        }

        audio.SetSessionMute(null, appId, !muted.Value);
        AudioDeviceParameter.ShowOverlay(ctx, !muted.Value ? "🔇" : $"🔊 {appId}");
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="AudioVolumeCommand.Execute"/>
    public Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        try
        {
            string? appId = AudioAppParameter.ResolveAppId(ctx, audio);
            if (appId == null) return null;

            // An app that is not playing has no session and therefore no level. Reporting
            // nothing is the honest answer: the dial falls back to its label.
            float? volume = audio.GetSessionVolume(null, appId);
            if (volume == null) return null;

            bool muted = audio.GetSessionMute(null, appId) ?? false;
            return new AdjustmentValue(volume.Value,
                muted ? "🔇" : AudioDeviceParameter.FormatVolume(volume.Value));
        }
        catch
        {
            return null;
        }
    }
}
