using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The pactl event monitor and the caches it keeps fresh.
/// <para>
/// Every pactl call is a process. Without caching, a dial tick cost a GetVolume and a SetVolume
/// (two processes), the devices folder cost 2 + 2N processes every 750 ms, and each strip bar ran
/// its own <c>pactl subscribe</c> that re-read volume and mute on every app-stream change. One
/// shared <c>pactl subscribe</c> process now tells which device or stream changed, so volume, mute,
/// the endpoint lists and the sink-input list are answered from memory until that device reports a
/// change. While the monitor is not running (pactl missing, process died), cached values expire
/// after <see cref="UnmonitoredLifetimeMs"/> instead.
/// </para>
/// </summary>
public sealed partial class LinuxAudioService : IDisposable
{
    /// <summary>How long a cached answer is trusted while no event monitor is running.</summary>
    private const long UnmonitoredLifetimeMs = 500;

    /// <summary>Minimum pause before a died monitor is started again.</summary>
    private const long MonitorRestartDelayMs = 5000;

    // "Event 'change' on sink #42", "Event 'remove' on sink-input #7", "Event 'change' on server"
    private static readonly Regex EventLine =
        new(@"^Event '(\w+)' on ([\w-]+)(?: #(\d+))?", RegexOptions.Compiled);

    private readonly Lock _cacheLock = new();
    private Process? _monitor;
    private CancellationTokenSource? _monitorCts;
    // Not long.MinValue: TickCount64 - long.MinValue overflows to a negative number, which reads
    // as "started just now" and kept the first monitor from ever starting.
    private long _monitorStartedAt = -MonitorRestartDelayMs;
    private bool _disposed;

    /// <summary>Bumped on every event, so a read that raced an event does not store a stale answer.</summary>
    private long _generation;

    private readonly Dictionary<string, CachedLevel> _levels = new(StringComparer.Ordinal);
    private readonly Dictionary<AudioEndpointKind, CachedEndpoints> _endpoints = [];
    private readonly Dictionary<AudioEndpointKind, Dictionary<int, string>> _namesByIndex = [];
    private CachedSinkInputs? _sinkInputs;
    private readonly List<Listener> _listeners = [];
    private readonly DeviceChangeNotifier _deviceChanges = new();

    /// <summary>A device's level. <paramref name="PendingEchoes"/> counts writes of this service
    /// whose change event has not arrived yet; see <see cref="StoreLevel"/>.</summary>
    private sealed record CachedLevel(float Volume, bool Muted, long Stamp, int PendingEchoes = 0);

    /// <summary>How long after its own write a change event still counts as that write's echo.</summary>
    private const long EchoWindowMs = 1000;
    private sealed record CachedEndpoints(IReadOnlyList<AudioEndpointInfo> List, string DefaultName, long Stamp);
    private sealed record CachedSinkInputs(IReadOnlyList<SinkInput> List, long Stamp);

    /// <summary>
    /// True once the monitor has had time to connect. <c>pactl subscribe</c> needs a moment after
    /// the process starts; a change in that gap would never be reported, so until then cached
    /// answers still expire as if no monitor ran.
    /// </summary>
    private bool MonitorRunning =>
        _monitor != null && Environment.TickCount64 - _monitorStartedAt >= UnmonitoredLifetimeMs;

    private bool IsFresh(long stamp) =>
        MonitorRunning || Environment.TickCount64 - stamp < UnmonitoredLifetimeMs;

    // --- endpoint volume and mute -----------------------------------------

    /// <summary>"sink:NAME" / "source:NAME" — the same id whether the caller passed a prefix or not.</summary>
    private static string LevelKey(AudioEndpointKind kind, string name) => $"{Noun(kind)}:{name}";

    private static string Noun(AudioEndpointKind kind) => kind == AudioEndpointKind.Render ? "sink" : "source";

    private CachedLevel Level(string endpointId)
    {
        EnsureMonitor();
        (AudioEndpointKind kind, string name) = SplitId(endpointId);
        string key = LevelKey(kind, name);

        long generation;
        lock (_cacheLock)
        {
            if (_levels.TryGetValue(key, out CachedLevel? cached) && IsFresh(cached.Stamp)) return cached;
            generation = _generation;
        }

        string noun = Noun(kind);
        bool ok = TryRunPactl(out string volumeOutput, $"get-{noun}-volume", name);
        ok &= TryRunPactl(out string muteOutput, $"get-{noun}-mute", name);

        // e.g. "Volume: front-left: 45875 / 70% / -9.62 dB, front-right: 45875 / 70% ..."
        Match volume = Regex.Match(volumeOutput, @"(\d+)%");
        CachedLevel level = new(
            volume.Success
                ? Math.Clamp(int.Parse(volume.Groups[1].Value, CultureInfo.InvariantCulture) / 100f, 0f, 1f)
                : 0f,
            // "Mute: yes" / "Mute: no"
            muteOutput.Trim().EndsWith("yes", StringComparison.OrdinalIgnoreCase),
            Environment.TickCount64);

        // A failed read is answered but not kept: with a running monitor nothing would replace it.
        lock (_cacheLock)
        {
            if (ok && generation == _generation) _levels[key] = level;
        }
        return level;
    }

    /// <summary>
    /// Records a value this service just wrote, so the next read needs no process. The write comes
    /// back as a change event; that echo is expected and keeps the entry, otherwise every dial tick
    /// would pay for a re-read of the value it has just set.
    /// </summary>
    private void StoreLevel(string endpointId, float? volume, bool? muted)
    {
        (AudioEndpointKind kind, string name) = SplitId(endpointId);
        string key = LevelKey(kind, name);

        lock (_cacheLock)
        {
            // Only a known entry is updated: half a level (volume without mute) is not a level.
            if (!_levels.TryGetValue(key, out CachedLevel? cached)) return;

            // PulseAudio posts no change event for a write that changes nothing (a dial turned
            // further at 100 %), so such a write must not wait for an echo: it would swallow the
            // next outside change as its own.
            if ((volume == null || Math.Abs(volume.Value - cached.Volume) < 0.005f) &&
                (muted == null || muted.Value == cached.Muted))
            {
                return;
            }

            _levels[key] = cached with
            {
                Volume = volume ?? cached.Volume,
                Muted = muted ?? cached.Muted,
                Stamp = Environment.TickCount64,
                PendingEchoes = cached.PendingEchoes + 1
            };
        }
    }

    // --- endpoint lists ---------------------------------------------------

    private CachedEndpoints Endpoints(AudioEndpointKind kind)
    {
        EnsureMonitor();

        long generation;
        lock (_cacheLock)
        {
            if (_endpoints.TryGetValue(kind, out CachedEndpoints? cached) && IsFresh(cached.Stamp)) return cached;
            generation = _generation;
        }

        // A failed default lookup reads as "no default"; the list is still answered but not kept,
        // or every @default binding would do nothing until the next device event.
        bool defaultOk = TryRunPactl(out string defaultOutput, $"get-default-{Noun(kind)}");
        string defaultName = defaultOutput.Trim();

        // A pactl call that failed or timed out (a stuck PulseAudio socket, say) reads as "no
        // devices", which would empty every menu and folder for one refresh. Keep the last list.
        if (!TryRunPactl(out string listOutput, "list", kind == AudioEndpointKind.Render ? "sinks" : "sources"))
        {
            lock (_cacheLock)
            {
                return _endpoints.TryGetValue(kind, out CachedEndpoints? last)
                    ? last
                    : new CachedEndpoints([], defaultName, long.MinValue);
            }
        }

        CachedEndpoints endpoints = new(ParseEndpoints(listOutput, defaultName, kind), defaultName,
            Environment.TickCount64);

        lock (_cacheLock)
        {
            if (defaultOk && generation == _generation) _endpoints[kind] = endpoints;
        }
        return endpoints;
    }

    // --- sink inputs ------------------------------------------------------

    private IReadOnlyList<SinkInput> SinkInputs()
    {
        EnsureMonitor();

        long generation;
        lock (_cacheLock)
        {
            if (_sinkInputs != null && IsFresh(_sinkInputs.Stamp)) return _sinkInputs.List;
            generation = _generation;
        }

        bool ok = TryRunPactl(out string output, "list", "sink-inputs");
        IReadOnlyList<SinkInput> inputs = ParseSinkInputs(output);

        lock (_cacheLock)
        {
            if (ok && generation == _generation) _sinkInputs = new CachedSinkInputs(inputs, Environment.TickCount64);
        }
        return inputs;
    }

    /// <summary>Drops the sink-input list after this service changed a stream.</summary>
    private void InvalidateSinkInputs()
    {
        lock (_cacheLock)
        {
            _sinkInputs = null;
            _generation++;
        }
    }

    // --- change listeners ---------------------------------------------------

    /// <summary>One <see cref="IAudioService.SubscribeVolumeChanges"/> registration.</summary>
    private sealed class Listener(LinuxAudioService owner, string key, Action<float, bool> onChange) : IDisposable
    {
        public string Key { get; } = key;
        public Action<float, bool> OnChange { get; } = onChange;

        public void Dispose()
        {
            lock (owner._cacheLock) owner._listeners.Remove(this);
        }
    }

    private IDisposable AddListener(string endpointId, Action<float, bool> onChange)
    {
        EnsureMonitor();
        (AudioEndpointKind kind, string name) = SplitId(endpointId);

        Listener listener = new(this, LevelKey(kind, name), onChange);
        lock (_cacheLock) _listeners.Add(listener);
        return listener;
    }

    public IDisposable SubscribeDeviceChanges(Action onChange)
    {
        // The events come from the monitor, so a subscriber needs it running even before
        // anything has read a volume.
        EnsureMonitor();
        return _deviceChanges.Subscribe(onChange);
    }

    // --- the monitor ------------------------------------------------------

    /// <summary>Starts the shared <c>pactl subscribe</c> process unless it runs, or died too recently.</summary>
    private void EnsureMonitor()
    {
        lock (_cacheLock)
        {
            if (_disposed || _monitor != null) return;
            if (Environment.TickCount64 - _monitorStartedAt < MonitorRestartDelayMs) return;
            _monitorStartedAt = Environment.TickCount64;

            Process process;
            try
            {
                process = Process.Start(PactlStartInfo("subscribe"))!;
            }
            catch
            {
                return;
            }

            CancellationTokenSource cts = new();
            _monitor = process;
            _monitorCts = cts;

            // stderr is never read; draining it keeps a chatty pactl from blocking on a full pipe.
            _ = process.StandardError.ReadToEndAsync(cts.Token);
            _ = Task.Run(() => ReadEvents(process, cts.Token));
        }
    }

    private async Task ReadEvents(Process process, CancellationToken token)
    {
        try
        {
            StreamReader reader = process.StandardOutput;
            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null) break;
                HandleEvent(line);
            }
        }
        catch
        {
            // Cancelled on dispose, or the process went away.
        }
        finally
        {
            StopMonitor(process);
        }
    }

    /// <summary>Forgets a monitor process. Everything cached on its word is dropped too, since
    /// events may have been missed; the next query starts a new monitor.</summary>
    private void StopMonitor(Process process)
    {
        CancellationTokenSource? cts = null;
        lock (_cacheLock)
        {
            if (!ReferenceEquals(_monitor, process)) return;
            _monitor = null;
            cts = _monitorCts;
            _monitorCts = null;
            ClearCaches();
        }

        // Changes may have gone unreported while the monitor was dying; whoever shows a device
        // re-reads it, which also starts the next monitor.
        _deviceChanges.Raise();

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        process.Dispose();
        cts?.Dispose();
    }

    public void RefreshDevices()
    {
        lock (_cacheLock) ClearCaches();
    }

    private void ClearCaches()
    {
        _levels.Clear();
        _endpoints.Clear();
        _namesByIndex.Clear();
        _sinkInputs = null;
        _generation++;
    }

    private void HandleEvent(string line)
    {
        Match match = EventLine.Match(line);
        if (!match.Success) return;

        string type = match.Groups[1].Value;
        string facility = match.Groups[2].Value;

        switch (facility)
        {
            case "sink-input":
                InvalidateSinkInputs();
                return;

            case "server":
                // The default sink or source moved: every list's IsDefault may be wrong now.
                lock (_cacheLock)
                {
                    _endpoints.Clear();
                    _generation++;
                }
                _deviceChanges.Raise();
                return;

            case "sink":
            case "source":
                break;

            default:
                // card, client, module, sample-cache, source-output: nothing cached depends on them.
                return;
        }

        AudioEndpointKind kind = facility == "sink" ? AudioEndpointKind.Render : AudioEndpointKind.Capture;
        if (!int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            return;

        if (type == "new")
        {
            // A device came: the list and the index map are out of date.
            lock (_cacheLock)
            {
                _endpoints.Remove(kind);
                _namesByIndex.Remove(kind);
                _generation++;
            }
            _deviceChanges.Raise();
            return;
        }

        // Looked up before a removal clears the map, so a removed device still has its name here.
        string? name = NameOfIndex(kind, index);

        if (type == "remove") _deviceChanges.Raise();

        List<Listener> notify;
        CachedLevel? echo = null;
        lock (_cacheLock)
        {
            _generation++;
            if (type == "remove")
            {
                _endpoints.Remove(kind);
                _namesByIndex.Remove(kind);
            }

            if (name == null)
            {
                // Unknown device: drop every level of that kind rather than keep a stale one.
                string prefix = Noun(kind) + ":";
                foreach (string key in _levels.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    _levels.Remove(key);
                return;
            }

            string levelKey = LevelKey(kind, name);
            if (type == "remove")
            {
                _levels.Remove(levelKey);
                return;
            }

            if (_levels.TryGetValue(levelKey, out CachedLevel? cached) && cached.PendingEchoes > 0 &&
                Environment.TickCount64 - cached.Stamp < EchoWindowMs)
            {
                // Our own write coming back: the cached value already is the new one.
                echo = cached with { PendingEchoes = cached.PendingEchoes - 1 };
                _levels[levelKey] = echo;
            }
            else
            {
                _levels.Remove(levelKey);
            }

            // Only the listeners of exactly this device: a change on an app stream or on another
            // device is not theirs to re-read.
            notify = _listeners.Where(l => string.Equals(l.Key, levelKey, StringComparison.Ordinal)).ToList();
        }

        if (notify.Count == 0) return;

        // One read serves every listener of the device: the first fills the cache.
        CachedLevel level;
        try { level = echo ?? Level(LevelKey(kind, name)); }
        catch { return; }

        foreach (Listener listener in notify)
        {
            try { listener.OnChange(level.Volume, level.Muted); }
            catch { /* a failing listener must not stop the monitor */ }
        }
    }

    /// <summary>The name of the sink or source with that index, from <c>pactl list short</c>,
    /// re-read when the index is not known yet.</summary>
    private string? NameOfIndex(AudioEndpointKind kind, int index)
    {
        lock (_cacheLock)
        {
            if (_namesByIndex.TryGetValue(kind, out Dictionary<int, string>? known) &&
                known.TryGetValue(index, out string? name))
            {
                return name;
            }
        }

        // "47\talsa_output.pci-0000_00_1f.3.analog-stereo\tmodule-alsa-card.c\ts16le 2ch 48000Hz\tRUNNING"
        Dictionary<int, string> names = [];
        string list = RunPactl("list", "short", kind == AudioEndpointKind.Render ? "sinks" : "sources");
        foreach (string row in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = row.Split('\t');
            if (columns.Length >= 2 &&
                int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowIndex))
            {
                names[rowIndex] = columns[1].Trim();
            }
        }

        lock (_cacheLock) _namesByIndex[kind] = names;
        return names.GetValueOrDefault(index);
    }

    /// <summary>Stops the event monitor. Called by the plugin on shutdown.</summary>
    public void Dispose()
    {
        Process? monitor;
        lock (_cacheLock)
        {
            _disposed = true;
            monitor = _monitor;
            _listeners.Clear();
        }

        if (monitor != null) StopMonitor(monitor);
    }
}
