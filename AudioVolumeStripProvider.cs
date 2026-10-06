using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Side-strip provider that shows the volume of the three dials adjacent to the strip
/// as three large vertical bars. Each bar follows the audio device or application that its
/// rotary actually controls — resolved from the rotary's bound <c>Audio.Volume*</c> or
/// <c>Audio.AppVolume*</c> command — so it stays in sync with the dial assignment without
/// separate configuration.
/// </summary>
internal sealed class AudioVolumeStripProvider(IAudioService audio, IPluginSettings settings, AudioAliasStore aliasStore,
    IPluginHost host)
    : ISideStripProvider, ISegmentStripProvider
{
    /// <summary>Settings key: when true the strip renders as 3 stacked horizontal
    /// segments instead of the default side-by-side vertical bars.</summary>
    internal const string HorizontalLayoutKey = "strip.horizontalLayout";

    public string Id => "audio.volume-bars";
    public string Title => host.Tr("Audio Volume Bars");

    // Live sessions, so a settings change can repaint the affected strips immediately.
    private readonly List<AudioVolumeStripSession> _sessions = [];

    public ISideStripSession CreateSession(SideStripContext context)
    {
        var session = new AudioVolumeStripSession(audio, settings, aliasStore, host, context, Forget);
        lock (_sessions) _sessions.Add(session);
        return session;
    }

    private void Forget(AudioVolumeStripSession session)
    {
        lock (_sessions) _sessions.Remove(session);
    }

    /// <summary>Repaints all live strips — called after settings change (layout toggle or a
    /// device alias rename). Names are resolved at render time, so the repaint picks them up.</summary>
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
        // Explicit rotary label (wins when set), the device's OS friendly name (alias
        // fallback) and the dial number (last-resort label). The displayed name is
        // resolved from these at render time so an alias rename repaints live.
        public string Label = string.Empty;
        public string Fallback = string.Empty;
        public int DialNumber;

        /// <summary>
        /// The endpoint this bar reads, already expanded from the binding: a dial bound to
        /// <c>@default</c> carries the endpoint that sentinel currently means, because the audio
        /// backend rejects the sentinel itself ("No such entity") and would report volume 0 and
        /// unmuted forever.
        /// </summary>
        public string? DeviceId;

        /// <summary>The default-device sentinel the dial is bound to (<c>@default</c> or
        /// <c>@defaultInput</c>), or null for a fixed endpoint. A bar that follows a default has
        /// <see cref="DeviceId"/> re-resolved when the backend reports a change instead of fixing
        /// it for the session.</summary>
        public string? DefaultSentinel;

        public bool FollowsDefault => DefaultSentinel != null;
        public float Volume;
        public bool Muted;
        public IDisposable? Subscription;

        /// <summary>
        /// The application a per-app dial controls, as bound (the foreground sentinel included), or null for a
        /// device dial. Such a dial has no endpoint; its level comes from <see cref="PullValue"/> and its mute
        /// state from the app's session.
        /// </summary>
        public string? AppId;

        /// <summary>
        /// Reads the level from the adjustment command bound to this dial. That command owns
        /// the value, so the bar follows it rather than asking the audio backend a second time
        /// and risking a different answer. Null for a dial bound to the old per-gesture
        /// commands, which is what <see cref="Volume"/> stays seeded for.
        /// </summary>
        public Func<AdjustmentValue?>? PullValue;

        /// <summary>The level to draw: the dial's command when it has one, else the seed.</summary>
        public float Level
        {
            get
            {
                AdjustmentValue? value = null;
                try { value = PullValue?.Invoke(); }
                catch { /* a command that throws costs its bar, not the strip */ }

                return value.HasValue ? Math.Clamp((float)value.Value.Normalized, 0f, 1f) : Volume;
            }
        }
    }

    private readonly IAudioService _audio;
    private readonly IPluginSettings _settings;
    private readonly AudioAliasStore _aliasStore;
    private readonly IPluginHost _host;
    private readonly SideStripContext _context;
    private readonly Action<AudioVolumeStripSession> _onDisposed;
    private readonly int _width;
    private readonly int _height;
    private readonly List<Bar> _bars = [];

    // Guards each bar's DeviceId/Subscription pair, which a rebind swaps from a notification
    // thread or the render path while another thread may be disposing the session.
    private readonly Lock _barLock = new();
    private bool _disposed;

    // Set once the host renders this session per-segment (segmented mode) so tap hit-testing
    // uses the vertical/stacked axis regardless of the whole-strip layout setting.
    private volatile bool _segmentMode;

    public event EventHandler? StripChanged;

    /// <summary>Forces a redraw of this strip (used when the layout setting changes).</summary>
    public void RaiseChanged() => StripChanged?.Invoke(this, EventArgs.Empty);

    public AudioVolumeStripSession(IAudioService audio, IPluginSettings settings,
        AudioAliasStore aliasStore, IPluginHost host, SideStripContext context,
        Action<AudioVolumeStripSession> onDisposed)
    {
        _audio = audio;
        _settings = settings;
        _aliasStore = aliasStore;
        _host = host;
        _context = context;
        _onDisposed = onDisposed;
        _width = context.Width;
        _height = context.Height;

        // Friendly-name lookup so a dial without a custom label still shows the device
        // it controls instead of a blank bar. The OS name is only the alias fallback —
        // the displayed name (alias-or-friendly) is resolved at render time.
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
            var boundId = AudioStripCommandParser.ExtractDeviceId(rotary);
            var deviceId = AudioDeviceParameter.ResolveEndpointId(boundId, _audio);
            var bar = new Bar
            {
                DeviceId = deviceId,
                DefaultSentinel = AudioDeviceParameter.IsDefaultSentinel(boundId) ? boundId : null,
                Label = rotary.Label?.Trim() ?? string.Empty,
                Fallback = deviceId != null && names.TryGetValue(deviceId, out var friendly)
                    ? friendly : string.Empty,
                DialNumber = rotary.Index + 1,
                PullValue = rotary.GetValue,
                AppId = boundId == null ? AudioStripCommandParser.ExtractAppId(rotary) : null
            };

            if (bar.DeviceId != null)
            {
                Seed(bar, bar.DeviceId);
                bar.Subscription = Subscribe(bar, bar.DeviceId);
            }

            _bars.Add(bar);
        }
    }

    /// <summary>Reads the current level of an endpoint into the bar.</summary>
    private void Seed(Bar bar, string deviceId)
    {
        try { bar.Volume = _audio.GetVolume(deviceId); bar.Muted = _audio.GetMute(deviceId); }
        catch { /* endpoint may have vanished */ }
    }

    /// <summary>Subscribes the bar to one endpoint's volume notifications. A notification that
    /// arrives after the bar moved to another endpoint is ignored.</summary>
    private IDisposable? Subscribe(Bar bar, string deviceId)
    {
        try
        {
            return _audio.SubscribeVolumeChanges(deviceId, (vol, mute) =>
            {
                if (!string.Equals(bar.DeviceId, deviceId, StringComparison.Ordinal)) return;

                // The default device changed under the dial: the values that came with the event
                // belong to the endpoint it left behind, so the rebind reads the new ones.
                if (bar.FollowsDefault && FollowDefault(bar, fresh: true)) return;

                bar.Volume = vol;
                bar.Muted = mute;
                StripChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch
        {
            // No live notifications — the bar still renders from the seed values.
            return null;
        }
    }

    /// <summary>Re-checks every bar that follows the default device. Runs on the render path, so it
    /// trusts the memoised default (one backend call per second at most) instead of forcing one.</summary>
    private void FollowDefaults()
    {
        foreach (Bar bar in _bars)
            if (bar.FollowsDefault) FollowDefault(bar, fresh: false);
    }

    public bool RenderStrip(IRenderCanvas canvas)
    {
        FollowDefaults();

        // Nothing audio-related on this side → let the host fall back to dial labels.
        if (_bars.Count == 0 || _bars.All(b => b.DeviceId == null && b.AppId == null))
            return false;

        var horizontal = _settings.Get(AudioVolumeStripProvider.HorizontalLayoutKey, false);
        AudioStripRenderer.Render(_bars.Select(View).ToList(), canvas, horizontal, _host.Tr("muted"));
        return true;
    }

    /// <summary>
    /// What one bar shows. A device bar draws its endpoint's level. An app bar draws the level of the app's
    /// session; an app that is not playing has none and draws as a dial without a value.
    /// </summary>
    private AudioStripRenderer.BarView View(Bar bar)
    {
        if (bar.DeviceId != null)
            return new AudioStripRenderer.BarView(DisplayName(bar), true, bar.Level, bar.Muted);

        if (bar.AppId == null)
            return new AudioStripRenderer.BarView(DisplayName(bar), false, 0f, false);

        string? appId = ResolveAppId(bar);
        float? level = null;
        bool muted = false;
        if (appId != null)
        {
            try
            {
                // The dial's own command owns the level when it is an adjustment command; a dial bound to the
                // older AppVolumeUp/Down commands has none, so the session is asked instead.
                AdjustmentValue? value = bar.PullValue?.Invoke();
                AudioSessionInfo? session = AudioAppParameter.FindSession(_audio, appId);
                level = value.HasValue
                    ? Math.Clamp((float)value.Value.Normalized, 0f, 1f)
                    : session?.Volume;
                muted = level != null && (session?.Muted ?? false);
            }
            catch
            {
                // Runs on the render path: an app that quit between the frame and the query costs its bar.
                level = null;
            }
        }

        return new AudioStripRenderer.BarView(DisplayName(bar, appId), level != null, level ?? 0f, muted);
    }

    /// <summary>The app the bar means right now: the foreground sentinel names whichever app is in front.</summary>
    private string? ResolveAppId(Bar bar)
    {
        if (bar.AppId == null) return null;

        try
        {
            return string.Equals(bar.AppId, AudioAppParameter.ForegroundAppId, StringComparison.Ordinal)
                ? _audio.GetForegroundAppId()
                : bar.AppId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Draws one segment in the host's segmented mode: the single band for the dial at
    /// <paramref name="rotaryIndex"/>, or <c>false</c> when that dial controls no device or app so
    /// the host draws its normal label. Always a stacked band (the whole-strip vertical/horizontal
    /// layout setting does not apply to an individual segment).
    /// </summary>
    public bool RenderSegment(int rotaryIndex, IRenderCanvas canvas)
    {
        _segmentMode = true;

        if (rotaryIndex < 0 || rotaryIndex >= _bars.Count)
            return false;

        var bar = _bars[rotaryIndex];
        if (bar.FollowsDefault) FollowDefault(bar, fresh: false);
        if (bar.DeviceId == null && bar.AppId == null)
            return false;

        AudioStripRenderer.RenderBand(View(bar), canvas);
        return true;
    }

    /// <summary>
    /// Re-reads the endpoint a default-following bar points at and, when the default moved, points
    /// the bar at the new endpoint: <see cref="Bar.DeviceId"/> switches at once, the subscription to
    /// the old endpoint is dropped and one to the new endpoint is made in the background. Without
    /// the new subscription, volume changes on the new default would never reach the bar. Returns
    /// true when the bar moved.
    /// </summary>
    /// <param name="bar">A bar bound to the default device.</param>
    /// <param name="fresh">Ask the backend instead of trusting the memoised default. Set when a
    /// notification or a tap says something may have changed.</param>
    private bool FollowDefault(Bar bar, bool fresh)
    {
        string? current;
        try
        {
            if (fresh) AudioDeviceParameter.InvalidateDefaultEndpoint();
            current = AudioDeviceParameter.ResolveEndpointId(bar.DefaultSentinel, _audio);
        }
        catch { return false; }

        if (current == null) return false;

        IDisposable? old;
        lock (_barLock)
        {
            if (_disposed || string.Equals(current, bar.DeviceId, StringComparison.Ordinal)) return false;

            bar.DeviceId = current;
            old = bar.Subscription;
            bar.Subscription = null;
        }

        // Off the calling thread: this may run inside the old subscription's own callback, which
        // must not dispose itself, or on the render path, which must not wait for the backend.
        _ = Task.Run(() =>
        {
            try { old?.Dispose(); }
            catch { /* best effort */ }

            // Read into locals: a later rebind may already own the bar, and its values must not be
            // overwritten with this endpoint's.
            float volume = 0f;
            bool muted = false;
            try { volume = _audio.GetVolume(current); muted = _audio.GetMute(current); }
            catch { /* endpoint may have vanished */ }

            IDisposable? subscription = Subscribe(bar, current);

            bool keep;
            lock (_barLock)
            {
                keep = !_disposed && bar.Subscription == null
                       && string.Equals(bar.DeviceId, current, StringComparison.Ordinal);
                if (keep)
                {
                    bar.Subscription = subscription;
                    bar.Volume = volume;
                    bar.Muted = muted;
                }
            }

            if (!keep)
            {
                // The session closed, or the default moved again while this one was being made.
                try { subscription?.Dispose(); }
                catch { /* best effort */ }
                return;
            }

            StripChanged?.Invoke(this, EventArgs.Empty);
        });

        return true;
    }

    /// <summary>Picks the best display name for a dial, resolved at render time so an alias
    /// rename repaints live: an explicit rotary label wins, otherwise the user-defined alias
    /// (falling back to the device's friendly name), otherwise a generic dial number.</summary>
    private string DisplayName(Bar bar, string? appId = null)
    {
        if (!string.IsNullOrWhiteSpace(bar.Label))
            return bar.Label;

        // An app dial is named after its app: the executable name, which is what the binding stores.
        if (!string.IsNullOrWhiteSpace(appId))
            return appId;

        if (bar.DeviceId != null)
        {
            var resolved = _aliasStore.Resolve(bar.DeviceId, bar.Fallback);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        // The dial number is a value, so only the fixed part is a key.
        return string.Format(_host.Tr("Dial {0}"), bar.DialNumber);
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
        if (bar.FollowsDefault) FollowDefault(bar, fresh: true);

        if (bar.DeviceId == null)
        {
            ToggleAppMute(bar);
            return;
        }

        try
        {
            var muted = !_audio.GetMute(bar.DeviceId);
            _audio.SetMute(bar.DeviceId, muted);
            bar.Muted = muted;
            StripChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* endpoint gone */ }
    }

    /// <summary>Tapping an app bar mutes the app, as pressing its dial does.</summary>
    private void ToggleAppMute(Bar bar)
    {
        string? appId = ResolveAppId(bar);
        if (appId == null) return;

        try
        {
            bool? muted = _audio.GetSessionMute(null, appId);
            if (muted == null) return; // not playing

            _audio.SetSessionMute(null, appId, !muted.Value);
            StripChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* app gone */ }
    }

    /// <summary>Swiping the strip still pages this side's rotary pages.</summary>
    public void OnStripSwiped(StripSwipeDirection direction)
    {
        if (direction == StripSwipeDirection.Up) _context.RequestNextPage();
        else _context.RequestPreviousPage();
    }

    public void Dispose()
    {
        List<IDisposable> subscriptions = [];
        lock (_barLock)
        {
            _disposed = true;
            foreach (var bar in _bars)
            {
                if (bar.Subscription != null) subscriptions.Add(bar.Subscription);
                bar.Subscription = null;
            }
        }

        foreach (IDisposable subscription in subscriptions)
        {
            try { subscription.Dispose(); }
            catch { /* best effort */ }
        }
        _bars.Clear();
        _onDisposed(this);
    }
}

/// <summary>Extracts the audio device id a rotary controls from its bound command.</summary>
internal static class AudioStripCommandParser
{
    // Audio.Volume first: it is what a dial is bound to now, and a migrated dial carries
    // nothing else. The app commands are kept apart: their first parameter is an app id,
    // not an endpoint.
    private static readonly string[] VolumeCommands =
        ["Audio.Volume", "Audio.VolumeUp", "Audio.VolumeDown", "Audio.MuteToggle"];

    private static readonly string[] AppVolumeCommands =
        ["Audio.AppVolume", "Audio.AppVolumeUp", "Audio.AppVolumeDown", "Audio.AppMuteToggle"];

    public static string? ExtractDeviceId(SideStripRotary rotary) => Extract(rotary, VolumeCommands);

    /// <summary>The app id a per-app dial controls, as bound; the foreground sentinel is kept as it is.</summary>
    public static string? ExtractAppId(SideStripRotary rotary) => Extract(rotary, AppVolumeCommands);

    private static string? Extract(SideStripRotary rotary, string[] commands)
    {
        return FromCommand(rotary.RightCommand, commands)
               ?? FromCommand(rotary.LeftCommand, commands)
               ?? FromCommand(rotary.PressCommand, commands);
    }

    private static string? FromCommand(string command, string[] commands)
    {
        if (string.IsNullOrEmpty(command)) return null;

        foreach (var name in commands)
        {
            var marker = name + "(";
            var open = command.IndexOf(marker, StringComparison.Ordinal);
            if (open < 0) continue;

            var start = open + marker.Length;
            var close = command.IndexOf(')', start);
            if (close < 0) continue;

            var id = command[start..close].Trim();
            // The device id is the first parameter; ignore any trailing settings such as the
            // configurable step (e.g. "Audio.VolumeUp(<id>,2)").
            var comma = id.IndexOf(',');
            if (comma >= 0) id = id[..comma].Trim();
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

    /// <param name="mutedLabel">The translated "muted" caption the vertical layout writes under a
    /// muted bar; the horizontal layout draws a symbol instead.</param>
    public static void Render(IReadOnlyList<BarView> bars, IRenderCanvas canvas, bool horizontal, string mutedLabel)
    {
        if (horizontal) RenderHorizontal(bars, canvas);
        else RenderVertical(bars, canvas, mutedLabel);
    }

    private static void RenderVertical(IReadOnlyList<BarView> bars, IRenderCanvas canvas, string mutedLabel)
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
            var label = bar.Muted ? mutedLabel : Fit(bar.Name, canvas, labelSize, colW);
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
