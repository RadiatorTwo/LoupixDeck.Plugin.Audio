using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

internal static class AudioDeviceParameter
{
    public const string DeviceIdName = "deviceId";
    public const float StepScalar = 0.02f;

    public static string? ResolveDeviceId(CommandContext ctx)
    {
        var p = ctx.Parameters;
        if (p == null || p.Length == 0) return null;
        var id = p[0];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public static IReadOnlyList<CommandParameter> DeviceIdParameters { get; } =
        [new CommandParameter(DeviceIdName, typeof(string))];

    public static void ShowOverlay(CommandContext ctx, string text)
    {
        if (ctx.SourceIndex is not int rotaryIdx) return;
        var slot = ctx.Host.GetTouchSlotForRotary(rotaryIdx);
        if (slot < 0) return;
        ctx.Host.OverlayTouchText(slot, text, AudioPlugin.VolumeOverlayDuration);
    }

    public static string FormatVolume(float scalar01) => $"{(int)Math.Round(scalar01 * 100f)}%";
}

internal sealed class AudioVolumeUpCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.VolumeUp",
        DisplayName = "Audio: Volume Up",
        Group = "Audio",
        Icon = "\U000F057E",
        Description = "Raise the device volume",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId})",
        Parameters = AudioDeviceParameter.DeviceIdParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx);
        if (id == null) return Task.CompletedTask;
        var next = Math.Clamp(audio.GetVolume(id) + AudioDeviceParameter.StepScalar, 0f, 1f);
        audio.SetVolume(id, next);
        AudioDeviceParameter.ShowOverlay(ctx, AudioDeviceParameter.FormatVolume(next));
        return Task.CompletedTask;
    }
}

internal sealed class AudioVolumeDownCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.VolumeDown",
        DisplayName = "Audio: Volume Down",
        Group = "Audio",
        Icon = "\U000F057F",
        Description = "Lower the device volume",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId})",
        Parameters = AudioDeviceParameter.DeviceIdParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx);
        if (id == null) return Task.CompletedTask;
        var next = Math.Clamp(audio.GetVolume(id) - AudioDeviceParameter.StepScalar, 0f, 1f);
        audio.SetVolume(id, next);
        AudioDeviceParameter.ShowOverlay(ctx, AudioDeviceParameter.FormatVolume(next));
        return Task.CompletedTask;
    }
}

internal sealed class AudioMuteToggleCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.MuteToggle",
        DisplayName = "Audio: Mute Toggle",
        Group = "Audio",
        Icon = "\U000F075F",
        Description = "Toggle mute for the device",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId})",
        Parameters = AudioDeviceParameter.DeviceIdParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx);
        if (id == null) return Task.CompletedTask;
        var muted = !audio.GetMute(id);
        audio.SetMute(id, muted);
        AudioDeviceParameter.ShowOverlay(ctx, muted ? "🔇" : $"🔊 {AudioDeviceParameter.FormatVolume(audio.GetVolume(id))}");
        return Task.CompletedTask;
    }
}
