using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Entry point of the Audio plugin. Contributes commands that open a folder
/// for picking an output/input device and adjusting its volume and mute state.
/// Backed by WASAPI on Windows and pactl (PulseAudio / pipewire-pulse) on Linux.
/// </summary>
public sealed partial class AudioPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor, IPluginRequirements
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
    private IDisposable? _deviceChanges;
    private readonly AppIdentityCache _appIdentity = new();

    internal static readonly TimeSpan VolumeOverlayDuration = TimeSpan.FromMilliseconds(1500);

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "audio",
        Name = "Audio",
        Version = new Version(1, 15, 0),
        SdkVersion = SdkInfo.Version,
        Author = "RadiatorTwo",
        Description = "Pick the active audio output/input device and adjust volume and mute from the device.",
        Icon = LoadIcon()
    };

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(AudioPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.Audio.icon.png");
        if (stream == null) return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public override void Initialize(IPluginHost host)
    {
        // Kept before the early return: GetRequirements logs through it even when nothing else is set up.
        _logger = host.Logger;

        if (!_audio.IsSupported)
        {
            _logger.Warn("No supported audio backend was found, so the Audio plugin registers no commands. "
                         + "See the plugin requirements.");
            return;
        }

        // The Windows backend logs its own COM failures, so it needs the host logger.
        if (_audio is WindowsAudioService windows) windows.Logger = host.Logger;

        _host = host;
        AudioButtonLayouts.Translate = host.Tr;
        _settings = host.Settings;
        _aliasStore = new AudioAliasStore(host.Settings);
        _soundLibrary = new SoundLibrary(host.Settings);
        _playbackDevices = new PlaybackDeviceStore(host.Settings);
        _visibility = new AudioVisibilityStore(host.Settings);

        _commands =
        [
            new AudioOutputFolderCommand(_audio, _aliasStore, _visibility),
            new AudioCurrentOutputCommand(_audio, _aliasStore, _visibility),
            new AudioCycleOutputCommand(_audio, _aliasStore, _visibility),
            new AudioInputFolderCommand(_audio, _aliasStore, _visibility),
            new AudioVolumeCommand(_audio),
            new AudioAppVolumeCommand(_audio, _appIdentity),
            new AudioVolumeUpCommand(_audio),
            new AudioVolumeDownCommand(_audio),
            new AudioMuteToggleCommand(_audio),
            new AudioMuteCommand(_audio),
            new AudioUnmuteCommand(_audio),
            new AudioSetMuteCommand(_audio),
            new AudioPlaySoundCommand(_audio, _soundLibrary, _playbackDevices, host),
            new AudioStopSoundCommand(_audio, host),
            new AudioSetVolumeCommand(_audio),
            new AudioSetDefaultDeviceCommand(_audio),
            new AudioAppVolumeUpCommand(_audio, _appIdentity),
            new AudioAppVolumeDownCommand(_audio, _appIdentity),
            new AudioAppMuteToggleCommand(_audio, _appIdentity),
            new AudioAppMuteCommand(_audio, _appIdentity),
            new AudioAppUnmuteCommand(_audio, _appIdentity),
            new AudioAppSetMuteCommand(_audio, _appIdentity),
            new AudioAppSetVolumeCommand(_audio, _appIdentity),
            new AudioMixerFolderCommand(_audio, _appIdentity),
            new AudioVolumeTileCommand(_audio, _aliasStore),
            new AudioAppVolumeTileCommand(_audio, _appIdentity),
        ];

        _stripProvider = new AudioVolumeStripProvider(_audio, host.Settings, _aliasStore, host);
        _stripProviders = [_stripProvider];

        _deviceChanges = _audio.SubscribeDeviceChanges(OnDeviceChanged);
    }

    /// <summary>Commands whose buttons or dials show something that depends on the default device.</summary>
    private static readonly string[] DefaultDeviceCommands =
        ["Audio.CurrentOutput", "Audio.CycleOutput", "Audio.Volume", "Audio.VolumeTile", "Audio.MuteToggle"];

    /// <summary>
    /// A device came or went, or the default moved. Drops what the commands remember about the
    /// default and repaints everything that shows it, instead of leaving it to their next poll.
    /// </summary>
    private void OnDeviceChanged()
    {
        AudioDeviceParameter.InvalidateDefaultEndpoint();
        AudioCurrentOutputCommand.InvalidateLabel();
        RefreshDeviceButtons();
    }

    private void RefreshDeviceButtons()
    {
        if (_host == null) return;

        foreach (string command in DefaultDeviceCommands)
        {
            try { _host.RequestButtonRefresh(command); }
            catch (Exception ex) { _logger?.Warn($"Audio: could not refresh {command}: {ex.Message}"); }
        }
        // The strip bars follow the default device on render.
        _stripProvider?.NotifyLayoutChanged();
    }

    public override void Shutdown()
    {
        // Sounds are fire-and-forget, so an unloaded plugin could otherwise leave
        // a WASAPI stream or a paplay process behind.
        _audio.StopAllPlayback();
        // Before the backend goes away, so no change is delivered to a plugin that is shutting down.
        _deviceChanges?.Dispose();
        _deviceChanges = null;
        // The Windows backend holds a cached session device that COM only releases on demand;
        // the Linux backend runs a pactl event monitor process.
        if (_audio is IDisposable disposable) disposable.Dispose();
        base.Shutdown();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    // Requirements that were already logged as unmet, so the warning appears once per problem
    // instead of on every check the host makes.
    private readonly HashSet<string> _warnedRequirements = [];

    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        IReadOnlyList<PluginRequirement> requirements = _audio.GetRequirements();

        lock (_warnedRequirements)
        {
            foreach (PluginRequirement requirement in requirements)
            {
                if (requirement.IsMet)
                {
                    _warnedRequirements.Remove(requirement.Id);
                    continue;
                }

                if (_warnedRequirements.Add(requirement.Id))
                    _logger?.Warn($"Requirement '{requirement.Id}' is not met: {requirement.Message} {requirement.InstallHint}");
            }
        }

        return requirements;
    }

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
    /// The endpoints of one kind, or none when the backend cannot enumerate them. The dial presets,
    /// the command menu and the settings page all build from this list on whatever thread asked,
    /// and a failure there must cost that one list, not the plugin.
    /// </summary>
    private IReadOnlyList<AudioEndpointInfo> Endpoints(AudioEndpointKind kind)
    {
        try
        {
            return _audio.IsSupported ? _audio.GetEndpoints(kind) : [];
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Could not list {kind} endpoints: {ex.Message}");
            return [];
        }
    }

    /// <summary>The device's name as the rest of the plugin shows it — the user's alias when they
    /// set one.</summary>
    private string Name(AudioEndpointInfo ep) => _aliasStore?.Resolve(ep) ?? ep.FriendlyName;

    public override IEnumerable<ISideStripProvider> GetSideStripProviders() => _stripProviders;

    // ---- IMenuContributor ----

    // ---- IPluginSettingsPage ----

    /// <summary>Text this plugin composes itself, which the host cannot translate from a
    /// descriptor. Falls back to the English wording before Initialize has run.</summary>
    private string Tr(string english) => _host?.Tr(english) ?? english;

    private static IAudioService CreateAudioService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsAudioService();
        if (OperatingSystem.IsLinux()) return new LinuxAudioService();
        return new UnsupportedAudioService();
    }
}
