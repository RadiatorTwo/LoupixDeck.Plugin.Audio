using LoupixDeck.PluginSdk;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Linux implementation backed by <c>pactl</c>. Works on PulseAudio and on PipeWire
/// systems via the pipewire-pulse compatibility layer.
/// </summary>
public sealed partial class LinuxAudioService : IAudioService
{
    private static readonly ToolProbe HasPactl = new("pactl", "--version");
    private static readonly ToolProbe HasPaplay = new("paplay", "--version");
    private static readonly ToolProbe HasFfplay = new("ffplay", "-version");
    private static readonly ToolProbe HasMpv = new("mpv", "--version");
    private static readonly ToolProbe HasFfmpeg = new("ffmpeg", "-version");

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

    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind) =>
        IsSupported ? Endpoints(kind).List : [];

    public string? GetDefaultEndpointId(AudioEndpointKind kind)
    {
        if (!IsSupported) return null;

        string name = Endpoints(kind).DefaultName;
        if (string.IsNullOrEmpty(name)) return null;

        // Same "sink:NAME" / "source:NAME" encoding GetEndpoints reports, so the id is
        // interchangeable with a bound one.
        return LevelKey(kind, name);
    }

    public float GetVolume(string endpointId) => IsSupported ? Level(endpointId).Volume : 0f;

    public void SetVolume(string endpointId, float scalar01)
    {
        if (!IsSupported) return;
        var (kind, name) = SplitId(endpointId);
        float clamped = Math.Clamp(scalar01, 0f, 1f);
        var pct = (int)Math.Round(clamped * 100f);
        if (TryRunPactl(out _, $"set-{Noun(kind)}-volume", name, $"{pct.ToString(CultureInfo.InvariantCulture)}%"))
            StoreLevel(endpointId, pct / 100f, null);
    }

    public bool GetMute(string endpointId) => IsSupported && Level(endpointId).Muted;

    public void SetMute(string endpointId, bool muted)
    {
        if (!IsSupported) return;
        var (kind, name) = SplitId(endpointId);
        if (TryRunPactl(out _, $"set-{Noun(kind)}-mute", name, muted ? "1" : "0"))
            StoreLevel(endpointId, null, muted);
    }

    /// <summary>
    /// Registers with the shared event monitor. The callback runs only for a change on exactly
    /// this device, not for app streams or other devices, which used to cost two pactl processes
    /// per subscriber on every stream change.
    /// </summary>
    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        return IsSupported ? AddListener(endpointId, onChange) : EmptyDisposable.Instance;
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
            foreach (SinkInput input in SinkInputs())
                RunPactl("move-sink-input", input.Index.ToString(CultureInfo.InvariantCulture), name);
            InvalidateSinkInputs();
        }

        // The server event that follows would clear it too, but a menu opened right now must
        // already show the new default.
        lock (_cacheLock)
        {
            _endpoints.Remove(kind);
            _generation++;
        }

        string current = RunPactl($"get-default-{noun}").Trim();
        return string.Equals(current, name, StringComparison.Ordinal);
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

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
