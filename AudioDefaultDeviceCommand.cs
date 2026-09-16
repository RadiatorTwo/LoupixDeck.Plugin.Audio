using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Makes one endpoint the system default. Bound from the command menu under
/// Audio → Output/Input Devices → &lt;device&gt; → Set as Default.
/// </summary>
internal sealed class AudioSetDefaultDeviceCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.SetDefaultDevice",
        DisplayName = "Audio: Set Default Device",
        Group = "Audio",
        Icon = "\U000F04C2",
        Description = "Make this the default audio device",
        HiddenFromMenu = true,
        ParameterTemplate = "({deviceId})",
        Parameters = AudioDeviceParameter.DeviceIdParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        // Deliberately the raw overload: "make the default device the default" is not an action,
        // so the @default sentinel simply does not resolve here.
        string? id = AudioDeviceParameter.ResolveDeviceId(ctx);
        if (id == null) return Task.CompletedTask;

        if (!audio.SetDefaultEndpoint(id))
        {
            ctx.Host.Logger.Warn($"Audio: could not make '{id}' the default device.");
            AudioDeviceParameter.ShowOverlay(ctx, "Failed");
            return Task.CompletedTask;
        }

        AudioDeviceParameter.ShowOverlay(ctx, "Default");
        return Task.CompletedTask;
    }
}
