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

    private static bool DetectPactl()
    {
        try
        {
            var psi = new ProcessStartInfo("pactl", "--version")
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
