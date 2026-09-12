using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Per-application mixer. One tile per application currently playing audio, showing its
/// level; tapping a tile selects it, the first rotary then adjusts the selected app and
/// its press toggles mute. Refreshes on a timer because neither WASAPI sessions nor pactl
/// give a usable per-session change notification across both platforms.
/// </summary>
public sealed class AudioMixerFolderProvider : FolderProviderBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);
    private const float StepScalar = 0.05f;

    private readonly IAudioService _audio;
    private readonly Dictionary<int, RotaryOverride> _rotaries;

    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private string? _selectedAppId;
    private Timer? _refresh;

    public AudioMixerFolderProvider(IAudioService audio)
    {
        _audio = audio;
        _rotaries = new Dictionary<int, RotaryOverride>
        {
            [0] = new RotaryOverride
            {
                OnLeft = () => { Adjust(-StepScalar); return Task.CompletedTask; },
                OnRight = () => { Adjust(+StepScalar); return Task.CompletedTask; },
                OnPress = () => { ToggleMute(); return Task.CompletedTask; }
            }
        };
    }

    public override string Title => "Mixer";

    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => _rotaries;

    public override void OnEnter()
    {
        Reload();
        // The timer only lives while the folder is open, so a closed mixer costs nothing.
        _refresh = new Timer(_ => Reload(), null, RefreshInterval, RefreshInterval);
    }

    public override void OnExit()
    {
        _refresh?.Dispose();
        _refresh = null;
    }

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        List<FolderEntry> entries = [];
        int slot = 0;

        foreach (AudioSessionInfo session in _sessions)
        {
            if (slot == FolderLayout.BackSlotIndex) slot++;
            if (slot >= FolderLayout.TotalSlots) break;

            AudioSessionInfo captured = session;
            bool selected = string.Equals(captured.AppId, _selectedAppId, StringComparison.Ordinal);
            int percent = (int)Math.Round(captured.Volume * 100f);

            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                Text = $"{percent} %\n{captured.DisplayName}",
                TextSize = 13,
                Bold = selected,
                BackColor = captured.Muted
                    ? PluginColor.FromRgb(0x70, 0x20, 0x20)
                    : selected
                        ? PluginColor.FromRgb(0x20, 0x40, 0x60)
                        : PluginColor.FromRgb(0x20, 0x20, 0x40),
                OnPress = () =>
                {
                    _selectedAppId = captured.AppId;
                    RaiseEntriesChanged();
                    return Task.CompletedTask;
                }
            });
            slot++;
        }

        if (entries.Count == 0)
        {
            entries.Add(new FolderEntry
            {
                SlotIndex = 0,
                Text = "No audio",
                TextSize = 14,
                BackColor = PluginColor.FromRgb(0x30, 0x30, 0x30)
            });
        }

        return entries;
    }

    private void Reload()
    {
        _sessions = _audio.GetSessions(null);

        // An app that stopped playing must not keep the selection, or the rotary would
        // silently control nothing.
        if (_selectedAppId != null &&
            _sessions.All(s => !string.Equals(s.AppId, _selectedAppId, StringComparison.Ordinal)))
        {
            _selectedAppId = null;
        }

        _selectedAppId ??= _sessions.Count > 0 ? _sessions[0].AppId : null;

        RaiseEntriesChanged();
    }

    private void Adjust(float delta)
    {
        if (_selectedAppId == null) return;

        float? current = _audio.GetSessionVolume(null, _selectedAppId);
        if (current == null) return;

        _audio.SetSessionVolume(null, _selectedAppId, Math.Clamp(current.Value + delta, 0f, 1f));
        Reload();
    }

    private void ToggleMute()
    {
        if (_selectedAppId == null) return;

        bool? muted = _audio.GetSessionMute(null, _selectedAppId);
        if (muted == null) return;

        _audio.SetSessionMute(null, _selectedAppId, !muted.Value);
        Reload();
    }
}
