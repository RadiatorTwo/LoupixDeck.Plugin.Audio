using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>The plugin's settings page (<see cref="IPluginSettingsPage"/>).</summary>
public sealed partial class AudioPlugin
{
    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema => BuildSchema();

    public IReadOnlyList<PluginSettingAction> SettingsActions { get; } = [];

    public void OnSettingsSaved()
    {
        _aliasStore?.CleanupEmpty();
        _aliasStore?.Reload();
        // Collapse the per-device playback toggles back into a single selection.
        if (_audio.IsSupported)
            _playbackDevices?.Normalize(Endpoints(AudioEndpointKind.Render));
        // The layout toggle may have flipped — repaint any live volume strips.
        _stripProvider?.NotifyLayoutChanged();
    }

    private IReadOnlyList<PluginSettingDescriptor> BuildSchema()
    {
        if (!_audio.IsSupported || _aliasStore == null) return [];

        var outputs = Endpoints(AudioEndpointKind.Render);
        var inputs = Endpoints(AudioEndpointKind.Capture);

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

    private static PluginSettingDescriptor AliasField(AudioEndpointInfo ep) => new()
    {
        Key = AudioAliasStore.KeyPrefix + ep.Id,
        Label = ep.FriendlyName,
        Kind = PluginSettingKind.Text,
        DefaultValue = string.Empty
    };
}
