using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Entry point of the Audio plugin. Contributes commands that open a folder
/// for picking an output/input device and adjusting its volume and mute state.
/// Backed by WASAPI on Windows and pactl (PulseAudio / pipewire-pulse) on Linux.
/// </summary>
public sealed class AudioPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor
{
    private readonly IAudioService _audio = CreateAudioService();
    private List<IPluginCommand> _commands = [];
    private AudioAliasStore? _aliasStore;

    internal static readonly TimeSpan VolumeOverlayDuration = TimeSpan.FromMilliseconds(1500);

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "audio",
        Name = "Audio",
        Version = new Version(1, 4, 0),
        SdkVersion = new Version(1, 4, 0),
        Author = "RadiatorTwo",
        Description = "Pick the active audio output/input device and adjust volume and mute from the device."
    };

    public override void Initialize(IPluginHost host)
    {
        if (!_audio.IsSupported) return;

        _aliasStore = new AudioAliasStore(host.Settings);

        _commands =
        [
            new AudioOutputFolderCommand(_audio, _aliasStore),
            new AudioInputFolderCommand(_audio, _aliasStore),
            new AudioVolumeUpCommand(_audio),
            new AudioVolumeDownCommand(_audio),
            new AudioMuteToggleCommand(_audio),
        ];
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    // ---- IMenuContributor ----

    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (!_audio.IsSupported || _aliasStore == null)
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        var outputs = _audio.GetEndpoints(AudioEndpointKind.Render);
        var inputs = _audio.GetEndpoints(AudioEndpointKind.Capture);

        MenuNode AdjustmentGroup(string commandName, string label)
        {
            List<MenuNode> children = [];
            if (outputs.Count > 0) children.Add(DeviceGroup("Output Devices", outputs, commandName));
            if (inputs.Count > 0) children.Add(DeviceGroup("Input Devices", inputs, commandName));
            return new MenuNode { Name = label, CommandName = string.Empty, Children = children };
        }

        List<MenuNode> rootChildren =
        [
            new MenuNode { Name = "Output Devices", CommandName = "Audio.OutputDevices" },
            new MenuNode { Name = "Input Devices", CommandName = "Audio.InputDevices" },
            AdjustmentGroup("Audio.VolumeUp", "Volume Up"),
            AdjustmentGroup("Audio.VolumeDown", "Volume Down"),
            AdjustmentGroup("Audio.MuteToggle", "Mute Toggle"),
        ];

        IReadOnlyList<MenuNode> roots =
        [
            new MenuNode { Name = "Audio", CommandName = string.Empty, Children = rootChildren },
        ];

        return Task.FromResult(roots);
    }

    private MenuNode DeviceGroup(string label, IReadOnlyList<AudioEndpointInfo> devices, string commandName)
    {
        var leaves = devices.Select(ep => new MenuNode
        {
            Name = _aliasStore!.Resolve(ep),
            CommandName = commandName,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AudioDeviceParameter.DeviceIdName] = ep.Id,
            },
        }).ToList();

        return new MenuNode
        {
            Name = label,
            CommandName = string.Empty,
            Children = leaves,
        };
    }

    // ---- IPluginSettingsPage ----

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema => BuildSchema();

    public IReadOnlyList<PluginSettingAction> SettingsActions { get; } = [];

    public void OnSettingsSaved()
    {
        _aliasStore?.CleanupEmpty();
        _aliasStore?.Reload();
    }

    private IReadOnlyList<PluginSettingDescriptor> BuildSchema()
    {
        if (!_audio.IsSupported || _aliasStore == null) return [];

        var outputs = _audio.GetEndpoints(AudioEndpointKind.Render);
        var inputs = _audio.GetEndpoints(AudioEndpointKind.Capture);

        var list = new List<PluginSettingDescriptor>
        {
            new()
            {
                Key = "__heading_outputs",
                Label = "Output Devices",
                Kind = PluginSettingKind.Heading,
                Description = "Set a short alias to replace the OS device name.",
                DefaultValue = string.Empty
            }
        };
        foreach (var ep in outputs)
            list.Add(AliasField(ep));

        list.Add(new PluginSettingDescriptor
        {
            Key = "__heading_inputs",
            Label = "Input Devices",
            Kind = PluginSettingKind.Heading,
            DefaultValue = string.Empty
        });
        foreach (var ep in inputs)
            list.Add(AliasField(ep));

        var presentIds = new HashSet<string>(
            outputs.Select(e => e.Id).Concat(inputs.Select(e => e.Id)),
            StringComparer.Ordinal);

        var offlineIds = _aliasStore.KnownAliasedIds
            .Where(id => !presentIds.Contains(id))
            .ToList();

        if (offlineIds.Count > 0)
        {
            list.Add(new PluginSettingDescriptor
            {
                Key = "__heading_offline",
                Label = "Saved aliases (not connected)",
                Kind = PluginSettingKind.Heading,
                Description = "Clear the field to forget the alias.",
                DefaultValue = string.Empty
            });
            foreach (var id in offlineIds)
            {
                list.Add(new PluginSettingDescriptor
                {
                    Key = AudioAliasStore.KeyPrefix + id,
                    Label = $"{id} (not connected)",
                    Kind = PluginSettingKind.Text,
                    DefaultValue = string.Empty
                });
            }
        }

        return list;
    }

    private static PluginSettingDescriptor AliasField(AudioEndpointInfo ep) => new()
    {
        Key = AudioAliasStore.KeyPrefix + ep.Id,
        Label = ep.FriendlyName,
        Kind = PluginSettingKind.Text,
        DefaultValue = string.Empty
    };

    private static IAudioService CreateAudioService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsAudioService();
        if (OperatingSystem.IsLinux()) return new LinuxAudioService();
        return new UnsupportedAudioService();
    }
}

internal sealed class UnsupportedAudioService : IAudioService
{
    public bool IsSupported => false;
    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind) => [];
    public float GetVolume(string endpointId) => 0f;
    public void SetVolume(string endpointId, float scalar01) { }
    public bool GetMute(string endpointId) => false;
    public void SetMute(string endpointId, bool muted) { }
    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
        => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
