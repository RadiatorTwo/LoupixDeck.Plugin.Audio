using System.Text;
using LoupixDeck.Plugin.Audio.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Per-application mixer. One tile per application currently playing audio, showing its
/// level; tapping a tile selects it, the first rotary then adjusts the selected app and
/// its press toggles mute. Refreshes on a timer because neither WASAPI sessions nor pactl
/// give a usable per-session change notification across both platforms.
/// Each tile is a picture drawn by <see cref="MixerTileRenderer"/> (icon, level, name); the look
/// is chosen by the command's layout and font parameters.
/// </summary>
public sealed class AudioMixerFolderProvider : FolderProviderBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);
    private const float StepScalar = 0.05f;

    private readonly IAudioService _audio;
    private readonly AudioFolderGrid _grid;
    private readonly IPluginHost _host;
    private readonly MixerTileStyle _style;
    private readonly AppIdentityCache _identity;
    private readonly TileSlotPainter _painter;
    private readonly Dictionary<int, RotaryOverride> _rotaries;

    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private string? _selectedAppId;
    private string? _rendered;
    private Timer? _refresh;

    internal AudioMixerFolderProvider(IAudioService audio, AudioFolderGrid grid, IPluginHost host,
        MixerTileStyle style, AppIdentityCache identity)
    {
        _audio = audio;
        _grid = grid;
        _host = host;
        _style = style;
        _identity = identity;
        _painter = new TileSlotPainter(RaiseEntriesChanged);
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

    public override string Title => _host.Tr("Mixer");

    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => _rotaries;

    public override void OnEnter()
    {
        // The host builds the entries right after this returns, so nothing is announced here: an announcement
        // during OnEnter makes it redraw the page or parent folder first and race that with the new folder.
        Reload(announce: false);
        // The timer only lives while the folder is open, so a closed mixer costs nothing.
        _refresh = new Timer(_ => Reload(), null, RefreshInterval, RefreshInterval);
    }

    public override void OnExit()
    {
        _refresh?.Dispose();
        _refresh = null;
        _painter.Stop();
    }

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        List<FolderEntry> entries = [];
        HashSet<string> used = [];
        int index = 0;

        foreach (AudioSessionInfo session in _sessions)
        {
            int slot = _grid.SlotForIndex(index++);
            if (slot < 0) break; // grid full

            AudioSessionInfo captured = session;
            bool selected = string.Equals(captured.AppId, _selectedAppId, StringComparison.Ordinal);
            int percent = (int)Math.Round(captured.Volume * 100f);
            AppIdentity identity = _identity.Resolve(captured.ExecutablePath);
            string name = NameOf(captured, identity);

            MixerTileData data = new(name, percent, captured.Muted, selected, identity.Icon, identity.IconSize,
                _painter.Frame);

            TileSlotSpec spec = new(
                slot,
                captured.Muted ? PluginColor.FromRgb(0x6E, 0x76, 0x7E) : PluginColor.FromRgb(0xFF, 0xFF, 0xFF),
                () =>
                {
                    _selectedAppId = captured.AppId;
                    RaiseIfChanged();
                    return Task.CompletedTask;
                });

            entries.Add(_painter.Entry(spec, data, _style,
                $"{captured.AppId}|{percent}|{captured.Muted}|{selected}|{name}|{captured.ExecutablePath}", used));
        }

        _painter.Prune(index, used);

        if (entries.Count == 0)
        {
            entries.Add(new FolderEntry
            {
                SlotIndex = 0,
                Text = _host.Tr("No audio"),
                TextSize = 14,
                BackColor = PluginColor.FromRgb(0x30, 0x30, 0x30)
            });
        }

        return entries;
    }

    /// <summary>The executable's file description ("Google Chrome") when it has one, otherwise what the session calls itself.</summary>
    private static string NameOf(AudioSessionInfo session, AppIdentity identity) =>
        identity.FriendlyName ?? session.DisplayName;

    private void Reload(bool announce = true)
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

        RaiseIfChanged(announce);
    }

    /// <summary>
    /// Announces new entries only when the tiles would actually look different. The host
    /// repaints every slot of the folder on each change, so raising the event on every
    /// timer tick would push a full redraw to the device more than once a second for
    /// nothing — most ticks read back exactly what is already on screen.
    /// </summary>
    private void RaiseIfChanged(bool announce = true)
    {
        _painter.UpdateMarquee(
            _sessions.Select(session => (NameOf(session, _identity.Resolve(session.ExecutablePath)),
                string.Equals(session.AppId, _selectedAppId, StringComparison.Ordinal))),
            _style);

        string snapshot = DescribeEntries();
        if (string.Equals(snapshot, _rendered, StringComparison.Ordinal)) return;

        _rendered = snapshot;
        if (announce) RaiseEntriesChanged();
    }

    /// <summary>Everything a tile is drawn from, so an unchanged snapshot means unchanged pixels.</summary>
    private string DescribeEntries()
    {
        StringBuilder builder = new();
        builder.Append(_selectedAppId).Append('|').Append(_painter.KeySize).Append('|');

        foreach (AudioSessionInfo session in _sessions)
        {
            builder.Append(session.AppId).Append(':')
                .Append(NameOf(session, _identity.Resolve(session.ExecutablePath))).Append(':')
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
