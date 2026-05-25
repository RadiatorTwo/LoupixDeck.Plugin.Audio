using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Opens the audio output-device folder. Command name kept identical to the
/// former built-in command.
/// </summary>
internal sealed class AudioOutputFolderCommand(IAudioService audio, AudioAliasStore aliasStore) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.OutputDevices",
        DisplayName = "Audio: Output Devices",
        Group = "Audio",
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        ctx.Host.OpenFolder(new AudioDevicesFolderProvider(audio, AudioEndpointKind.Render, aliasStore));
        return Task.CompletedTask;
    }
}

/// <summary>Opens the audio input-device folder.</summary>
internal sealed class AudioInputFolderCommand(IAudioService audio, AudioAliasStore aliasStore) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.InputDevices",
        DisplayName = "Audio: Input Devices",
        Group = "Audio",
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        ctx.Host.OpenFolder(new AudioDevicesFolderProvider(audio, AudioEndpointKind.Capture, aliasStore));
        return Task.CompletedTask;
    }
}
