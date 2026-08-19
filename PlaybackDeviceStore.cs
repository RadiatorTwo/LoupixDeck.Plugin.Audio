using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Holds the globally configured playback device for <c>Audio.PlaySound</c>.
/// </summary>
/// <remarks>
/// The settings page has no dropdown (<see cref="PluginSettingKind"/> offers only text,
/// password, number, toggle and heading), so the device is picked with one toggle per
/// device. <see cref="Normalize"/> turns those toggles back into a radio group after
/// every save, so exactly one device — or none, meaning the system default — is selected.
/// </remarks>
public sealed class PlaybackDeviceStore(IPluginSettings settings)
{
    /// <summary>Prefix of the per-device toggles shown on the settings page.</summary>
    internal const string TogglePrefix = "playback:";

    /// <summary>The canonical selection; the toggles are only its UI representation.</summary>
    internal const string SelectedKey = "playbackDevice";

    /// <summary>
    /// Endpoint id the sounds should play on, or null for the system default device.
    /// </summary>
    public string? SelectedId
    {
        get
        {
            string? id = settings.Get<string>(SelectedKey);
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
    }

    /// <summary>Whether the toggle of <paramref name="endpointId"/> is currently on.</summary>
    public bool IsSelected(string endpointId) =>
        string.Equals(SelectedId, endpointId, StringComparison.Ordinal);

    /// <summary>
    /// Collapses the per-device toggles back into a single selection and rewrites them so
    /// the page shows a consistent radio group. When several toggles are on, the one that
    /// is not the previous selection wins — that is the one the user just enabled.
    /// </summary>
    public void Normalize(IReadOnlyList<AudioEndpointInfo> renderEndpoints)
    {
        string? previous = SelectedId;

        // The currently selected device is considered even when it is not connected right
        // now, so unplugging it does not silently reset the choice to the system default.
        // Its toggle is still shown on the settings page, so switching it off clears it.
        List<string> candidates = [.. renderEndpoints.Select(e => e.Id)];
        if (previous != null && !candidates.Contains(previous, StringComparer.Ordinal))
            candidates.Add(previous);

        List<string> enabled = [];
        foreach (string candidate in candidates)
        {
            if (settings.Get<bool>(TogglePrefix + candidate))
                enabled.Add(candidate);
        }

        string selected = enabled.Count switch
        {
            0 => string.Empty,
            1 => enabled[0],
            _ => enabled.FirstOrDefault(id => !string.Equals(id, previous, StringComparison.Ordinal))
                 ?? enabled[0]
        };

        settings.Set(SelectedKey, selected);

        // Rewrite every toggle from the resolved selection, and drop the ones belonging to
        // devices that are no longer present so the file does not accumulate stale keys.
        foreach (string candidate in candidates)
        {
            settings.Set(TogglePrefix + candidate,
                string.Equals(candidate, selected, StringComparison.Ordinal));
        }

        HashSet<string> present = new(renderEndpoints.Select(e => e.Id), StringComparer.Ordinal);
        List<string> stale = [];
        foreach (string key in settings.Keys)
        {
            if (!key.StartsWith(TogglePrefix, StringComparison.Ordinal)) continue;
            string id = key[TogglePrefix.Length..];
            if (!present.Contains(id) && !string.Equals(id, selected, StringComparison.Ordinal))
                stale.Add(key);
        }
        foreach (string key in stale) settings.Remove(key);

        settings.Save();
    }
}
