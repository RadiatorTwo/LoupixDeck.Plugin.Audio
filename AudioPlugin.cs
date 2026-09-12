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
    private List<ISideStripProvider> _stripProviders = [];
    private AudioVolumeStripProvider? _stripProvider;
    private AudioAliasStore? _aliasStore;
    private SoundLibrary? _soundLibrary;
    private PlaybackDeviceStore? _playbackDevices;
    private IPluginSettings? _settings;

    internal static readonly TimeSpan VolumeOverlayDuration = TimeSpan.FromMilliseconds(1500);

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "audio",
        Name = "Audio",
        Version = new Version(1, 7, 0),
        SdkVersion = new Version(1, 16, 0),
        Author = "RadiatorTwo",
        Description = "Pick the active audio output/input device and adjust volume and mute from the device."
    };

    public override void Initialize(IPluginHost host)
    {
        if (!_audio.IsSupported) return;

        _settings = host.Settings;
        _aliasStore = new AudioAliasStore(host.Settings);
        _soundLibrary = new SoundLibrary(host.Settings);
        _playbackDevices = new PlaybackDeviceStore(host.Settings);

        _commands =
        [
            new AudioOutputFolderCommand(_audio, _aliasStore),
            new AudioInputFolderCommand(_audio, _aliasStore),
            new AudioVolumeUpCommand(_audio),
            new AudioVolumeDownCommand(_audio),
            new AudioMuteToggleCommand(_audio),
            new AudioPlaySoundCommand(_audio, _soundLibrary, _playbackDevices, host),
        ];

        _stripProvider = new AudioVolumeStripProvider(_audio, host.Settings, _aliasStore);
        _stripProviders = [_stripProvider];
    }

    public override void Shutdown()
    {
        // Sounds are fire-and-forget, so an unloaded plugin could otherwise leave
        // a WASAPI stream or a paplay process behind.
        _audio.StopAllPlayback();
        base.Shutdown();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = "Audio",
            Description = "Volume, mute and device control",
            Icon = "\U000F057E",
            Section = CommandGroupSection.Plugins
        }
    ];

    public override IEnumerable<ISideStripProvider> GetSideStripProviders() => _stripProviders;

    // ---- IMenuContributor ----

    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (!_audio.IsSupported || _aliasStore == null)
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        var outputs = _audio.GetEndpoints(AudioEndpointKind.Render);
        var inputs = _audio.GetEndpoints(AudioEndpointKind.Capture);

        // The "Volume Control" rotary group only makes sense on a rotary encoder;
        // it never appears for simple/touch buttons.
        bool includeGroup = target.HasFlag(ButtonTargets.RotaryEncoder);

        List<MenuNode> rootChildren =
        [
            new MenuNode { Name = "Select Output Device", CommandName = "Audio.OutputDevices" },
            new MenuNode { Name = "Select Input Device", CommandName = "Audio.InputDevices" },
        ];

        if (outputs.Count > 0)
            rootChildren.Add(DevicesCategory("Output Devices", outputs, includeGroup));
        if (inputs.Count > 0)
            rootChildren.Add(DevicesCategory("Input Devices", inputs, includeGroup));

        rootChildren.Add(SoundsCategory());

        IReadOnlyList<MenuNode> roots =
        [
            new MenuNode { Name = "Audio", CommandName = string.Empty, Children = rootChildren },
        ];

        return Task.FromResult(roots);
    }

    /// <summary>
    /// "Play Sound" category listing the files of the configured sound folder, with
    /// sub-folders as nested menus. When there is nothing to list, the category still
    /// shows a hint saying why — otherwise the command would silently be missing and the
    /// sound folder setting would be impossible to discover.
    /// </summary>
    private MenuNode SoundsCategory()
    {
        IReadOnlyList<SoundFile> sounds = _soundLibrary?.Enumerate() ?? [];
        if (sounds.Count == 0)
            return new MenuNode
            {
                Name = "Play Sound",
                CommandName = string.Empty,
                // A node with no command is ignored when assigned, so this is inert.
                Children = [new MenuNode { Name = EmptySoundsHint(), CommandName = string.Empty }],
            };

        // MenuNode.Children is immutable, so the tree is assembled in a mutable
        // shadow structure and converted in one go.
        SoundFolder root = new();

        foreach (SoundFile sound in sounds)
        {
            string[] segments = sound.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            SoundFolder parent = root;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (!parent.Folders.TryGetValue(segments[i], out SoundFolder? child))
                {
                    child = new SoundFolder();
                    parent.Folders[segments[i]] = child;
                }
                parent = child;
            }

            parent.Sounds.Add(sound);
        }

        return root.ToMenuNode("Play Sound");
    }

    /// <summary>Explains why the "Play Sound" category has nothing to offer.</summary>
    private string EmptySoundsHint()
    {
        string? configured = _soundLibrary?.ConfiguredFolder;
        if (configured == null)
            return "Set a sound folder in the Audio plugin settings";

        return _soundLibrary?.FolderPath == null
            ? $"Sound folder not found: {configured}"
            : "No supported audio files in the sound folder";
    }

    /// <summary>Mutable builder for the nested "Play Sound" menu.</summary>
    private sealed class SoundFolder
    {
        public SortedDictionary<string, SoundFolder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SoundFile> Sounds { get; } = [];

        public MenuNode ToMenuNode(string name) => new()
        {
            Name = name,
            CommandName = string.Empty,
            // Sub-folders first, then the playable files.
            Children =
            [
                .. Folders.Select(folder => folder.Value.ToMenuNode(folder.Key)),
                .. Sounds.Select(sound => new MenuNode
                {
                    Name = sound.DisplayName,
                    CommandName = "Audio.PlaySound",
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AudioPlaySoundCommand.SoundParameterName] = SoundLibrary.Encode(sound.RelativePath),
                    },
                }),
            ],
        };
    }

    private MenuNode DevicesCategory(string label, IReadOnlyList<AudioEndpointInfo> devices, bool includeGroup)
    {
        var deviceNodes = devices.Select(ep => DeviceNode(ep, includeGroup)).ToList();
        return new MenuNode { Name = label, CommandName = string.Empty, Children = deviceNodes };
    }

    /// <summary>
    /// One folder per device. Inside it: the "Volume Control" rotary group (rotary
    /// target only) followed by the individual Volume Down / Volume Up / Mute
    /// commands, all bound to this device.
    /// </summary>
    private MenuNode DeviceNode(AudioEndpointInfo ep, bool includeGroup)
    {
        Dictionary<string, string> DeviceParam() => new(StringComparer.Ordinal)
        {
            [AudioDeviceParameter.DeviceIdName] = ep.Id,
        };

        List<MenuNode> children = [];

        if (includeGroup)
        {
            children.Add(new MenuNode
            {
                Name = "Volume Control",
                RotaryGroup = new Dictionary<RotaryAction, MenuCommandRef>
                {
                    // Counter-clockwise lowers, clockwise raises, press mutes.
                    [RotaryAction.CounterClockwise] = new() { CommandName = "Audio.VolumeDown", Parameters = DeviceParam() },
                    [RotaryAction.Clockwise] = new() { CommandName = "Audio.VolumeUp", Parameters = DeviceParam() },
                    [RotaryAction.Press] = new() { CommandName = "Audio.MuteToggle", Parameters = DeviceParam() },
                },
            });
        }

        children.Add(new MenuNode { Name = "Volume Down", CommandName = "Audio.VolumeDown", Parameters = DeviceParam() });
        children.Add(new MenuNode { Name = "Volume Up", CommandName = "Audio.VolumeUp", Parameters = DeviceParam() });
        children.Add(new MenuNode { Name = "Mute", CommandName = "Audio.MuteToggle", Parameters = DeviceParam() });

        return new MenuNode
        {
            Name = _aliasStore!.Resolve(ep),
            CommandName = string.Empty,
            Children = children,
        };
    }

    // ---- IPluginSettingsPage ----

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema => BuildSchema();

    public IReadOnlyList<PluginSettingAction> SettingsActions { get; } = [];

    public void OnSettingsSaved()
    {
        _aliasStore?.CleanupEmpty();
        _aliasStore?.Reload();
        // Collapse the per-device playback toggles back into a single selection.
        if (_audio.IsSupported)
            _playbackDevices?.Normalize(_audio.GetEndpoints(AudioEndpointKind.Render));
        // The layout toggle may have flipped — repaint any live volume strips.
        _stripProvider?.NotifyLayoutChanged();
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
                Key = "__heading_strip",
                Label = "Side-Strip Volume Bars",
                Kind = PluginSettingKind.Heading,
                Description = "How the volume bars are arranged on the side strip.",
                DefaultValue = string.Empty
            },
            new()
            {
                Key = AudioVolumeStripProvider.HorizontalLayoutKey,
                Label = "Horizontal segments",
                Kind = PluginSettingKind.Toggle,
                Description = "Off: side-by-side vertical bars. On: 3 stacked horizontal segments.",
                DefaultValue = false
            },
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

        list.Add(new PluginSettingDescriptor
        {
            Key = "__heading_sounds",
            Label = "Sound Playback",
            Kind = PluginSettingKind.Heading,
            Description = "Folder holding the audio files. After saving they show up in the "
                          + "command menu under Audio → Play Sound.",
            DefaultValue = string.Empty
        });
        list.Add(new PluginSettingDescriptor
        {
            Key = SoundLibrary.FolderKey,
            Label = "Sound folder",
            Kind = PluginSettingKind.Text,
            Description = "Full path to a folder with .wav, .mp3, .flac, .m4a or .aiff files "
                          + "(.ogg on Linux only). Sub-folders become sub-menus.",
            DefaultValue = string.Empty
        });

        list.Add(new PluginSettingDescriptor
        {
            Key = "__heading_playback_device",
            Label = "Playback Device",
            Kind = PluginSettingKind.Heading,
            Description = "Device the sounds play on. Enable exactly one — with none enabled "
                          + "the system default device is used.",
            DefaultValue = string.Empty
        });
        foreach (var ep in outputs)
        {
            list.Add(new PluginSettingDescriptor
            {
                Key = PlaybackDeviceStore.TogglePrefix + ep.Id,
                Label = _aliasStore.Resolve(ep),
                Kind = PluginSettingKind.Toggle,
                DefaultValue = false
            });
        }

        // A selected device that is currently unplugged keeps its selection, so it needs a
        // toggle of its own — otherwise the choice could never be cleared again.
        var selectedId = _playbackDevices?.SelectedId;
        if (selectedId != null && outputs.All(e => e.Id != selectedId))
        {
            list.Add(new PluginSettingDescriptor
            {
                Key = PlaybackDeviceStore.TogglePrefix + selectedId,
                Label = $"{_aliasStore.Resolve(selectedId, selectedId)} (not connected)",
                Kind = PluginSettingKind.Toggle,
                Description = "Switch off to fall back to the system default device.",
                DefaultValue = true
            });
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
    public void PlayFile(string filePath, string? endpointId) { }
    public void StopAllPlayback() { }
    public IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId) => [];
    public float? GetSessionVolume(string? endpointId, string appId) => null;
    public void SetSessionVolume(string? endpointId, string appId, float scalar01) { }
    public bool? GetSessionMute(string? endpointId, string appId) => null;
    public void SetSessionMute(string? endpointId, string appId, bool muted) { }
    public string? GetForegroundAppId() => null;
    public bool SetDefaultEndpoint(string endpointId) => false;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
