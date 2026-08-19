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
    private static readonly Lazy<bool> HasPactl = new(DetectPactl);
    private static readonly Lazy<bool> HasPaplay = new(() => DetectTool("paplay", "--version"));
    private static readonly Lazy<bool> HasFfplay = new(() => DetectTool("ffplay", "-version"));
    private static readonly Lazy<bool> HasMpv = new(() => DetectTool("mpv", "--version"));

    private readonly List<Process> _playbacks = [];
    private readonly Lock _playbackLock = new();

    public bool IsSupported => HasPactl.Value;

    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind)
    {
        if (!IsSupported) return [];

        var listNoun = kind == AudioEndpointKind.Render ? "sinks" : "sources";
        var defaultNoun = kind == AudioEndpointKind.Render ? "sink" : "source";

        var defaultName = RunPactl($"get-default-{defaultNoun}").Trim();
        var listOutput = RunPactl($"list {listNoun}");

        return ParseEndpoints(listOutput, defaultName, kind);
    }

    public float GetVolume(string endpointId)
    {
        if (!IsSupported) return 0f;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        var output = RunPactl($"get-{noun}-volume \"{name}\"");
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
        RunPactl($"set-{noun}-volume \"{name}\" {pct.ToString(CultureInfo.InvariantCulture)}%");
    }

    public bool GetMute(string endpointId)
    {
        if (!IsSupported) return false;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        var output = RunPactl($"get-{noun}-mute \"{name}\"").Trim();
        // "Mute: yes" / "Mute: no"
        return output.EndsWith("yes", StringComparison.OrdinalIgnoreCase);
    }

    public void SetMute(string endpointId, bool muted)
    {
        if (!IsSupported) return;
        var (kind, name) = SplitId(endpointId);
        var noun = kind == AudioEndpointKind.Render ? "sink" : "source";
        RunPactl($"set-{noun}-mute \"{name}\" {(muted ? "1" : "0")}");
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
            var psi = new ProcessStartInfo("pactl", "subscribe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc = Process.Start(psi)!;
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
            ?? throw new InvalidOperationException(
                $"No player available for '{filePath}'. Install pulseaudio-utils (paplay), ffmpeg (ffplay) or mpv.");

        lock (_playbackLock) _playbacks.Add(process);

        // Reap the entry once the sound ends, without blocking the caller. Order matters:
        // the handler is attached first, and EnableRaisingEvents also fires for a process
        // that has already exited — so the entry cannot leak.
        process.Exited += (_, _) =>
        {
            lock (_playbackLock) _playbacks.Remove(process);
            process.Dispose();
        };
        process.EnableRaisingEvents = true;
    }

    public void StopAllPlayback()
    {
        Process[] running;
        lock (_playbackLock)
        {
            running = [.. _playbacks];
            _playbacks.Clear();
        }

        foreach (Process process in running)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            process.Dispose();
        }
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
    /// ffplay/mpv for the formats paplay cannot decode. Both are routed to the chosen sink
    /// via PULSE_SINK, which the PulseAudio and pipewire-pulse client libraries honour.
    /// </summary>
    private static Process? StartDecoder(string filePath, string? sink)
    {
        if (HasFfplay.Value)
        {
            ProcessStartInfo psi = new("ffplay") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-nodisp");
            psi.ArgumentList.Add("-autoexit");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add(filePath);
            if (!string.IsNullOrEmpty(sink)) psi.Environment["PULSE_SINK"] = sink;

            try { return Process.Start(psi); }
            catch { /* fall through to mpv */ }
        }

        if (HasMpv.Value)
        {
            ProcessStartInfo psi = new("mpv") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--no-video");
            psi.ArgumentList.Add("--really-quiet");
            psi.ArgumentList.Add(filePath);
            if (!string.IsNullOrEmpty(sink)) psi.Environment["PULSE_SINK"] = sink;

            try { return Process.Start(psi); }
            catch { return null; }
        }

        return null;
    }

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

    private static string RunPactl(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pactl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return stdout;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool DetectPactl() => DetectTool("pactl", "--version");

    /// <summary>Probes once whether a CLI tool is installed and runnable.</summary>
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
            p.WaitForExit(1000);
            return p.ExitCode == 0;
        }
        catch { return false; }
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
