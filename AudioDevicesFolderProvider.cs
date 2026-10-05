using System.Text;
using LoupixDeck.Plugin.Audio.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Top-level folder for the Windows-Audio command. Lists each active endpoint of
/// the chosen kind and lets the user open a sub-folder to control volume/mute.
/// Each endpoint is a tile drawn like a mixer tile (icon, level, name) in the look the command's
/// parameters choose; the default endpoint carries the selection frame. The levels follow the
/// devices, so the folder refreshes on a timer while it is open.
/// </summary>
public sealed class AudioDevicesFolderProvider : FolderProviderBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);

    private readonly IAudioService _audio;
    private readonly AudioEndpointKind _kind;
    private readonly AudioAliasStore _aliasStore;
    private readonly AudioVisibilityStore _visibility;
    private readonly AudioFolderGrid _grid;
    private readonly IPluginHost _host;
    private readonly MixerTileStyle _style;
    private readonly TileSlotPainter _painter;
    private readonly FolderPager _pager;

    private IReadOnlyList<DeviceRow>? _rows;
    private string? _rendered;
    private Timer? _refresh;

    // The timer, alias changes and the host's BuildEntries all touch the fields above from
    // different threads. _reloading keeps a slow tick (a pactl call can take a while) from
    // overlapping the next one, which System.Threading.Timer would otherwise start regardless.
    private readonly Lock _gate = new();
    private int _reloading;

    /// <summary>An endpoint as the tile shows it, read once per refresh.</summary>
    private sealed record DeviceRow(AudioEndpointInfo Endpoint, string Name, int Percent, bool Muted);

    internal AudioDevicesFolderProvider(IAudioService audio, AudioEndpointKind kind, AudioAliasStore aliasStore,
        AudioVisibilityStore visibility, AudioFolderGrid grid, IPluginHost host, MixerTileStyle style)
    {
        _audio = audio;
        _kind = kind;
        _aliasStore = aliasStore;
        _visibility = visibility;
        _grid = grid;
        _pager = new FolderPager(grid);
        _host = host;
        _style = style;
        _painter = new TileSlotPainter(RaiseEntriesChanged);
    }

    // Built while the plugin runs, so the host cannot translate it from the descriptors.
    public override string Title =>
        _host.Tr(_kind == AudioEndpointKind.Render ? "Output Devices" : "Input Devices");

    public override void OnEnter()
    {
        _aliasStore.Changed += OnAliasChanged;

        // The host builds the entries right after this returns, so nothing is announced here: an announcement
        // during OnEnter makes it redraw the page or parent folder first and race that with the new folder.
        Reload(announce: false);
        // The timer only lives while the folder is open, so a closed folder costs nothing.
        _refresh = new Timer(_ => OnTimer(), null, RefreshInterval, RefreshInterval);
    }

    public override void OnExit()
    {
        _aliasStore.Changed -= OnAliasChanged;
        _refresh?.Dispose();
        _refresh = null;
        _painter.Stop();
    }

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        IReadOnlyList<DeviceRow>? rows;
        lock (_gate) rows = _rows;
        if (rows == null)
        {
            rows = ReadRows();
            lock (_gate) _rows ??= rows;
        }

        lock (_gate) return BuildEntries(rows);
    }

    private List<FolderEntry> BuildEntries(IReadOnlyList<DeviceRow> rows)
    {
        List<FolderEntry> entries = new(rows.Count);
        HashSet<string> used = [];
        HashSet<int> shown = [];
        FolderPage page = _pager.Layout(rows.Count);

        // Fill the slots in reading order, skipping the reserved back-button slot.
        for (int index = 0; index < page.Count; index++)
        {
            int slot = _grid.SlotForIndex(index);
            shown.Add(slot);
            DeviceRow row = rows[page.First + index];

            // The default endpoint is the one in use. It is not a selection, so it gets no frame; its name is
            // drawn bold and bright instead.
            MixerTileData data = new(row.Name, row.Percent, row.Muted, row.Endpoint.IsDefault, null, 0,
                _painter.Frame, GlyphFor(_kind), Framed: false);

            TileSlotSpec spec = new(
                slot,
                row.Muted ? PluginColor.FromRgb(0x6E, 0x76, 0x7E) : PluginColor.FromRgb(0xFF, 0xFF, 0xFF),
                OpensFolder: new AudioDeviceControlFolderProvider(_audio, row.Endpoint, _kind, _aliasStore, _host));

            entries.Add(_painter.Entry(spec, data, _style,
                $"{row.Endpoint.Id}|{row.Percent}|{row.Muted}|{row.Endpoint.IsDefault}|{row.Name}", used));
        }

        _painter.Prune(shown, used);
        FolderPager.AddNavigation(entries, page, _host, TurnPage);

        if (entries.Count == 0)
        {
            entries.Add(new FolderEntry
            {
                SlotIndex = 0,
                Text = _host.Tr("No devices"),
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

    private static MixerTileGlyph GlyphFor(AudioEndpointKind kind) =>
        kind == AudioEndpointKind.Capture ? MixerTileGlyph.Microphone : MixerTileGlyph.Speaker;

    private IReadOnlyList<DeviceRow> ReadRows()
    {
        List<DeviceRow> rows = [];
        foreach (AudioEndpointInfo endpoint in _visibility.Visible(_audio.GetEndpoints(_kind)))
        {
            // An endpoint that vanishes between the listing and the read must not take the folder down.
            float volume = 0f;
            bool muted = false;
            try
            {
                volume = _audio.GetVolume(endpoint.Id);
                muted = _audio.GetMute(endpoint.Id);
            }
            catch (Exception ex)
            {
                _host.Logger?.Warn($"Audio devices: could not read '{endpoint.FriendlyName}': {ex.Message}");
            }

            rows.Add(new DeviceRow(endpoint, _aliasStore.Resolve(endpoint), (int)Math.Round(volume * 100f), muted));
        }
        return rows;
    }

    private void OnAliasChanged()
    {
        // A renamed device has to show its new name even though nothing else changed.
        lock (_gate) _rendered = null;
        Reload();
    }

    /// <summary>A timer tick, skipped while the previous one is still reading.</summary>
    private void OnTimer()
    {
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0) return;
        try { Reload(); }
        finally { Volatile.Write(ref _reloading, 0); }
    }

    /// <summary>
    /// Re-reads the devices. Runs on the refresh timer's thread-pool thread, where an exception —
    /// a COM error while the Windows audio service restarts, say — would be unhandled and take
    /// the host down, so a failed read keeps the tiles as they are and is logged.
    /// </summary>
    private void Reload(bool announce = true)
    {
        try
        {
            // The backend is read outside the lock, so a slow read never blocks the host's
            // BuildEntries; only the swap and the comparison are serialised.
            IReadOnlyList<DeviceRow> rows = ReadRows();

            bool changed;
            lock (_gate)
            {
                _rows = rows;
                changed = UpdateSnapshot();
            }

            if (changed && announce) RaiseEntriesChanged();
        }
        catch (Exception ex)
        {
            _host.Logger?.Warn($"Audio devices: refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reports whether the tiles would actually look different, so new entries are announced only then.
    /// The host repaints every slot of the folder on each change, so raising the event on every timer
    /// tick would push a full redraw to the device more than once a second for nothing. Call under
    /// <see cref="_gate"/>; the caller raises the event after leaving it.
    /// </summary>
    private bool UpdateSnapshot()
    {
        IReadOnlyList<DeviceRow> rows = _rows ?? [];

        // Only the current page is on screen, so only its names may need the marquee.
        FolderPage page = _pager.Layout(rows.Count);
        _painter.UpdateMarquee(
            rows.Skip(page.First).Take(page.Count).Select(row => (row.Name, row.Endpoint.IsDefault)), _style);

        StringBuilder builder = new();
        builder.Append(_painter.KeySize).Append('|').Append(page.Page).Append('/').Append(page.PageCount).Append('|');
        foreach (DeviceRow row in rows)
        {
            builder.Append(row.Endpoint.Id).Append(':').Append(row.Name).Append(':').Append(row.Percent).Append(':')
                .Append(row.Muted ? '1' : '0').Append(':').Append(row.Endpoint.IsDefault ? '1' : '0').Append('|');
        }

        string snapshot = builder.ToString();
        if (string.Equals(snapshot, _rendered, StringComparison.Ordinal)) return false;

        _rendered = snapshot;
        return true;
    }
}
