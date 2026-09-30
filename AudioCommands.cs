using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Opens the audio output-device folder. Command name kept identical to the
/// former built-in command.
/// </summary>
internal sealed class AudioOutputFolderCommand(IAudioService audio, AudioAliasStore aliasStore,
    AudioVisibilityStore visibility) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.OutputDevices",
        DisplayName = "Audio: Output Devices",
        Group = "Audio",
        Icon = AudioButtonLayouts.Speaker,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Speaker, "Output"),
        Description = "Open the output device picker folder",
        HiddenFromMenu = true,
        ParameterTemplate = MixerTileStyle.ParameterTemplate,
        Parameters = MixerTileStyle.Parameters()
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        AudioFolderGrid grid = FolderGridResolver.Resolve(ctx.Host);
        MixerTileStyle style = MixerTileStyle.FromParameters(ctx.Parameters);
        ctx.Host.OpenFolder(new AudioDevicesFolderProvider(audio, AudioEndpointKind.Render, aliasStore, visibility, grid, ctx.Host, style));
        return Task.CompletedTask;
    }
}

/// <summary>Opens the audio input-device folder.</summary>
internal sealed class AudioInputFolderCommand(IAudioService audio, AudioAliasStore aliasStore,
    AudioVisibilityStore visibility) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.InputDevices",
        DisplayName = "Audio: Input Devices",
        Group = "Audio",
        Icon = AudioButtonLayouts.Microphone,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Microphone, "Input"),
        Description = "Open the input device picker folder",
        HiddenFromMenu = true,
        ParameterTemplate = MixerTileStyle.ParameterTemplate,
        Parameters = MixerTileStyle.Parameters()
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        AudioFolderGrid grid = FolderGridResolver.Resolve(ctx.Host);
        MixerTileStyle style = MixerTileStyle.FromParameters(ctx.Parameters);
        ctx.Host.OpenFolder(new AudioDevicesFolderProvider(audio, AudioEndpointKind.Capture, aliasStore, visibility, grid, ctx.Host, style));
        return Task.CompletedTask;
    }
}

/// <summary>Opens the per-application mixer folder.</summary>
internal sealed class AudioMixerFolderCommand(IAudioService audio, AppIdentityCache identity) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.Mixer",
        DisplayName = "Audio: Mixer",
        Group = "Audio",
        Icon = AudioButtonLayouts.Mixer,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Mixer, "Mixer"),
        Description = "Open the per-application volume mixer",
        HiddenFromMenu = true,
        ParameterTemplate = MixerTileStyle.ParameterTemplate,
        Parameters = MixerTileStyle.Parameters()
    };

    public ButtonTargets SupportedTargets => ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        AudioFolderGrid grid = FolderGridResolver.Resolve(ctx.Host);
        MixerTileStyle style = MixerTileStyle.FromParameters(ctx.Parameters);
        ctx.Host.OpenFolder(new AudioMixerFolderProvider(audio, grid, ctx.Host, style, identity));
        return Task.CompletedTask;
    }
}
