using System.Diagnostics;
using System.Globalization;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Sound playback through <c>afplay</c>, which ships with macOS. afplay always plays on the default
/// output, so a sound bound to another device plays on the default one instead.
/// </summary>
public sealed partial class MacAudioService
{
    private const string Afplay = "/usr/bin/afplay";

    private readonly List<Playback> _playbacks = [];
    private readonly Lock _playbackLock = new();
    private bool _loggedDeviceFallback;

    public void PlayFile(string filePath, string? endpointId, float volume)
    {
        if (!string.IsNullOrWhiteSpace(endpointId)
            && endpointId != GetDefaultEndpointId(AudioEndpointKind.Render)
            && !_loggedDeviceFallback)
        {
            _loggedDeviceFallback = true;
            Logger?.Info("Audio: afplay cannot choose an output device, so sounds play on the default output.");
        }

        ProcessStartInfo psi = new(Afplay)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-v", Math.Clamp(volume, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture),
                filePath,
            },
        };

        Process process = Process.Start(psi)
                          ?? throw new InvalidOperationException($"Could not start afplay for '{filePath}'.");

        Playback playback = new(process, filePath);
        lock (_playbackLock) _playbacks.Add(playback);

        // Reap the entry once the sound ends. The handler is attached before EnableRaisingEvents,
        // which also fires for a process that has already exited, so the entry cannot leak.
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
            matching = [.. _playbacks.Where(p => string.Equals(p.FilePath, filePath, StringComparison.Ordinal))];
            foreach (Playback playback in matching) _playbacks.Remove(playback);
        }

        foreach (Playback playback in matching) Kill(playback);
        return matching.Length > 0;
    }

    /// <summary>The afplay process of one running sound, and the file it was started with.</summary>
    private readonly record struct Playback(Process Process, string FilePath);

    private static void Kill(Playback playback)
    {
        try { playback.Process.Kill(); }
        catch { /* already exited */ }
    }
}
