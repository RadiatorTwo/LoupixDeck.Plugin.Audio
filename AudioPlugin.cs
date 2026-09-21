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
    private AudioVisibilityStore? _visibility;
    private IPluginSettings? _settings;
    private IPluginLogger? _logger;
    private IPluginHost? _host;

    internal static readonly TimeSpan VolumeOverlayDuration = TimeSpan.FromMilliseconds(1500);

    // Material Design Icons code points used by the contributed dial presets.
    private const string VolumeGlyph = "\U000F057E";      // mdi-volume-high
    private const string MicrophoneGlyph = "\U000F036C";  // mdi-microphone
    private const string ApplicationGlyph = "\U000F0614";  // mdi-application

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "audio",
        Name = "Audio",
        Version = new Version(1, 14, 1),
        SdkVersion = SdkInfo.Version,
        Author = "RadiatorTwo",
        Description = "Pick the active audio output/input device and adjust volume and mute from the device."
    };

    public override void Initialize(IPluginHost host)
    {
        if (!_audio.IsSupported) return;

        // The Windows backend logs its own COM failures, so it needs the host logger.
        if (_audio is WindowsAudioService windows) windows.Logger = host.Logger;

        _host = host;
        _logger = host.Logger;
        _settings = host.Settings;
        _aliasStore = new AudioAliasStore(host.Settings);
        _soundLibrary = new SoundLibrary(host.Settings);
        _playbackDevices = new PlaybackDeviceStore(host.Settings);
        _visibility = new AudioVisibilityStore(host.Settings);

        _commands =
        [
            new AudioOutputFolderCommand(_audio, _aliasStore, _visibility),
            new AudioCurrentOutputCommand(_audio, _aliasStore, _visibility),
            new AudioInputFolderCommand(_audio, _aliasStore, _visibility),
            new AudioVolumeCommand(_audio),
            new AudioAppVolumeCommand(_audio),
            new AudioVolumeUpCommand(_audio),
            new AudioVolumeDownCommand(_audio),
            new AudioMuteToggleCommand(_audio),
            new AudioPlaySoundCommand(_audio, _soundLibrary, _playbackDevices, host),
            new AudioStopSoundCommand(_audio, host),
            new AudioSetVolumeCommand(_audio),
            new AudioSetDefaultDeviceCommand(_audio),
            new AudioAppVolumeUpCommand(_audio),
            new AudioAppVolumeDownCommand(_audio),
            new AudioAppMuteToggleCommand(_audio),
            new AudioAppSetVolumeCommand(_audio),
            new AudioMixerFolderCommand(_audio),
        ];

        _stripProvider = new AudioVolumeStripProvider(_audio, host.Settings, _aliasStore, host);
        _stripProviders = [_stripProvider];
    }

    public override void Shutdown()
    {
        // Sounds are fire-and-forget, so an unloaded plugin could otherwise leave
        // a WASAPI stream or a paplay process behind.
        _audio.StopAllPlayback();
        // The Windows backend holds a cached session device that COM only releases on demand.
        if (_audio is IDisposable disposable) disposable.Dispose();
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

    /// <summary>
    /// Moves dials the user configured before <c>Audio.Volume</c> existed onto it. Without this
    /// they would sit on the old triad forever: it still works, but a dial there runs one
    /// command per detent and cannot report its level, and nothing would ever retire the old
    /// shape. The host runs each rule at most once per config and only on a dial that carries
    /// exactly the old binding, so a dial the user has since composed differently is safe.
    /// </summary>
    public override IEnumerable<CommandMigration> GetCommandMigrations()
    {
        yield return new CommandMigration
        {
            Id = "volume-dial",
            From = new Dictionary<RotaryAction, string>
            {
                [RotaryAction.CounterClockwise] = "Audio.VolumeDown",
                [RotaryAction.Clockwise] = "Audio.VolumeUp",
                [RotaryAction.Press] = "Audio.MuteToggle",
            },
            To = "Audio.Volume",
            // The endpoint and the user's step size carry over; the mute command never had a
            // step, so the value comes from whichever turn slot declared one.
            Parameters = new Dictionary<string, string>
            {
                [AudioDeviceParameter.DeviceIdName] = $"{{{AudioDeviceParameter.DeviceIdName}}}",
                [AudioDeviceParameter.StepName] = $"{{{AudioDeviceParameter.StepName}}}",
            },
        };

        yield return new CommandMigration
        {
            Id = "app-volume-dial",
            From = new Dictionary<RotaryAction, string>
            {
                [RotaryAction.CounterClockwise] = "Audio.AppVolumeDown",
                [RotaryAction.Clockwise] = "Audio.AppVolumeUp",
                [RotaryAction.Press] = "Audio.AppMuteToggle",
            },
            To = "Audio.AppVolume",
            Parameters = new Dictionary<string, string>
            {
                [AudioAppParameter.AppIdName] = $"{{{AudioAppParameter.AppIdName}}}",
                [AudioAppParameter.StepName] = $"{{{AudioAppParameter.StepName}}}",
            },
        };
    }

    /// <summary>
    /// The dial presets this plugin offers. Rebuilt on every call, so the device presets follow
    /// the endpoints that actually exist right now — plug in a headset and its preset is there.
    /// </summary>
    /// <remarks>
    /// The master preset binds the default-device sentinel rather than an endpoint id, so it keeps
    /// meaning "the speakers I am listening on" after the user switches their default output.
    /// The per-device presets bind the endpoint id, which is the point of having them.
    /// </remarks>
    public override IEnumerable<DialPresetDescriptor> GetDialPresets()
    {
        yield return DevicePreset(
            "master-volume", "Master volume", VolumeGlyph, AudioDeviceParameter.DefaultDeviceId);

        foreach (AudioEndpointInfo ep in Endpoints(AudioEndpointKind.Render))
            yield return DevicePreset($"output-{ep.Id}", Name(ep), VolumeGlyph, ep.Id);

        foreach (AudioEndpointInfo ep in Endpoints(AudioEndpointKind.Capture))
            yield return DevicePreset($"input-{ep.Id}", Name(ep), MicrophoneGlyph, ep.Id);

        // Whatever is playing in front, rather than a fixed application: the one preset that is
        // worth having without knowing which app the user will be in.
        Dictionary<string, string> foreground = new(StringComparer.Ordinal)
        {
            [AudioAppParameter.AppIdName] = AudioAppParameter.ForegroundAppId,
        };

        yield return new DialPresetDescriptor
        {
            Id = "foreground-app-volume",
            Name = "Foreground app volume",
            Glyph = ApplicationGlyph,
            Actions = new Dictionary<RotaryAction, MenuCommandRef>
            {
                [RotaryAction.CounterClockwise] = new()
                {
                    CommandName = "Audio.AppVolume",
                    Parameters = foreground,
                },
                [RotaryAction.Clockwise] = new()
                {
                    CommandName = "Audio.AppVolume",
                    Parameters = foreground,
                },
                [RotaryAction.Press] = new()
                {
                    CommandName = "Audio.AppVolume",
                    Parameters = foreground,
                },
            },
        };
    }

    /// <summary>Turn to change this endpoint's volume, press to mute it.</summary>
    private static DialPresetDescriptor DevicePreset(string id, string name, string glyph,
        string endpointId)
    {
        Dictionary<string, string> device = new(StringComparer.Ordinal)
        {
            [AudioDeviceParameter.DeviceIdName] = endpointId,
        };

        return new DialPresetDescriptor
        {
            // Derived from the endpoint id, so a preset keeps its identity across restarts and
            // across a device coming and going.
            Id = id,
            Name = name,
            Glyph = glyph,
            Actions = new Dictionary<RotaryAction, MenuCommandRef>
            {
                [RotaryAction.CounterClockwise] = new()
                {
                    CommandName = "Audio.Volume",
                    Parameters = device,
                },
                [RotaryAction.Clockwise] = new()
                {
                    CommandName = "Audio.Volume",
                    Parameters = device,
                },
                [RotaryAction.Press] = new()
                {
                    CommandName = "Audio.Volume",
                    Parameters = device,
                },
            },
        };
    }

    /// <summary>
    /// The endpoints of one kind, or none when the backend cannot enumerate them. Preset building
    /// runs on whatever thread opened a preset surface, and a failure there must cost the presets,
    /// not the plugin.
    /// </summary>
    private IReadOnlyList<AudioEndpointInfo> Endpoints(AudioEndpointKind kind)
    {
        try
        {
            return _audio.IsSupported ? _audio.GetEndpoints(kind) : [];
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Could not list {kind} endpoints for the dial presets: {ex.Message}");
            return [];
        }
    }

    /// <summary>The device's name as the rest of the plugin shows it — the user's alias when they
    /// set one.</summary>
    private string Name(AudioEndpointInfo ep) => _aliasStore?.Resolve(ep) ?? ep.FriendlyName;

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
            new MenuNode { Name = "Current Output Device", CommandName = "Audio.CurrentOutput" },
            new MenuNode { Name = "Select Input Device", CommandName = "Audio.InputDevices" },
            new MenuNode { Name = "Mixer", CommandName = "Audio.Mixer" },
        ];

        if (outputs.Count > 0)
            rootChildren.Add(DevicesCategory("Output Devices", outputs, includeGroup));
        if (inputs.Count > 0)
            rootChildren.Add(DevicesCategory("Input Devices", inputs, includeGroup));

        rootChildren.Add(ApplicationsCategory(includeGroup));
        rootChildren.Add(SoundsCategory());
        // A sibling of the sound tree rather than a child of it: stopping must stay
        // reachable even when no sound folder is configured.
        rootChildren.Add(new MenuNode { Name = "Stop Sounds", CommandName = "Audio.StopSounds" });

        IReadOnlyList<MenuNode> roots =
        [
            new MenuNode { Name = "Audio", CommandName = string.Empty, Children = rootChildren },
        ];

        return Task.FromResult(roots);
    }

    /// <summary>
    /// "Applications" category. The listed apps are the ones playing audio right now — a
    /// binding stores the process name, so it keeps working after that app restarts. The
    /// foreground entry is always offered because it needs no running session to be useful.
    /// </summary>
    private MenuNode ApplicationsCategory(bool includeGroup)
    {
        List<MenuNode> children = [AppNode("Foreground App", AudioAppParameter.ForegroundAppId, includeGroup)];

        foreach (AudioSessionInfo session in _audio.GetSessions(null))
            children.Add(AppNode(session.DisplayName, session.AppId, includeGroup));

        return new MenuNode { Name = "Applications", CommandName = string.Empty, Children = children };
    }

    private MenuNode AppNode(string label, string appId, bool includeGroup)
    {
        Dictionary<string, string> AppParam() => new(StringComparer.Ordinal)
        {
            [AudioAppParameter.AppIdName] = appId,
        };

        List<MenuNode> children = [];

        if (includeGroup)
        {
            children.Add(new MenuNode
            {
                Name = "Volume Control",
                // One adjustment command on all three gestures: the tick delta carries the
                // direction, the press resets (mutes), and the dial can report its level.
                RotaryGroup = new Dictionary<RotaryAction, MenuCommandRef>
                {
                    [RotaryAction.CounterClockwise] = new() { CommandName = "Audio.AppVolume", Parameters = AppParam() },
                    [RotaryAction.Clockwise] = new() { CommandName = "Audio.AppVolume", Parameters = AppParam() },
                    [RotaryAction.Press] = new() { CommandName = "Audio.AppVolume", Parameters = AppParam() },
                },
            });
        }

        children.Add(new MenuNode { Name = "Volume Down", CommandName = "Audio.AppVolumeDown", Parameters = AppParam() });
        children.Add(new MenuNode { Name = "Volume Up", CommandName = "Audio.AppVolumeUp", Parameters = AppParam() });
        children.Add(new MenuNode { Name = "Mute", CommandName = "Audio.AppMuteToggle", Parameters = AppParam() });
        children.Add(new MenuNode { Name = "Set Volume", CommandName = "Audio.AppSetVolume", Parameters = AppParam() });

        return new MenuNode { Name = label, CommandName = string.Empty, Children = children };
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
            return Tr("Set a sound folder in the Audio plugin settings");

        // The folder path is a value, so only the fixed part is a key.
        return _soundLibrary?.FolderPath == null
            ? string.Format(Tr("Sound folder not found: {0}"), configured)
            : Tr("No supported audio files in the sound folder");
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
                // One adjustment command on all three gestures: the tick delta carries the
                // direction, the press mutes, and the dial can report its level.
                RotaryGroup = new Dictionary<RotaryAction, MenuCommandRef>
                {
                    [RotaryAction.CounterClockwise] = new() { CommandName = "Audio.Volume", Parameters = DeviceParam() },
                    [RotaryAction.Clockwise] = new() { CommandName = "Audio.Volume", Parameters = DeviceParam() },
                    [RotaryAction.Press] = new() { CommandName = "Audio.Volume", Parameters = DeviceParam() },
                },
            });
        }

        children.Add(new MenuNode { Name = "Volume Down", CommandName = "Audio.VolumeDown", Parameters = DeviceParam() });
        children.Add(new MenuNode { Name = "Volume Up", CommandName = "Audio.VolumeUp", Parameters = DeviceParam() });
        children.Add(new MenuNode { Name = "Mute", CommandName = "Audio.MuteToggle", Parameters = DeviceParam() });
        children.Add(new MenuNode { Name = "Set Volume", CommandName = "Audio.SetVolume", Parameters = DeviceParam() });
        children.Add(new MenuNode { Name = "Set as Default", CommandName = "Audio.SetDefaultDevice", Parameters = DeviceParam() });

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
                    // The device name is a value, so only the fixed part is a key.
                    Label = string.Format(Tr("{0} (not connected)"), id),
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
            Key = SoundLibrary.StopOnSecondPressKey,
            Label = "Stop on second press",
            Kind = PluginSettingKind.Toggle,
            Description = "Pressing the button again stops that sound instead of starting it "
                          + "a second time. Off: presses overlap.",
            DefaultValue = false
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
                Label = string.Format(Tr("{0} (not connected)"), _aliasStore.Resolve(selectedId, selectedId)),
                Kind = PluginSettingKind.Toggle,
                Description = "Switch off to fall back to the system default device.",
                DefaultValue = true
            });
        }

        list.Add(new PluginSettingDescriptor
        {
            Key = "__heading_visibility",
            Label = "Visible Devices",
            Kind = PluginSettingKind.Heading,
            Description = "Hide a device from the touch-screen picker folders. Commands bound "
                          + "to a hidden device keep working.",
            DefaultValue = string.Empty
        });
        foreach (var ep in outputs.Concat(inputs))
        {
            list.Add(new PluginSettingDescriptor
            {
                Key = AudioVisibilityStore.TogglePrefix + ep.Id,
                // The device name is a value, so only the fixed part is a key.
                Label = string.Format(Tr("Hide {0}"), _aliasStore.Resolve(ep)),
                Kind = PluginSettingKind.Toggle,
                Description = "On: hidden from the device picker.",
                DefaultValue = false
            });
        }

        return list;
    }

    /// <summary>Text this plugin composes itself, which the host cannot translate from a
    /// descriptor. Falls back to the English wording before Initialize has run.</summary>
    private string Tr(string english) => _host?.Tr(english) ?? english;

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
    public string? GetDefaultEndpointId(AudioEndpointKind kind) => null;
    public float GetVolume(string endpointId) => 0f;
    public void SetVolume(string endpointId, float scalar01) { }
    public bool GetMute(string endpointId) => false;
    public void SetMute(string endpointId, bool muted) { }
    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
        => NoopDisposable.Instance;
    public void PlayFile(string filePath, string? endpointId) { }
    public void StopAllPlayback() { }
    public bool StopFile(string filePath) => false;
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
