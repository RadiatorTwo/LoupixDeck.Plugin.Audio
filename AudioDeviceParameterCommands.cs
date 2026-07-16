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

    public static string? ResolveDeviceId(CommandContext ctx)
    {
        var p = ctx.Parameters;
        if (p == null || p.Length == 0) return null;
        var id = p[0];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

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
        ParameterTemplate = "({deviceId},{step})",
        Parameters = AudioDeviceParameter.VolumeStepParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx);
        if (id == null) return Task.CompletedTask;
        var next = Math.Clamp(audio.GetVolume(id) + AudioDeviceParameter.ResolveStepScalar(ctx), 0f, 1f);
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
        ParameterTemplate = "({deviceId},{step})",
        Parameters = AudioDeviceParameter.VolumeStepParameters
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var id = AudioDeviceParameter.ResolveDeviceId(ctx);
        if (id == null) return Task.CompletedTask;
        var next = Math.Clamp(audio.GetVolume(id) - AudioDeviceParameter.ResolveStepScalar(ctx), 0f, 1f);
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
