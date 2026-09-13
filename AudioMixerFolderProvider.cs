using System.Text;
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
    private readonly FolderGridInfo _grid;
    private readonly Dictionary<int, RotaryOverride> _rotaries;

    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private string? _selectedAppId;
    private string? _rendered;
    private Timer? _refresh;

    public AudioMixerFolderProvider(IAudioService audio, FolderGridInfo grid)
    {
        _audio = audio;
        _grid = grid;
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
        // Force the first frame: the folder may have been open before with other content.
        _rendered = null;
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
        int index = 0;

        foreach (AudioSessionInfo session in _sessions)
        {
            int slot = _grid.SlotForIndex(index++);
            if (slot < 0) break; // grid full

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
                    RaiseIfChanged();
                    return Task.CompletedTask;
                }
            });
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

        RaiseIfChanged();
    }

    /// <summary>
    /// Announces new entries only when the tiles would actually look different. The host
    /// repaints every slot of the folder on each change, so raising the event on every
    /// timer tick would push a full redraw to the device more than once a second for
    /// nothing — most ticks read back exactly what is already on screen.
    /// </summary>
    private void RaiseIfChanged()
    {
        string snapshot = DescribeEntries();
        if (string.Equals(snapshot, _rendered, StringComparison.Ordinal)) return;

        _rendered = snapshot;
        RaiseEntriesChanged();
    }

    /// <summary>Everything a tile is drawn from, so an unchanged snapshot means unchanged pixels.</summary>
    private string DescribeEntries()
    {
        StringBuilder builder = new();
        builder.Append(_selectedAppId).Append('|');

        foreach (AudioSessionInfo session in _sessions)
        {
            builder.Append(session.AppId).Append(':')
                .Append(session.DisplayName).Append(':')
                // The tile shows whole percent, so a smaller change is invisible.
                .Append((int)Math.Round(session.Volume * 100f)).Append(':')
                .Append(session.Muted ? '1' : '0').Append('|');
        }

        return builder.ToString();
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
