using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>The command menu tree (<see cref="IMenuContributor"/>).</summary>
public sealed partial class AudioPlugin
{
    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (!_audio.IsSupported || _aliasStore == null)
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        var outputs = Endpoints(AudioEndpointKind.Render);
        var inputs = Endpoints(AudioEndpointKind.Capture);

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

        IReadOnlyList<AudioSessionInfo> sessions;
        try
        {
            sessions = _audio.GetSessions(null);
        }
        catch (Exception ex)
        {
            // Without the running apps the menu still offers the foreground entry.
            _logger?.Warn($"Could not list audio sessions: {ex.Message}");
            sessions = [];
        }

        foreach (AudioSessionInfo session in sessions)
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
}
