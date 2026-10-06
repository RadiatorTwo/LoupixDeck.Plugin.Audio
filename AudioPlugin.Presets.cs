using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>The dial presets this plugin contributes.</summary>
public sealed partial class AudioPlugin
{
    // Material Design Icons code points used by the contributed dial presets.
    private const string VolumeGlyph = "\U000F057E";      // mdi-volume-high
    private const string MicrophoneGlyph = "\U000F036C";  // mdi-microphone
    private const string ApplicationGlyph = "\U000F0614";  // mdi-application

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
            "master-volume", Tr("Master volume"), VolumeGlyph, AudioDeviceParameter.DefaultDeviceId);

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
}
