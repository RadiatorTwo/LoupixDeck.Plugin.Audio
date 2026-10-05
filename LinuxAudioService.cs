using LoupixDeck.PluginSdk;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Linux implementation backed by <c>pactl</c>. Works on PulseAudio and on PipeWire
/// systems via the pipewire-pulse compatibility layer.
/// </summary>
public sealed class LinuxAudioService : IAudioService
{
    private static readonly ToolProbe HasPactl = new("pactl", "--version");
    private static readonly ToolProbe HasPaplay = new("paplay", "--version");
    private static readonly ToolProbe HasFfplay = new("ffplay", "-version");
    private static readonly ToolProbe HasMpv = new("mpv", "--version");
    private static readonly ToolProbe HasFfmpeg = new("ffmpeg", "-version");

    private static readonly Lazy<bool> HasXprop = new(() => DetectTool("xprop", "-version"));

    private readonly List<Playback> _playbacks = [];
    private readonly Lock _playbackLock = new();

    /// <summary>How long a resolved foreground app stays valid. Long enough to keep the dial's
    /// render path off xprop on every frame, short enough that an alt-tab shows up at once.</summary>
    private const long ForegroundCacheMs = 400;

    private readonly Lock _foregroundLock = new();
    private string? _foregroundAppId;
    private long _foregroundStamp = long.MinValue;

    public bool IsSupported => HasPactl.Value;

    /// <summary>
    /// The tools this backend shells out to. Probes again on every call rather than trusting the
    /// first answer, so a package installed while LoupixDeck is running reads as met on the next
    /// check. Note that commands are only registered at load: after installing pactl the plugin
    /// still needs a restart to offer them, which the hint says.
    /// </summary>
    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        bool pactl = HasPactl.Refresh();
        bool paplay = HasPaplay.Refresh();
        bool mpv = HasMpv.Refresh();
        bool ffplay = HasFfplay.Refresh();
        HasFfmpeg.Refresh();

        return
        [
            new PluginRequirement
            {
                Id = "pactl",
                Name = "pactl (pulseaudio-utils)",
                IsMet = pactl,
                Message = "pactl is missing, so the Audio plugin has no commands.",
                InstallHint = "Install the package pulseaudio-utils (Arch Linux: libpulse), then restart LoupixDeck."
            },
            new PluginRequirement
            {
                Id = "sound-player",
                Name = "Sound player (paplay, mpv or ffplay)",
                IsMet = paplay || mpv || ffplay,
                Message = "No sound player was found, so sounds cannot be played.",
                InstallHint = "Install pulseaudio-utils (paplay) or mpv. For mp3 and m4a files mpv, "
                              + "or ffmpeg together with paplay, is needed."
            }
        ];
    }

    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind)
    {
        if (!IsSupported) return [];

        var listNoun = kind == AudioEndpointKind.Render ? "sinks" : "sources";
        var defaultNoun = kind == AudioEndpointKind.Render ? "sink" : "source";

        var defaultName = RunPactl($"get-default-{defaultNoun}").Trim();

        // A pactl call that failed or timed out (a stuck PulseAudio socket, say) reads as "no
        // devices", which would empty every menu and folder for one refresh. Keep the last list.
        if (!TryRunPactl(out string listOutput, "list", listNoun))
        {
            lock (_lastEndpointsLock)
                return _lastEndpoints.TryGetValue(kind, out IReadOnlyList<AudioEndpointInfo>? last) ? last : [];
        }

        IReadOnlyList<AudioEndpointInfo> endpoints = ParseEndpoints(listOutput, defaultName, kind);
        lock (_lastEndpointsLock) _lastEndpoints[kind] = endpoints;
        return endpoints;
    }

    private readonly Lock _lastEndpointsLock = new();
    private readonly Dictionary<AudioEndpointKind, IReadOnlyList<AudioEndpointInfo>> _lastEndpoints = [];

    public string? GetDefaultEndpointId(AudioEndpointKind kind)
    {
        if (!IsSupported) return null;

        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        var name = RunPactl($"get-default-{noun}").Trim();
        if (string.IsNullOrEmpty(name)) return null;

        // Same "sink:NAME" / "source:NAME" encoding GetEndpoints reports, so the id is
        // interchangeable with a bound one.
        return $"{noun}:{name}";
    }

    public float GetVolume(string endpointId)
    {
        if (!IsSupported) return 0f;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        var output = RunPactl($"get-{noun}-volume", name);
        // e.g. "Volume: front-left: 45875 / 70% / -9.62 dB, front-right: 45875 / 70% ..."
        var m = Regex.Match(output, @"(\d+)%");
        return m.Success
            ? Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100f, 0f, 1f)
            : 0f;
    }

    public void SetVolume(string endpointId, float scalar01)
    {
        if (!IsSupported) return;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        var pct = (int)Math.Round(Math.Clamp(scalar01, 0f, 1f) * 100f);
        RunPactl($"set-{noun}-volume", name, $"{pct.ToString(CultureInfo.InvariantCulture)}%");
    }

    public bool GetMute(string endpointId)
    {
        if (!IsSupported) return false;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        var output = RunPactl($"get-{noun}-mute", name).Trim();
        // "Mute: yes" / "Mute: no"
        return output.EndsWith("yes", StringComparison.OrdinalIgnoreCase);
    }

    public void SetMute(string endpointId, bool muted)
    {
        if (!IsSupported) return;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        RunPactl($"set-{noun}-mute", name, muted ? "1" : "0");
    }

    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        if (!IsSupported) return EmptyDisposable.Instance;

        var (kind, _) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";

        Process proc;
        try
        {
            proc = Process.Start(PactlStartInfo("subscribe"))!;
        }
        catch
        {
            return EmptyDisposable.Instance;
        }

        var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                var reader = proc.StandardOutput;
                while (!cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (line == null) break;
                    // pactl subscribe output: "Event 'change' on sink #42"
                    if (line.Contains($"on {noun}", StringComparison.Ordinal) &&
                        line.Contains("change", StringComparison.Ordinal))
                    {
                        try { onChange(GetVolume(endpointId), GetMute(endpointId)); }
                        catch { /* swallow callback failure */ }
                    }
                }
            }
            catch { /* process killed on dispose */ }
        }, cts.Token);

        return new Subscription(proc, cts);
    }

    public void PlayFile(string filePath, string? endpointId)
    {
        string? sink = string.IsNullOrWhiteSpace(endpointId)
            ? null
            : SplitId(endpointId).Name;

        // paplay goes through libsndfile (wav/flac/ogg) and cannot decode mp3/m4a.
        string extension = Path.GetExtension(filePath);
        bool needsDecoder =
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

        Process process =
            (!needsDecoder && HasPaplay.Value ? StartPaplay(filePath, sink) : null)
            ?? StartDecoder(filePath, sink)
            ?? throw new InvalidOperationException(sink == null
                ? $"No player available for '{filePath}'. Install pulseaudio-utils (paplay), ffmpeg (ffplay) or mpv."
                : $"No player able to target the device '{sink}' for '{filePath}'. "
                  + "Install mpv, or ffmpeg together with pulseaudio-utils (paplay).");

        Playback playback = new(process, filePath);
        lock (_playbackLock) _playbacks.Add(playback);

        // Reap the entry once the sound ends, without blocking the caller. Order matters:
        // the handler is attached first, and EnableRaisingEvents also fires for a process
        // that has already exited — so the entry cannot leak.
        process.Exited += (_, _) =>
        {
            lock (_playbackLock) _playbacks.Remove(playback);
            process.Dispose();
        };
        process.EnableRaisingEvents = true;
    }

    public void StopAllPlayback()
    {
        Playback[] running;
        lock (_playbackLock)
        {
            running = [.. _playbacks];
            _playbacks.Clear();
        }

        foreach (Playback playback in running) Kill(playback);
    }

    public bool StopFile(string filePath)
    {
        Playback[] matching;
        lock (_playbackLock)
        {
            matching = [.. _playbacks.Where(p =>
                string.Equals(p.FilePath, filePath, StringComparison.Ordinal))];
            foreach (Playback playback in matching) _playbacks.Remove(playback);
        }

        foreach (Playback playback in matching) Kill(playback);
        return matching.Length > 0;
    }

    /// <summary>The player process of one running sound, and the file it was started with.</summary>
    private readonly record struct Playback(Process Process, string FilePath);

    private static void Kill(Playback playback)
    {
        // The decoders spawn no children, but paplay under a wrapper might — killing the
        // tree keeps a stray child from holding the sink open.
        try { if (!playback.Process.HasExited) playback.Process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        playback.Process.Dispose();
    }

    /// <summary>
    /// <paramref name="endpointId"/> is accepted and ignored: pactl lists every sink input
    /// regardless of its sink, and filtering by sink would hide exactly the streams a user
    /// wants to turn down when they are playing on another device.
    /// </summary>
    public IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId)
    {
        if (!IsSupported) return [];

        Dictionary<string, AudioSessionInfo> byApp = new(StringComparer.Ordinal);
        foreach (SinkInput input in ParseSinkInputs(RunPactl("list", "sink-inputs")))
        {
            if (input.AppId.Length == 0) continue;

            // Several streams per app: keep the loudest, and count the app as muted
            // only when every one of its streams is.
            if (byApp.TryGetValue(input.AppId, out AudioSessionInfo? existing))
            {
                byApp[input.AppId] = existing with
                {
                    Volume = Math.Max(existing.Volume, input.Volume),
                    Muted = existing.Muted && input.Muted
                };
            }
            else
            {
                byApp[input.AppId] = new AudioSessionInfo(
                    input.AppId, input.DisplayName, input.Volume, input.Muted);
            }
        }

        return [.. byApp.Values.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public float? GetSessionVolume(string? endpointId, string appId)
    {
        if (!IsSupported) return null;

        float? result = null;
        foreach (SinkInput input in ParseSinkInputs(RunPactl("list", "sink-inputs")))
        {
            if (!string.Equals(input.AppId, appId, StringComparison.Ordinal)) continue;
            result = result is { } current ? Math.Max(current, input.Volume) : input.Volume;
        }
        return result;
    }

    public void SetSessionVolume(string? endpointId, string appId, float scalar01)
    {
        if (!IsSupported) return;

        int percent = (int)Math.Round(Math.Clamp(scalar01, 0f, 1f) * 100f);
        foreach (SinkInput input in ParseSinkInputs(RunPactl("list", "sink-inputs")))
        {
            if (string.Equals(input.AppId, appId, StringComparison.Ordinal))
                RunPactl("set-sink-input-volume", input.Index.ToString(CultureInfo.InvariantCulture),
                    $"{percent.ToString(CultureInfo.InvariantCulture)}%");
        }
    }

    public bool? GetSessionMute(string? endpointId, string appId)
    {
        if (!IsSupported) return null;

        bool? result = null;
        foreach (SinkInput input in ParseSinkInputs(RunPactl("list", "sink-inputs")))
        {
            if (!string.Equals(input.AppId, appId, StringComparison.Ordinal)) continue;
            result = result is { } current ? current && input.Muted : input.Muted;
        }
        return result;
    }

    public void SetSessionMute(string? endpointId, string appId, bool muted)
    {
        if (!IsSupported) return;

        foreach (SinkInput input in ParseSinkInputs(RunPactl("list", "sink-inputs")))
        {
            if (string.Equals(input.AppId, appId, StringComparison.Ordinal))
                RunPactl("set-sink-input-mute", input.Index.ToString(CultureInfo.InvariantCulture), muted ? "1" : "0");
        }
    }

    /// <summary>
    /// Resolves the application owning the focused window through X11 (<c>xprop</c>), the same
    /// mechanism the host uses for app-focus page switching, so it covers X11 and XWayland
    /// sessions. Returns null on a pure Wayland session (no <c>DISPLAY</c>), without xprop, or
    /// when the focused window exposes no PID.
    /// <para>
    /// The result is cached for <see cref="ForegroundCacheMs"/> because the dial reads it on the
    /// render path: without the cache every frame would spawn two xprop processes.
    /// </para>
    /// </summary>
    public string? GetForegroundAppId()
    {
        lock (_foregroundLock)
        {
            long now = Environment.TickCount64;
            if (now - _foregroundStamp < ForegroundCacheMs) return _foregroundAppId;

            _foregroundStamp = now;
            _foregroundAppId = ResolveForegroundAppId();
            return _foregroundAppId;
        }
    }

    private string? ResolveForegroundAppId()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        if (!HasXprop.Value) return null;

        Match window = Regex.Match(RunTool("xprop", "-root", "_NET_ACTIVE_WINDOW"), @"window id # (0x[0-9a-fA-F]+)");
        if (!window.Success) return null;

        string windowId = window.Groups[1].Value;
        // 0x0 is the "no active window" answer, not a window to query.
        if (string.Equals(windowId, "0x0", StringComparison.OrdinalIgnoreCase)) return null;

        Match pidMatch = Regex.Match(RunTool("xprop", "-id", windowId, "_NET_WM_PID"), @"_NET_WM_PID\(CARDINAL\) = (\d+)");
        if (!pidMatch.Success) return null;
        if (!int.TryParse(pidMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            return null;

        IReadOnlyList<SinkInput> inputs = ParseSinkInputs(RunPactl("list", "sink-inputs"));

        // The stream's own PID is the exact answer whenever the window's process (or one of its
        // ancestors, which is how a browser tab's window maps onto the process that plays the
        // sound) owns a stream. This also survives a mismatch between the window's executable
        // name and the one PulseAudio reports for the stream.
        for (int walk = pid, depth = 0; walk > 1 && depth < 8; walk = ParentPid(walk), depth++)
        {
            foreach (SinkInput input in inputs)
                if (input.Pid == walk && input.AppId.Length > 0) return input.AppId;
        }

        // No stream yet: the executable name still identifies the app, so a dial bound to the
        // foreground sentinel names the right target once that app starts playing.
        string? binary = ProcessBinaryName(pid);
        return string.IsNullOrEmpty(binary) ? null : binary;
    }

    /// <summary>Executable name of a process, lowercased and without extension, matching the
    /// AppId <see cref="ParseSinkInputs"/> derives from <c>application.process.binary</c>.</summary>
    private static string? ProcessBinaryName(int pid)
    {
        string dir = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            // The exe symlink carries the full name; comm is truncated at 15 characters, so it
            // is only the fallback for a process whose exe link cannot be read.
            string? target = File.ResolveLinkTarget($"{dir}/exe", returnFinalTarget: false)?.Name;
            if (!string.IsNullOrEmpty(target))
                return Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        }
        catch { /* permission denied or process gone */ }

        try
        {
            string comm = File.ReadAllText($"{dir}/comm").Trim();
            return comm.Length == 0 ? null : Path.GetFileNameWithoutExtension(comm).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>PPid from <c>/proc/&lt;pid&gt;/stat</c>, or 0 when it cannot be read.</summary>
    private static int ParentPid(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            // Field 4 is the PPid, but field 2 (comm) may contain spaces and parentheses, so
            // parsing starts after its closing parenthesis.
            int close = stat.LastIndexOf(')');
            if (close < 0) return 0;

            string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int ppid) ? ppid : 0;
        }
        catch { return 0; }
    }

    public bool SetDefaultEndpoint(string endpointId)
    {
        if (!IsSupported) return false;

        (AudioEndpointKind kind, string name) = SplitId(endpointId);
        string noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        RunPactl($"set-default-{noun}", name);

        // pactl leaves already-running streams on the old device, which reads as "nothing
        // happened" to the user. Move them across too.
        if (kind == AudioEndpointKind.Render)
        {
            foreach (SinkInput input in ParseSinkInputs(RunPactl("list", "sink-inputs")))
                RunPactl("move-sink-input", input.Index.ToString(CultureInfo.InvariantCulture), name);
        }

        string current = RunPactl($"get-default-{noun}").Trim();
        return string.Equals(current, name, StringComparison.Ordinal);
    }

    /// <summary>One parsed "pactl list sink-inputs" block.</summary>
    private readonly record struct SinkInput(
        int Index, string AppId, string DisplayName, float Volume, bool Muted, int Pid);

    private static IReadOnlyList<SinkInput> ParseSinkInputs(string pactlList)
    {
        List<SinkInput> result = [];

        foreach (string block in pactlList.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Match indexMatch = Regex.Match(block, @"Sink Input #(\d+)");
            if (!indexMatch.Success) continue;

            // application.process.binary is the executable name, which is the stable
            // identity used in saved bindings; application.name is only for display.
            string binary = Regex.Match(block,
                @"application\.process\.binary\s*=\s*""([^""]+)""").Groups[1].Value;
            string appName = Regex.Match(block,
                @"application\.name\s*=\s*""([^""]+)""").Groups[1].Value;

            Match volumeMatch = Regex.Match(block, @"Volume:[^\n]*?(\d+)%");
            float volume = volumeMatch.Success
                ? Math.Clamp(int.Parse(volumeMatch.Groups[1].Value, CultureInfo.InvariantCulture) / 100f, 0f, 1f)
                : 0f;

            bool muted = Regex.Match(block, @"Mute:\s*(\w+)").Groups[1].Value
                .Equals("yes", StringComparison.OrdinalIgnoreCase);

            string appId = Path.GetFileNameWithoutExtension(binary).ToLowerInvariant();

            // The stream's own PID, used to match a stream against the foreground window's
            // process. Absent for a stream that reports no process, which parses as 0.
            _ = int.TryParse(Regex.Match(block, @"application\.process\.id\s*=\s*""(\d+)""").Groups[1].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid);

            result.Add(new SinkInput(
                int.Parse(indexMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                appId,
                string.IsNullOrEmpty(appName) ? appId : appName,
                volume,
                muted,
                pid));
        }

        return result;
    }

    private static Process? StartPaplay(string filePath, string? sink)
    {
        ProcessStartInfo psi = new("paplay") { UseShellExecute = false, CreateNoWindow = true };
        if (!string.IsNullOrEmpty(sink)) psi.ArgumentList.Add($"--device={sink}");
        psi.ArgumentList.Add(filePath);

        try { return Process.Start(psi); }
        catch { return null; }
    }

    /// <summary>
    /// Plays the formats paplay cannot decode. When a device is requested, only players
    /// that take it as an explicit argument are used — never PULSE_SINK: newer SDL builds
    /// (and therefore ffplay) ask the server for the default sink and pass it to libpulse
    /// themselves, which overrides that variable and lands the sound on the default device.
    /// Rather than play on the wrong device, nothing is started when no such player exists;
    /// the caller turns that into a log entry.
    /// </summary>
    private static Process? StartDecoder(string filePath, string? sink)
    {
        // paplay first: its --device is the selection this plugin already relies on for the
        // formats it can decode itself, so it is the one known to hold on this platform.
        // mpv only starts successfully with a device it accepted, but a build without the
        // pulse output would still start and play elsewhere, so it goes second.
        if (!string.IsNullOrEmpty(sink))
            return StartFfmpegToPaplay(filePath, sink) ?? StartMpv(filePath, sink);

        return StartFfplay(filePath) ?? StartMpv(filePath, null);
    }

    /// <summary>ffplay takes no device argument, so it is only used for the default device.</summary>
    private static Process? StartFfplay(string filePath)
    {
        if (!HasFfplay.Value) return null;

        ProcessStartInfo psi = new("ffplay") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-nodisp");
        psi.ArgumentList.Add("-autoexit");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add(filePath);

        try { return Process.Start(psi); }
        catch { return null; }
    }

    private static Process? StartMpv(string filePath, string? sink)
    {
        if (!HasMpv.Value) return null;

        ProcessStartInfo psi = new("mpv") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--really-quiet");
        // "pulse/<sink>" picks mpv's PulseAudio output plus the device. Without it mpv uses
        // its native pipewire output, which has its own device naming and no PULSE_SINK.
        if (!string.IsNullOrEmpty(sink)) psi.ArgumentList.Add($"--audio-device=pulse/{sink}");
        psi.ArgumentList.Add(filePath);

        try { return Process.Start(psi); }
        catch { return null; }
    }

    /// <summary>
    /// Decodes with ffmpeg and lets paplay do the output, because paplay's --device is the
    /// one device selection on this platform that is not negotiable by the client library.
    /// Raw s16le keeps the pipe seek-free, which a wav header would not.
    /// </summary>
    private static Process? StartFfmpegToPaplay(string filePath, string sink)
    {
        if (!HasFfmpeg.Value || !HasPaplay.Value) return null;

        string command =
            $"ffmpeg -v quiet -i {ShellQuote(filePath)} -f s16le -ar 48000 -ac 2 - "
            + $"| paplay --raw --rate=48000 --channels=2 --format=s16le --device={ShellQuote(sink)}";

        ProcessStartInfo psi = new("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        // The shell is the tracked process, so stopping this playback has to kill the
        // process tree — which is what Kill(entireProcessTree: true) does for every entry.
        try { return Process.Start(psi); }
        catch { return null; }
    }

    /// <summary>Single-quotes a value for /bin/sh, closing and reopening around any quote.</summary>
    private static string ShellQuote(string value) => $"'{value.Replace("'", @"'\''")}'";

    // --- helpers ---------------------------------------------------------

    private static (AudioEndpointKind Kind, string Name) SplitId(string id)
    {
        // IDs are encoded as "sink:NAME" / "source:NAME" so the kind is recoverable.
        var idx = id.IndexOf(':');
        if (idx < 0) return (AudioEndpointKind.Render, id);
        var kind = id[..idx] == "source" ? AudioEndpointKind.Capture : AudioEndpointKind.Render;
        return (kind, id[(idx + 1)..]);
    }

    private static IReadOnlyList<AudioEndpointInfo> ParseEndpoints(
        string pactlList, string defaultName, AudioEndpointKind kind)
    {
        var prefix = kind == AudioEndpointKind.Render ? "sink" : "source";
        var result = new List<AudioEndpointInfo>();

        // Blocks separated by blank lines; each block has "Name: ..." and "Description: ..."
        foreach (var block in pactlList.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Regex.Match(block, @"^\s*Name:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var desc = Regex.Match(block, @"^\s*Description:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            // Filter out monitor sources (capture mirrors of sinks) — noise for the user.
            if (kind == AudioEndpointKind.Capture && name.EndsWith(".monitor", StringComparison.Ordinal))
                continue;

            result.Add(new AudioEndpointInfo(
                Id: $"{prefix}:{name}",
                FriendlyName: string.IsNullOrEmpty(desc) ? name : desc,
                IsDefault: name == defaultName));
        }
        return result;
    }

    /// <summary>How long a one-shot tool call may take before it is killed.</summary>
    private const int ToolTimeoutMs = 2000;

    /// <summary>Runs a tool and returns its stdout, or an empty string when it cannot run.</summary>
    private static string RunTool(string fileName, params string[] args)
    {
        ProcessStartInfo psi = new(fileName);
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        TryRun(psi, out string stdout);
        return stdout;
    }

    /// <summary>
    /// Runs a process to completion and hands back its stdout. Returns false when it could not
    /// start, ran longer than <see cref="ToolTimeoutMs"/> (it is killed then) or exited non-zero.
    /// <para>
    /// stdout and stderr are drained asynchronously while the timeout runs. Reading stdout to the end
    /// first would block until the process exits, so a stuck PulseAudio or PipeWire socket would hang
    /// the caller for good; and an unread stderr can fill its pipe and stall the process itself.
    /// </para>
    /// </summary>
    private static bool TryRun(ProcessStartInfo psi, out string stdout)
    {
        stdout = string.Empty;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        try
        {
            using Process proc = Process.Start(psi)!;
            Task<string> output = proc.StandardOutput.ReadToEndAsync();
            Task<string> error = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(ToolTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch { /* exited in the meantime */ }
                return false;
            }

            // Both pipes close with the process, so the reads finish right after it.
            if (!Task.WaitAll([output, error], ToolTimeoutMs)) return false;

            stdout = output.Result;
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A pactl invocation pinned to the C locale. pactl translates its output through gettext,
    /// and every parser here matches the English text ("Mute: yes", "Name:", "Event 'change'
    /// on sink"): on a German system mute reads back as "Stumm: ja", so it always parsed as
    /// unmuted and a toggle could never unmute (LoupixDeck#297).
    /// </summary>
    private static ProcessStartInfo PactlStartInfo(params string[] args)
    {
        // ArgumentList passes every value as one argument as-is, so a device name with spaces or
        // quotes cannot break the command line the way an interpolated string could.
        ProcessStartInfo psi = new("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["LC_ALL"] = "C";
        return psi;
    }

    /// <summary>Runs pactl and returns its stdout; empty when it failed or timed out.</summary>
    private static string RunPactl(params string[] args)
    {
        TryRun(PactlStartInfo(args), out string stdout);
        return stdout;
    }

    /// <summary>Runs pactl and reports whether it succeeded, for callers that must tell a failed
    /// call from an empty answer.</summary>
    private static bool TryRunPactl(out string stdout, params string[] args) =>
        TryRun(PactlStartInfo(args), out stdout);

    /// <summary>Probes whether a CLI tool is installed and runnable.</summary>
    private static bool DetectTool(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            // Nobody reads the redirected pipes, but a --version answer fits easily into them.
            if (!p.WaitForExit(1000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { /* exited in the meantime */ }
                return false;
            }

            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A CLI tool that is probed on first use and can be probed again on demand.</summary>
    private sealed class ToolProbe(string fileName, string arguments)
    {
        private volatile int _state; // 0 = not probed yet, 1 = present, 2 = absent

        public bool Value
        {
            get
            {
                int state = _state;
                if (state == 0)
                {
                    state = DetectTool(fileName, arguments) ? 1 : 2;
                    _state = state;
                }

                return state == 1;
            }
        }

        /// <summary>Forgets the earlier answer and probes the tool again.</summary>
        public bool Refresh()
        {
            _state = 0;
            return Value;
        }
    }

    private sealed class Subscription(Process proc, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            proc.Dispose();
            cts.Dispose();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
