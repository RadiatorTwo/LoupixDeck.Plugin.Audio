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
    private readonly FolderPager _pager;

    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private string? _selectedAppId;
    private string? _rendered;
    private Timer? _refresh;

    // The timer, the rotary handlers and the host's BuildEntries all touch the fields above from
    // different threads. _reloading keeps a slow tick (a pactl call can take a while) from
    // overlapping the next one, which System.Threading.Timer would otherwise start regardless.
    private readonly Lock _gate = new();
    private int _reloading;

    internal AudioMixerFolderProvider(IAudioService audio, AudioFolderGrid grid, IPluginHost host,
        MixerTileStyle style, AppIdentityCache identity)
    {
        _audio = audio;
        _grid = grid;
        _pager = new FolderPager(grid);
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
        _refresh = new Timer(_ => OnTimer(), null, RefreshInterval, RefreshInterval);
    }

    public override void OnExit()
    {
        _refresh?.Dispose();
        _refresh = null;
        _painter.Stop();
    }

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        lock (_gate) return BuildEntriesLocked();
    }

    private List<FolderEntry> BuildEntriesLocked()
    {
        List<FolderEntry> entries = [];
        HashSet<string> used = [];
        HashSet<int> shown = [];
        FolderPage page = _pager.Layout(_sessions.Count);

        for (int index = 0; index < page.Count; index++)
        {
            int slot = _grid.SlotForIndex(index);
            shown.Add(slot);

            AudioSessionInfo captured = _sessions[page.First + index];
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
                    bool changed;
                    lock (_gate)
                    {
                        _selectedAppId = captured.AppId;
                        changed = UpdateSnapshot();
                    }

                    if (changed) RaiseEntriesChanged();
                    return Task.CompletedTask;
                });

            entries.Add(_painter.Entry(spec, data, _style,
                $"{captured.AppId}|{percent}|{captured.Muted}|{selected}|{name}|{captured.ExecutablePath}", used));
        }

        _painter.Prune(shown, used);
        FolderPager.AddNavigation(entries, page, _host, TurnPage);

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

    private void TurnPage(int delta)
    {
        bool changed;
        lock (_gate) changed = _pager.Turn(delta) && UpdateSnapshot();
        if (changed) RaiseEntriesChanged();
    }

    private string NameOf(AudioSessionInfo session, AppIdentity identity) =>
        AudioSessionNames.Display(session, identity, _host);

    /// <summary>A timer tick, skipped while the previous one is still reading.</summary>
    private void OnTimer()
    {
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0) return;
        try { Reload(); }
        finally { Volatile.Write(ref _reloading, 0); }
    }

    /// <summary>
    /// Re-reads the sessions. Runs on the refresh timer's thread-pool thread, where an exception
    /// would be unhandled and take the host down, so a failed read keeps the tiles as they are and
    /// is logged.
    /// </summary>
    private void Reload(bool announce = true)
    {
        try
        {
            // The backend is asked outside the lock, so a slow read never blocks the host's
            // BuildEntries; only the swap and the comparison are serialised.
            IReadOnlyList<AudioSessionInfo> sessions = _audio.GetSessions(null);

            bool changed;
            lock (_gate)
            {
                _sessions = sessions;

                // An app that stopped playing must not keep the selection, or the rotary would
                // silently control nothing.
                if (_selectedAppId != null &&
                    _sessions.All(s => !string.Equals(s.AppId, _selectedAppId, StringComparison.Ordinal)))
                {
                    _selectedAppId = null;
                }

                _selectedAppId ??= _sessions.Count > 0 ? _sessions[0].AppId : null;

                changed = UpdateSnapshot();
            }

            if (changed && announce) RaiseEntriesChanged();
        }
        catch (Exception ex)
        {
            _host.Logger?.Warn($"Audio mixer: refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reports whether the tiles would actually look different, so new entries are announced
    /// only then. The host repaints every slot of the folder on each change, so raising the
    /// event on every timer tick would push a full redraw to the device more than once a second
    /// for nothing — most ticks read back exactly what is already on screen. Call under
    /// <see cref="_gate"/>; the caller raises the event after leaving it.
    /// </summary>
    private bool UpdateSnapshot()
    {
        // Only the current page is on screen, so only its names may need the marquee.
        FolderPage page = _pager.Layout(_sessions.Count);
        _painter.UpdateMarquee(
            _sessions.Skip(page.First).Take(page.Count)
                .Select(session => (NameOf(session, _identity.Resolve(session.ExecutablePath)),
                string.Equals(session.AppId, _selectedAppId, StringComparison.Ordinal))),
            _style);

        string snapshot = $"{page.Page}/{page.PageCount}|{DescribeEntries()}";
        if (string.Equals(snapshot, _rendered, StringComparison.Ordinal)) return false;

        _rendered = snapshot;
        return true;
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

    private void Adjust(float delta) => Change("adjust", appId =>
    {
        float? current = _audio.GetSessionVolume(null, appId);
        if (current != null)
            _audio.SetSessionVolume(null, appId, Math.Clamp(current.Value + delta, 0f, 1f));
    });

    private void ToggleMute() => Change("toggle mute", appId =>
    {
        bool? muted = _audio.GetSessionMute(null, appId);
        if (muted != null)
            _audio.SetSessionMute(null, appId, !muted.Value);
    });

    /// <summary>Applies a rotary action to the selected app. An app that quit in between must
    /// cost the action, not the host.</summary>
    private void Change(string action, Action<string> apply)
    {
        string? appId;
        lock (_gate) appId = _selectedAppId;
        if (appId == null) return;

        try
        {
            apply(appId);
        }
        catch (Exception ex)
        {
            _host.Logger?.Warn($"Audio mixer: could not {action} '{appId}': {ex.Message}");
            return;
        }

        Reload();
    }
}
