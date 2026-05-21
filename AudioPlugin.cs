using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Entry point of the Windows Audio plugin (Windows only). Contributes commands
/// that open a folder for picking an output/input device and adjusting its
/// volume and mute state.
/// </summary>
public sealed class AudioPlugin : LoupixPlugin
{
    private readonly WindowsAudioService _audio = new();
    private List<IPluginCommand> _commands = [];

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "audio",
        Name = "Windows Audio",
        Version = new Version(1, 0, 0),
        SdkVersion = new Version(1, 2, 0),
        Author = "RadiatorTwo",
        Description = "Pick the active audio output/input device and adjust volume and mute from the device."
    };

    public override void Initialize(IPluginHost host)
    {
        _commands =
        [
            new AudioOutputFolderCommand(_audio),
            new AudioInputFolderCommand(_audio)
        ];
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;
}
