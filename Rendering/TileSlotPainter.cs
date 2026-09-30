using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio.Rendering;

/// <summary>What a tile slot does besides showing its picture.</summary>
/// <param name="Slot">The folder slot the tile goes into.</param>
/// <param name="TextColor">Colour of the text the host draws over the picture.</param>
/// <param name="OnPress">Runs when the tile is pressed.</param>
/// <param name="OpensFolder">The folder the tile opens when pressed.</param>
internal readonly record struct TileSlotSpec(
    int Slot, PluginColor TextColor, Func<Task>? OnPress = null, IFolderProvider? OpensFolder = null);

/// <summary>
/// Turns <see cref="MixerTileData"/> into folder entries for one folder: draws each tile through the host on
/// a canvas of the key's real size, or as a cached PNG on a host without that, and runs the timer that moves a
/// name that is too wide for its tile. Every folder that shows mixer-style tiles owns one.
/// </summary>
internal sealed class TileSlotPainter(Action redraw)
{
    // A scrolling name moves 20 px per second. Every step repaints the folder, so it advances
    // 2 px every 100 ms instead of 1 px every 50 ms: the same speed for half the repaints.
    private static readonly TimeSpan MarqueeInterval = TimeSpan.FromMilliseconds(100);
    private const int MarqueeStepFrames = 2;

    // From SDK 1.28.0 the host lets a slot draw itself on a canvas of the key's real size, so the tile is
    // pixel-exact on any key calibration. An older host only takes a PNG, which it has to scale to the key.
    private static readonly bool HostDrawsSlots = SdkInfo.Version >= new Version(1, 28, 0);

    private static readonly PluginColor TileBack = PluginColor.FromRgb(0x0A, 0x0B, 0x0D);

    private readonly MixerTileRenderer _renderer = new();
    private readonly Dictionary<string, byte[]> _tiles = [];

    // Which slots drew a name that is wider than the tile in the smooth font, learned from drawing it: only the
    // canvas can measure that font.
    private readonly ConcurrentDictionary<int, bool> _overflowBySlot = [];

    private Timer? _marquee;
    private int _marqueeFrame;

    // The key size is only known once the host draws a slot; until then the design's 90 px is assumed. It decides
    // whether a name still fits and so whether the marquee has to run.
    private volatile int _keySize = TileSurface.DesignSize;

    /// <summary>Edge of the key the tiles are drawn for.</summary>
    public int KeySize => _keySize;

    /// <summary>The frame of the running marquee, or 0 while none runs. One counter serves all tiles: a tile
    /// whose name fits, or that may not scroll, ignores it.</summary>
    public int Frame => _marquee != null ? _marqueeFrame : 0;

    /// <summary>
    /// The entry for one tile. <paramref name="pictureKey"/> names everything the tile is drawn from except the
    /// marquee frame; it is only used to cache the PNG of a host that cannot draw slots itself.
    /// </summary>
    public FolderEntry Entry(TileSlotSpec spec, MixerTileData data, MixerTileStyle style, string pictureKey,
        HashSet<string> used)
    {
        if (HostDrawsSlots) return DrawnSlot(spec, data, style);

        // The PNG fallback caches pictures per state, so a picture that does not move must not depend on the frame.
        int pictureFrame = MixerTileRenderer.Scrolls(data.Name, data.Selected, style, _keySize) ? data.MarqueeFrame : 0;
        return PictureSlot(spec, data with { MarqueeFrame = pictureFrame }, style, $"{pictureKey}|{pictureFrame}", used);
    }

    /// <summary>Forgets what was learned about slots and pictures that are no longer shown.</summary>
    public void Prune(int shownSlots, HashSet<string> usedPictures)
    {
        // A slot that is no longer shown must not keep the marquee alive.
        foreach (int slot in _overflowBySlot.Keys.Where(k => k >= shownSlots).ToList())
            _overflowBySlot.TryRemove(slot, out _);

        // Pictures of states that are gone must not pile up, least of all every frame of a marquee.
        lock (_tiles)
        {
            foreach (string stale in _tiles.Keys.Where(key => !usedPictures.Contains(key)).ToList())
                _tiles.Remove(stale);
        }
    }

    /// <summary>Runs the marquee timer only while some tile has a name to move.</summary>
    public void UpdateMarquee(IEnumerable<(string Name, bool Selected)> tiles, MixerTileStyle style)
    {
        if (!AnyNeedsMarquee(tiles, style))
        {
            Stop();
            return;
        }

        _marquee ??= new Timer(_ => Tick(), null, MarqueeInterval, MarqueeInterval);
    }

    /// <summary>Stops the marquee and rewinds it.</summary>
    public void Stop()
    {
        _marquee?.Dispose();
        _marquee = null;
        _marqueeFrame = 0;
    }

    private bool AnyNeedsMarquee(IEnumerable<(string Name, bool Selected)> tiles, MixerTileStyle style)
    {
        // The smooth font can only tell from the canvas that a name is too wide, the pixel font can tell up front.
        if (_overflowBySlot.Values.Any(overflows => overflows)) return true;

        return tiles.Any(tile => MixerTileRenderer.Scrolls(tile.Name, tile.Selected, style, _keySize));
    }

    private void Tick()
    {
        _marqueeFrame += MarqueeStepFrames;
        redraw();
    }

    /// <summary>
    /// A slot the host asks to draw itself, on a canvas of the key's real size. Kept out of line: the
    /// JIT resolves <c>FolderEntry.Render</c> when it compiles this method, which must not happen on a
    /// host whose SDK lacks the member.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private FolderEntry DrawnSlot(TileSlotSpec spec, MixerTileData data, MixerTileStyle style) => new()
    {
        SlotIndex = spec.Slot,
        // The callback draws the text itself, so the host has none to add.
        Text = string.Empty,
        TextSize = 14,
        TextColor = spec.TextColor,
        BackColor = TileBack,
        OnPress = spec.OnPress,
        OpensFolder = spec.OpensFolder,
        Render = canvas =>
        {
            int size = Math.Min(canvas.Width, canvas.Height);
            _keySize = size;
            // The picture goes on the canvas through DrawPixels, and the smooth-font text is drawn over it with the
            // host font at exact positions. That text is only measurable here, on the canvas, so the drawing also
            // tells whether the selected name is too wide and has to scroll.
            _overflowBySlot[spec.Slot] = _renderer.Draw(canvas, data, style, size);
        }
    };

    /// <summary>The same slot as a 90 x 90 PNG, for a host without <c>FolderEntry.Render</c>.</summary>
    private FolderEntry PictureSlot(TileSlotSpec spec, MixerTileData data, MixerTileStyle style, string key,
        HashSet<string> used)
    {
        used.Add(key);
        byte[] png;
        lock (_tiles)
        {
            if (!_tiles.TryGetValue(key, out byte[]? cached))
            {
                cached = _renderer.RenderPng(data, style);
                _tiles[key] = cached;
            }
            png = cached;
        }

        return new FolderEntry
        {
            SlotIndex = spec.Slot,
            Image = png,
            Text = MixerTileRenderer.HostText(data, style),
            TextSize = 14,
            TextColor = spec.TextColor,
            BackColor = TileBack,
            OnPress = spec.OnPress,
            OpensFolder = spec.OpensFolder
        };
    }
}
