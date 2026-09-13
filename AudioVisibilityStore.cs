using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Which endpoints the device-picker folders leave out. Stored as one "hidden" toggle per
/// endpoint id, so an unknown (newly plugged) device defaults to visible, and a device
/// unplugged while hidden stays hidden because the setting is keyed by id, not by presence.
/// Unlike <see cref="PlaybackDeviceStore"/> the toggles are independent (no radio-group
/// collapsing) and stale ids are never pruned, so hiding an endpoint survives it going offline.
/// </summary>
public sealed class AudioVisibilityStore(IPluginSettings settings)
{
    /// <summary>Prefix of the per-device toggles shown on the settings page.</summary>
    internal const string TogglePrefix = "hidden.";

    /// <summary>Whether <paramref name="endpointId"/> is hidden from the device picker.</summary>
    public bool IsHidden(string endpointId) =>
        settings.Get(TogglePrefix + endpointId, false);

    /// <summary>The subset of <paramref name="endpoints"/> that is not hidden.</summary>
    public IReadOnlyList<AudioEndpointInfo> Visible(IReadOnlyList<AudioEndpointInfo> endpoints) =>
        [.. endpoints.Where(ep => !IsHidden(ep.Id))];
}
