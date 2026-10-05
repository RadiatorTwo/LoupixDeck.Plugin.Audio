using System.Diagnostics;
using System.Globalization;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Sound playback through paplay, mpv, ffplay or ffmpeg piped into paplay.</summary>
public sealed partial class LinuxAudioService
{
    private readonly List<Playback> _playbacks = [];
    private readonly Lock _playbackLock = new();

    public void PlayFile(string filePath, string? endpointId, float volume)
    {
        int percent = (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100f);

        string? sink = string.IsNullOrWhiteSpace(endpointId)
            ? null
            : SplitId(endpointId).Name;

        // paplay goes through libsndfile (wav/flac/ogg) and cannot decode mp3/m4a.
        string extension = Path.GetExtension(filePath);
        bool needsDecoder =
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

        Process process =
            (!needsDecoder && HasPaplay.Value ? StartPaplay(filePath, sink, percent) : null)
            ?? StartDecoder(filePath, sink, percent)
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

    /// <summary>paplay's --volume takes PulseAudio's linear scale, where 65536 is 100 %.</summary>
    private static string PaplayVolume(int percent) =>
        (percent * 65536 / 100).ToString(CultureInfo.InvariantCulture);

    private static Process? StartPaplay(string filePath, string? sink, int percent)
    {
        ProcessStartInfo psi = new("paplay") { UseShellExecute = false, CreateNoWindow = true };
        if (!string.IsNullOrEmpty(sink)) psi.ArgumentList.Add($"--device={sink}");
        psi.ArgumentList.Add($"--volume={PaplayVolume(percent)}");
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
    private static Process? StartDecoder(string filePath, string? sink, int percent)
    {
        // paplay first: its --device is the selection this plugin already relies on for the
        // formats it can decode itself, so it is the one known to hold on this platform.
        // mpv only starts successfully with a device it accepted, but a build without the
        // pulse output would still start and play elsewhere, so it goes second.
        if (!string.IsNullOrEmpty(sink))
            return StartFfmpegToPaplay(filePath, sink, percent) ?? StartMpv(filePath, sink, percent);

        return StartFfplay(filePath, percent) ?? StartMpv(filePath, null, percent);
    }

    /// <summary>ffplay takes no device argument, so it is only used for the default device.</summary>
    private static Process? StartFfplay(string filePath, int percent)
    {
        if (!HasFfplay.Value) return null;

        ProcessStartInfo psi = new("ffplay") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-nodisp");
        psi.ArgumentList.Add("-autoexit");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-volume");
        psi.ArgumentList.Add(percent.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(filePath);

        try { return Process.Start(psi); }
        catch { return null; }
    }

    private static Process? StartMpv(string filePath, string? sink, int percent)
    {
        if (!HasMpv.Value) return null;

        ProcessStartInfo psi = new("mpv") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add($"--volume={percent.ToString(CultureInfo.InvariantCulture)}");
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
    private static Process? StartFfmpegToPaplay(string filePath, string sink, int percent)
    {
        if (!HasFfmpeg.Value || !HasPaplay.Value) return null;

        string command =
            $"ffmpeg -v quiet -i {ShellQuote(filePath)} -f s16le -ar 48000 -ac 2 - "
            + "| paplay --raw --rate=48000 --channels=2 --format=s16le "
            + $"--volume={PaplayVolume(percent)} --device={ShellQuote(sink)}";

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
}
