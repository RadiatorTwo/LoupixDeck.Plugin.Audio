using System.Runtime.CompilerServices;
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

    // A scrolling name moves 20 px per second. Every step repaints the folder, so it advances
    // 2 px every 100 ms instead of 1 px every 50 ms: the same speed for half the repaints.
    private static readonly TimeSpan MarqueeInterval = TimeSpan.FromMilliseconds(100);
    private const int MarqueeStepFrames = 2;

    // From SDK 1.28.0 the host lets a slot draw itself on a canvas of the key's real size, so the tile is
    // pixel-exact on any key calibration. An older host only takes a PNG, which it has to scale to the key.
    private static readonly bool HostDrawsSlots = SdkInfo.Version >= new Version(1, 28, 0);

    private readonly IAudioService _audio;
    private readonly AudioFolderGrid _grid;
    private readonly IPluginHost _host;
    private readonly MixerTileStyle _style;
    private readonly AppIdentityCache _identity;
    private readonly MixerTileRenderer _renderer = new();
    private readonly Dictionary<string, byte[]> _tiles = [];
    private readonly Dictionary<int, RotaryOverride> _rotaries;

    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private string? _selectedAppId;
    private string? _rendered;
    private Timer? _refresh;
    private Timer? _marquee;
    private int _marqueeFrame;

    // The key size is only known once the host draws a slot; until then the design's 90 px is assumed. It decides
    // whether a name still fits and so whether the marquee has to run.
    private volatile int _keySize = TileSurface.DesignSize;

    // Whether the selected tile's name is wider than the tile in the smooth font, learned from drawing it.
    private volatile bool _smoothOverflows;
    private string? _marqueeApp;

    internal AudioMixerFolderProvider(IAudioService audio, AudioFolderGrid grid, IPluginHost host,
        MixerTileStyle style, AppIdentityCache identity)
    {
        _audio = audio;
        _grid = grid;
        _host = host;
        _style = style;
        _identity = identity;
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
        StopMarquee();
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

            // Only the selected tile with an overlong name moves; every other tile keeps one picture.
            int frame = selected && _marquee != null ? _marqueeFrame : 0;
            MixerTileData data = new(name, percent, captured.Muted, selected, identity.Icon, identity.IconSize, frame);

            SlotSpec spec = new(
                slot,
                // Empty with the pixel font: the picture already carries the text.
                MixerTileRenderer.HostText(data, _style),
                captured.Muted ? PluginColor.FromRgb(0x6E, 0x76, 0x7E) : PluginColor.FromRgb(0xFF, 0xFF, 0xFF),
                () =>
                {
                    _selectedAppId = captured.AppId;
                    RaiseIfChanged();
                    return Task.CompletedTask;
                });

            entries.Add(HostDrawsSlots
                ? DrawnSlot(spec, data)
                : PictureSlot(spec, data,
                    $"{captured.AppId}|{percent}|{captured.Muted}|{selected}|{name}|{captured.ExecutablePath}|{frame}",
                    used));
        }

        // Pictures of states that are gone must not pile up, least of all every frame of a marquee.
        lock (_tiles)
        {
            foreach (string stale in _tiles.Keys.Where(key => !used.Contains(key)).ToList())
                _tiles.Remove(stale);
        }

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

    private readonly record struct SlotSpec(int Slot, string Text, PluginColor TextColor, Func<Task> OnPress);

    /// <summary>
    /// A slot the host asks to draw itself, on a canvas of the key's real size. Kept out of line: the
    /// JIT resolves <c>FolderEntry.Render</c> when it compiles this method, which must not happen on a
    /// host whose SDK lacks the member.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private FolderEntry DrawnSlot(SlotSpec spec, MixerTileData data) => new()
    {
        SlotIndex = spec.Slot,
        // The callback draws the text itself, so the host has none to add.
        Text = string.Empty,
        TextSize = 14,
        TextColor = spec.TextColor,
        BackColor = PluginColor.FromRgb(0x0A, 0x0B, 0x0D),
        OnPress = spec.OnPress,
        Render = canvas =>
        {
            int size = Math.Min(canvas.Width, canvas.Height);
            _keySize = size;
            // The picture goes on the canvas through DrawPixels, and the smooth-font text is drawn over it with the
            // host font at exact positions. That text is only measurable here, on the canvas, so the drawing also
            // tells whether the selected name is too wide and has to scroll.
            bool scrolls = _renderer.Draw(canvas, data, _style, size);
            if (data.Selected) _smoothOverflows = scrolls;
        }
    };

    /// <summary>The same slot as a 90 x 90 PNG, for a host without <c>FolderEntry.Render</c>.</summary>
    private FolderEntry PictureSlot(SlotSpec spec, MixerTileData data, string key, HashSet<string> used)
    {
        used.Add(key);
        byte[] png;
        lock (_tiles)
        {
            if (!_tiles.TryGetValue(key, out byte[]? cached))
            {
                cached = _renderer.RenderPng(data, _style);
                _tiles[key] = cached;
            }
            png = cached;
        }

        return new FolderEntry
        {
            SlotIndex = spec.Slot,
            Image = png,
            Text = spec.Text,
            TextSize = 14,
            TextColor = spec.TextColor,
            BackColor = PluginColor.FromRgb(0x0A, 0x0B, 0x0D),
            OnPress = spec.OnPress
        };
    }

    /// <summary>Whether the selected tile has a name that has to scroll to be read.</summary>
    private bool SelectedNeedsMarquee()
    {
        if (_selectedAppId == null) return false;

        foreach (AudioSessionInfo session in _sessions)
        {
            if (!string.Equals(session.AppId, _selectedAppId, StringComparison.Ordinal)) continue;
            // The smooth font can only tell from the canvas that a name is too wide, the pixel font can tell up front.
            return _smoothOverflows
                || MixerTileRenderer.Scrolls(NameOf(session, _identity.Resolve(session.ExecutablePath)), true, _style, _keySize);
        }
        return false;
    }

    /// <summary>
    /// Runs the marquee timer only while it has something to move, and restarts the scroll (with its
    /// one-second hold) whenever another tile is selected.
    /// </summary>
    private void UpdateMarquee()
    {
        if (!SelectedNeedsMarquee())
        {
            StopMarquee();
            return;
        }

        if (!string.Equals(_marqueeApp, _selectedAppId, StringComparison.Ordinal))
        {
            _marqueeApp = _selectedAppId;
            _marqueeFrame = 0;
        }

        _marquee ??= new Timer(_ => TickMarquee(), null, MarqueeInterval, MarqueeInterval);
    }

    private void StopMarquee()
    {
        _marquee?.Dispose();
        _marquee = null;
        _marqueeApp = null;
        _marqueeFrame = 0;
    }

    private void TickMarquee()
    {
        _marqueeFrame += MarqueeStepFrames;
        RaiseEntriesChanged();
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
        UpdateMarquee();

        string snapshot = DescribeEntries();
        if (string.Equals(snapshot, _rendered, StringComparison.Ordinal)) return;

        _rendered = snapshot;
        RaiseEntriesChanged();
    }

    /// <summary>Everything a tile is drawn from, so an unchanged snapshot means unchanged pixels.</summary>
    private string DescribeEntries()
    {
        StringBuilder builder = new();
        builder.Append(_selectedAppId).Append('|').Append(_keySize).Append('|');

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
