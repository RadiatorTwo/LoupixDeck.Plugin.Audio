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

    private IReadOnlyList<DeviceRow>? _rows;
    private string? _rendered;
    private Timer? _refresh;

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
        _refresh = new Timer(_ => Reload(), null, RefreshInterval, RefreshInterval);
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
        IReadOnlyList<DeviceRow> rows = _rows ??= ReadRows();
        List<FolderEntry> entries = new(rows.Count);
        HashSet<string> used = [];

        // Fill the slots in reading order, skipping the reserved back-button slot.
        int index = 0;
        foreach (DeviceRow row in rows)
        {
            int slot = _grid.SlotForIndex(index++);
            if (slot < 0) break; // grid full

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

        _painter.Prune(index, used);

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
        _rendered = null;
        Reload();
    }

    private void Reload(bool announce = true)
    {
        _rows = ReadRows();
        RaiseIfChanged(announce);
    }

    /// <summary>
    /// Announces new entries only when the tiles would actually look different. The host repaints every
    /// slot of the folder on each change, so raising the event on every timer tick would push a full
    /// redraw to the device more than once a second for nothing.
    /// </summary>
    private void RaiseIfChanged(bool announce = true)
    {
        IReadOnlyList<DeviceRow> rows = _rows ?? [];
        _painter.UpdateMarquee(rows.Select(row => (row.Name, row.Endpoint.IsDefault)), _style);

        StringBuilder builder = new();
        builder.Append(_painter.KeySize).Append('|');
        foreach (DeviceRow row in rows)
        {
            builder.Append(row.Endpoint.Id).Append(':').Append(row.Name).Append(':').Append(row.Percent).Append(':')
                .Append(row.Muted ? '1' : '0').Append(':').Append(row.Endpoint.IsDefault ? '1' : '0').Append('|');
        }

        string snapshot = builder.ToString();
        if (string.Equals(snapshot, _rendered, StringComparison.Ordinal)) return;

        _rendered = snapshot;
        if (announce) RaiseEntriesChanged();
    }
}
