using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Side-strip provider that shows the volume of the three dials adjacent to the strip
/// as three large vertical bars. Each bar follows the audio device that its rotary
/// actually controls — resolved from the rotary's bound <c>Audio.Volume*</c> command —
/// so it stays in sync with the dial assignment without separate configuration.
/// </summary>
internal sealed class AudioVolumeStripProvider(IAudioService audio, IPluginSettings settings)
    : ISideStripProvider, ISegmentStripProvider
{
    /// <summary>Settings key: when true the strip renders as 3 stacked horizontal
    /// segments instead of the default side-by-side vertical bars.</summary>
    internal const string HorizontalLayoutKey = "strip.horizontalLayout";

    public string Id => "audio.volume-bars";
    public string Title => "Audio Volume Bars";

    // Live sessions, so a settings change can repaint the affected strips immediately.
    private readonly List<AudioVolumeStripSession> _sessions = [];

    public ISideStripSession CreateSession(SideStripContext context)
    {
        var session = new AudioVolumeStripSession(audio, settings, context, Forget);
        lock (_sessions) _sessions.Add(session);
        return session;
    }

    private void Forget(AudioVolumeStripSession session)
    {
        lock (_sessions) _sessions.Remove(session);
    }

    /// <summary>Repaints all live strips — called after the layout setting changes.</summary>
    public void NotifyLayoutChanged()
    {
        AudioVolumeStripSession[] snapshot;
        lock (_sessions) snapshot = _sessions.ToArray();
        foreach (var session in snapshot) session.RaiseChanged();
    }
}

/// <summary>One live attachment of <see cref="AudioVolumeStripProvider"/> to a strip.</summary>
internal sealed class AudioVolumeStripSession : ISideStripSession, ISegmentStripSession
{
    private sealed class Bar
    {
        public string Name = string.Empty;
        public string? DeviceId;
        public float Volume;
        public bool Muted;
        public IDisposable? Subscription;
    }

    private readonly IAudioService _audio;
    private readonly IPluginSettings _settings;
    private readonly SideStripContext _context;
    private readonly Action<AudioVolumeStripSession> _onDisposed;
    private readonly int _width;
    private readonly int _height;
    private readonly List<Bar> _bars = [];

    // Set once the host renders this session per-segment (segmented mode) so tap hit-testing
    // uses the vertical/stacked axis regardless of the whole-strip layout setting.
    private volatile bool _segmentMode;

    public event EventHandler? StripChanged;

    /// <summary>Forces a redraw of this strip (used when the layout setting changes).</summary>
    public void RaiseChanged() => StripChanged?.Invoke(this, EventArgs.Empty);

    public AudioVolumeStripSession(IAudioService audio, IPluginSettings settings,
        SideStripContext context, Action<AudioVolumeStripSession> onDisposed)
    {
        _audio = audio;
        _settings = settings;
        _context = context;
        _onDisposed = onDisposed;
        _width = context.Width;
        _height = context.Height;

        // Friendly-name lookup so a dial without a custom label still shows the device
        // it controls instead of a blank bar.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var ep in _audio.GetEndpoints(AudioEndpointKind.Render)
                         .Concat(_audio.GetEndpoints(AudioEndpointKind.Capture)))
                names[ep.Id] = ep.FriendlyName;
        }
        catch { /* enumeration is best-effort; fall back to dial labels */ }

        foreach (var rotary in context.Rotaries)
        {
            var deviceId = AudioStripCommandParser.ExtractDeviceId(rotary);
            var bar = new Bar
            {
                DeviceId = deviceId,
                Name = ResolveName(rotary, deviceId, names)
            };

            if (bar.DeviceId != null)
            {
                try { bar.Volume = _audio.GetVolume(bar.DeviceId); bar.Muted = _audio.GetMute(bar.DeviceId); }
                catch { /* endpoint may have vanished */ }

                try
                {
                    bar.Subscription = _audio.SubscribeVolumeChanges(bar.DeviceId, (vol, mute) =>
                    {
                        bar.Volume = vol;
                        bar.Muted = mute;
                        StripChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
                catch { /* no live notifications — bars still render from the seed values */ }
            }

            _bars.Add(bar);
        }
    }

    public bool RenderStrip(IRenderCanvas canvas)
    {
        // Nothing audio-related on this side → let the host fall back to dial labels.
        if (_bars.Count == 0 || _bars.All(b => b.DeviceId == null))
            return false;

        var horizontal = _settings.Get(AudioVolumeStripProvider.HorizontalLayoutKey, false);
        AudioStripRenderer.Render(_bars.Select(b => new AudioStripRenderer.BarView(
            b.Name, b.DeviceId != null, Math.Clamp(b.Volume, 0f, 1f), b.Muted)).ToList(),
            canvas, horizontal);
        return true;
    }

    /// <summary>
    /// Draws one segment in the host's segmented mode: the single band for the dial at
    /// <paramref name="rotaryIndex"/>, or <c>false</c> when that dial controls no audio device so
    /// the host draws its normal label. Always a stacked band (the whole-strip vertical/horizontal
    /// layout setting does not apply to an individual segment).
    /// </summary>
    public bool RenderSegment(int rotaryIndex, IRenderCanvas canvas)
    {
        _segmentMode = true;

        if (rotaryIndex < 0 || rotaryIndex >= _bars.Count)
            return false;

        var bar = _bars[rotaryIndex];
        if (bar.DeviceId == null)
            return false;

        AudioStripRenderer.RenderBand(
            new AudioStripRenderer.BarView(bar.Name, true, Math.Clamp(bar.Volume, 0f, 1f), bar.Muted),
            canvas);
        return true;
    }

    /// <summary>Picks the best display name for a dial: an explicit rotary label wins,
    /// otherwise the controlled device's friendly name, otherwise a generic dial number.</summary>
    private static string ResolveName(SideStripRotary rotary, string? deviceId,
        IReadOnlyDictionary<string, string> names)
    {
        if (!string.IsNullOrWhiteSpace(rotary.Label))
            return rotary.Label.Trim();

        if (deviceId != null && names.TryGetValue(deviceId, out var friendly)
                             && !string.IsNullOrWhiteSpace(friendly))
            return friendly;

        return $"Dial {rotary.Index + 1}";
    }

    /// <summary>Tapping a bar toggles mute on that bar's device. The bars run left-to-right
    /// in vertical mode and top-to-bottom in horizontal mode, so hit-test the matching axis.
    /// In segmented mode the bands are always stacked vertically (one per segment), so the
    /// y-axis is used regardless of the whole-strip layout setting.</summary>
    public void OnStripTapped(int x, int y)
    {
        if (_bars.Count == 0) return;
        var stacked = _segmentMode || _settings.Get(AudioVolumeStripProvider.HorizontalLayoutKey, false);
        var index = stacked
            ? Math.Clamp((int)(y / (_height / (float)_bars.Count)), 0, _bars.Count - 1)
            : Math.Clamp((int)(x / (_width / (float)_bars.Count)), 0, _bars.Count - 1);
        var bar = _bars[index];
        if (bar.DeviceId == null) return;

        try
        {
            var muted = !_audio.GetMute(bar.DeviceId);
            _audio.SetMute(bar.DeviceId, muted);
            bar.Muted = muted;
            StripChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* endpoint gone */ }
    }

    /// <summary>Swiping the strip still pages this side's rotary pages.</summary>
    public void OnStripSwiped(StripSwipeDirection direction)
    {
        if (direction == StripSwipeDirection.Up) _context.RequestNextPage();
        else _context.RequestPreviousPage();
    }

    public void Dispose()
    {
        foreach (var bar in _bars)
        {
            try { bar.Subscription?.Dispose(); }
            catch { /* best effort */ }
        }
        _bars.Clear();
        _onDisposed(this);
    }
}

/// <summary>Extracts the audio device id a rotary controls from its bound command.</summary>
internal static class AudioStripCommandParser
{
    private static readonly string[] VolumeCommands =
        ["Audio.VolumeUp", "Audio.VolumeDown", "Audio.MuteToggle"];

    public static string? ExtractDeviceId(SideStripRotary rotary)
    {
        return FromCommand(rotary.RightCommand)
               ?? FromCommand(rotary.LeftCommand)
               ?? FromCommand(rotary.PressCommand);
    }

    private static string? FromCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;

        foreach (var name in VolumeCommands)
        {
            var marker = name + "(";
            var open = command.IndexOf(marker, StringComparison.Ordinal);
            if (open < 0) continue;

            var start = open + marker.Length;
            var close = command.IndexOf(')', start);
            if (close < 0) continue;

            var id = command[start..close].Trim();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        return null;
    }
}

/// <summary>Draws the volume bars onto a host <see cref="IRenderCanvas"/> — either side-by-side
/// vertical bars or stacked horizontal bands — using host primitives (so text matches the core
/// font). The host serializes the call against its own Skia work.</summary>
internal static class AudioStripRenderer
{
    public readonly record struct BarView(string Name, bool HasDevice, float Volume, bool Muted);

    private static readonly PluginColor Background = new(18, 18, 18);
    private static readonly PluginColor Track = new(48, 48, 48);
    private static readonly PluginColor FillActive = new(0x4C, 0xAF, 0x50);  // green
    private static readonly PluginColor FillMuted = new(0x9E, 0x9E, 0x9E);   // gray
    private static readonly PluginColor TextColor = new(0xE0, 0xE0, 0xE0);
    private static readonly PluginColor MuteColor = new(0xE5, 0x73, 0x73);   // red — mute indicator

    // The device bezel overlaps the outermost pixels of the 60×270 panel, so keep all
    // content clear of every edge by this inset.
    private const int Edge = 4;

    public static void Render(IReadOnlyList<BarView> bars, IRenderCanvas canvas, bool horizontal)
    {
        if (horizontal) RenderHorizontal(bars, canvas);
        else RenderVertical(bars, canvas);
    }

    private static void RenderVertical(IReadOnlyList<BarView> bars, IRenderCanvas canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;
        canvas.Clear(Background);

        var count = Math.Max(1, bars.Count);
        float contentLeft = Edge;
        float contentRight = width - Edge;
        var columnWidth = (contentRight - contentLeft) / count;
        const int gap = 4;
        // Equal top/bottom padding keeps the track vertically centered; the label
        // sits within the bottom padding band.
        const int vPad = 16;
        var trackTop = Edge + vPad;
        var trackBottom = height - Edge - vPad;
        var trackHeight = trackBottom - trackTop;
        const float labelSize = 11f;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var left = (int)Math.Round(contentLeft + i * columnWidth + gap);
            var right = (int)Math.Round(contentLeft + (i + 1) * columnWidth - gap);
            var colW = Math.Max(1, right - left);

            // Track.
            canvas.FillRoundedRectangle(left, trackTop, colW, trackHeight, 4, Track);

            if (!bar.HasDevice)
            {
                canvas.DrawText("–", left, trackTop, colW, trackHeight, TextColor, labelSize, centered: true);
                continue;
            }

            // Fill from the bottom proportional to volume.
            var fillHeight = (int)Math.Round(trackHeight * bar.Volume);
            if (fillHeight > 0)
                canvas.FillRoundedRectangle(left, trackBottom - fillHeight, colW, fillHeight, 4,
                    bar.Muted ? FillMuted : FillActive);

            // Label below the track (mute marker takes precedence).
            var label = bar.Muted ? "muted" : Fit(bar.Name, canvas, labelSize, colW);
            if (label.Length > 0)
                canvas.DrawText(label, left, trackBottom + 2, colW, height - trackBottom - 2,
                    TextColor, labelSize, centered: true);
        }
    }

    // Horizontal layout: the strip is split into N equal channel bands stacked top to
    // bottom. Each band is a self-contained card — device name on top, a full-width
    // volume bar in the middle, and the percentage (or "muted" / "—") underneath.
    private static void RenderHorizontal(IReadOnlyList<BarView> bars, IRenderCanvas canvas)
    {
        canvas.Clear(Background);

        var count = Math.Max(1, bars.Count);
        var bandHeight = (canvas.Height - 2f * Edge) / count;

        for (var i = 0; i < bars.Count; i++)
            DrawBand(canvas, bars[i], (int)Math.Round(Edge + i * bandHeight), (int)Math.Round(bandHeight));
    }

    /// <summary>
    /// Draws a single channel band (name + bar + value) filling the whole canvas. Used for one
    /// segment in the host's segmented strip mode (where each dial owns its own 60×90 region).
    /// </summary>
    public static void RenderBand(BarView bar, IRenderCanvas canvas)
    {
        // No opaque clear here: in segmented mode the host has already drawn the page wallpaper
        // into this segment, and the band should sit on top of it (the volume bar's own track
        // gives it contrast; text is outlined for legibility on any wallpaper).
        DrawBand(canvas, bar, 0, canvas.Height);
    }

    /// <summary>Draws one channel band — device name on top, a full-width volume bar in the
    /// middle, the percentage (or "muted"/"—") underneath — centered within the band rect
    /// <c>[bandTop, bandTop+bandHeight)</c> of the given canvas.</summary>
    private static void DrawBand(IRenderCanvas canvas, BarView bar, int bandTop, int bandHeight)
    {
        const int sideInset = 10;  // wider left/right margin so the bar + text clear the bezel
        const int barH = 12;       // thickness of the volume bar
        const int nameH = 12;      // name row height
        const int valueH = 12;     // percentage row height
        const int gap = 7;         // vertical gap name↔bar and bar↔value
        const float fontSize = 12f;

        var left = sideInset;
        var contentWidth = canvas.Width - 2 * sideInset;
        var radius = barH / 2;

        // Center the name + bar + value group vertically within the band.
        var groupHeight = nameH + gap + barH + gap + valueH;
        var groupTop = bandTop + (bandHeight - groupHeight) / 2;

        var nameTop = groupTop;
        var barTop = groupTop + nameH + gap;
        var valueTop = barTop + barH + gap;

        // Device name on top (outlined so it stays legible over a page wallpaper).
        canvas.DrawText(Fit(bar.Name, canvas, fontSize, contentWidth), 0, nameTop, canvas.Width, nameH,
            TextColor, fontSize, centered: true, outlined: true, outlineColor: PluginColor.Black);

        // Volume bar (track + left-anchored fill). The fill grows proportionally from a thin
        // sliver (≈2px) so low volumes are distinguishable; its corner radius is clamped to half
        // its width so a narrow fill stays a small pill instead of snapping to a 12px round dot.
        canvas.FillRoundedRectangle(left, barTop, contentWidth, barH, radius, Track);
        if (bar.HasDevice && bar.Volume > 0f)
        {
            var fillWidth = Math.Max(2, (int)Math.Round(contentWidth * bar.Volume));
            var fillRadius = Math.Min(radius, fillWidth / 2);
            canvas.FillRoundedRectangle(left, barTop, fillWidth, barH, fillRadius, bar.Muted ? FillMuted : FillActive);
        }

        // Value / state underneath: a red mute symbol when muted (outlined so it reads on any
        // wallpaper), otherwise the percentage (or "—" when the dial controls no device).
        if (bar.HasDevice && bar.Muted)
        {
            const int iconSize = 16;
            canvas.DrawSymbol("volume-mute", (canvas.Width - iconSize) / 2, valueTop - 2, iconSize, iconSize,
                new SymbolStyle(MuteColor) { Outlined = true, OutlineColor = PluginColor.Black, OutlineWidth = 1.5f });
        }
        else
        {
            var (value, valueColor) = !bar.HasDevice ? ("—", FillMuted)
                : ($"{(int)MathF.Round(bar.Volume * 100f)}%", TextColor);
            canvas.DrawText(value, 0, valueTop, canvas.Width, valueH, valueColor, fontSize, centered: true,
                outlined: true, outlineColor: PluginColor.Black);
        }
    }

    /// <summary>Truncates <paramref name="text"/> with a trailing ellipsis so it fits within
    /// <paramref name="maxWidth"/> in the host font. Keeps the narrow strip readable.</summary>
    private static string Fit(string text, IRenderCanvas canvas, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || canvas.MeasureText(text, fontSize) <= maxWidth)
            return text;

        const string ellipsis = "…";
        var trimmed = text;
        while (trimmed.Length > 1 && canvas.MeasureText(trimmed + ellipsis, fontSize) > maxWidth)
            trimmed = trimmed[..^1];

        return trimmed + ellipsis;
    }
}
