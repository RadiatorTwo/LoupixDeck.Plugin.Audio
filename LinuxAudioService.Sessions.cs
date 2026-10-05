using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Per-application mixer sessions, read from pactl's sink inputs.</summary>
public sealed partial class LinuxAudioService
{
    /// <summary>
    /// <paramref name="endpointId"/> is accepted and ignored: pactl lists every sink input
    /// regardless of its sink, and filtering by sink would hide exactly the streams a user
    /// wants to turn down when they are playing on another device.
    /// </summary>
    public IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId)
    {
        if (!IsSupported) return [];

        Dictionary<string, AudioSessionInfo> byApp = new(StringComparer.Ordinal);
        foreach (SinkInput input in SinkInputs())
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
        foreach (SinkInput input in SinkInputs())
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
        foreach (SinkInput input in SinkInputs())
        {
            if (string.Equals(input.AppId, appId, StringComparison.Ordinal))
                RunPactl("set-sink-input-volume", input.Index.ToString(CultureInfo.InvariantCulture),
                    $"{percent.ToString(CultureInfo.InvariantCulture)}%");
        }
        InvalidateSinkInputs();
    }

    public bool? GetSessionMute(string? endpointId, string appId)
    {
        if (!IsSupported) return null;

        bool? result = null;
        foreach (SinkInput input in SinkInputs())
        {
            if (!string.Equals(input.AppId, appId, StringComparison.Ordinal)) continue;
            result = result is { } current ? current && input.Muted : input.Muted;
        }
        return result;
    }

    public void SetSessionMute(string? endpointId, string appId, bool muted)
    {
        if (!IsSupported) return;

        foreach (SinkInput input in SinkInputs())
        {
            if (string.Equals(input.AppId, appId, StringComparison.Ordinal))
                RunPactl("set-sink-input-mute", input.Index.ToString(CultureInfo.InvariantCulture), muted ? "1" : "0");
        }
        InvalidateSinkInputs();
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

            string appId = AppIdOf(binary, appName,
                Regex.Match(block, @"media\.name\s*=\s*""([^""]+)""").Groups[1].Value);

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

    /// <summary>
    /// The stable identity of a stream. The executable name comes first, so every binding made
    /// before the fallbacks existed still resolves. Streams without one — some Flatpak apps,
    /// browser tabs, network streams — fall back to <c>application.name</c>, then <c>media.name</c>;
    /// those are names rather than file names, so no extension is stripped. Characters that would
    /// break a saved binding such as <c>Audio.AppVolume(appId,5)</c> are replaced.
    /// </summary>
    private static string AppIdOf(string binary, string appName, string mediaName)
    {
        if (binary.Length > 0) return Path.GetFileNameWithoutExtension(binary).ToLowerInvariant();

        string name = appName.Trim();
        if (name.Length == 0) name = mediaName.Trim();

        return name.ToLowerInvariant().Replace(',', '_').Replace('(', '_').Replace(')', '_');
    }
}
