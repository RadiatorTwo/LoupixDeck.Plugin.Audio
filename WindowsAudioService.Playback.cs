using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Sound playback through WASAPI.</summary>
public sealed partial class WindowsAudioService
{
    private readonly List<Playback> _playbacks = [];
    private readonly Lock _playbackLock = new();

    public void PlayFile(string filePath, string? endpointId, float volume)
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = string.IsNullOrWhiteSpace(endpointId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(endpointId);
        }
        catch (Exception ex)
        {
            enumerator.Dispose();
            // Deliberately no fallback to the default device: the user picked this endpoint
            // (often a virtual cable), and playing the sound out of the speakers instead
            // would be a worse surprise than staying silent and logging it.
            throw new InvalidOperationException(
                $"Playback device '{endpointId}' is not available.", ex);
        }

        Playback playback = new(device, enumerator);
        try
        {
            playback.Start(filePath, volume, OnPlaybackFinished);
        }
        catch
        {
            playback.Dispose();
            throw;
        }

        lock (_playbackLock) _playbacks.Add(playback);
    }

    public void StopAllPlayback()
    {
        Playback[] running;
        lock (_playbackLock)
        {
            running = [.. _playbacks];
            _playbacks.Clear();
        }

        foreach (Playback playback in running) playback.Dispose();
    }

    public bool StopFile(string filePath)
    {
        Playback[] matching;
        lock (_playbackLock)
        {
            matching = [.. _playbacks.Where(p =>
                string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase))];
            foreach (Playback playback in matching) _playbacks.Remove(playback);
        }

        // Disposing raises PlaybackStopped, so OnPlaybackFinished runs for an entry that is
        // already gone from the list — a no-op Remove, and Dispose guards against running twice.
        foreach (Playback playback in matching) playback.Dispose();
        return matching.Length > 0;
    }

    private void OnPlaybackFinished(Playback playback)
    {
        lock (_playbackLock) _playbacks.Remove(playback);
        playback.Dispose();
    }

    /// <summary>
    /// One fire-and-forget playback. Each press builds its own instance, which is what
    /// makes repeated presses overlap instead of interrupting each other.
    /// </summary>
    private sealed class Playback(MMDevice device, MMDeviceEnumerator enumerator) : IDisposable
    {
        private WasapiOut? _output;
        private AudioFileReader? _reader;
        private IDisposable? _resampler;
        private int _disposed;

        /// <summary>The file this playback was started with; empty until <see cref="Start"/>.</summary>
        public string FilePath { get; private set; } = string.Empty;

        public void Start(string filePath, float volume, Action<Playback> onFinished)
        {
            FilePath = filePath;
            // The reader scales the samples itself, so the level holds for this sound only and
            // the device, the session and every other sound keep theirs.
            _reader = new AudioFileReader(filePath) { Volume = Math.Clamp(volume, 0f, 1f) };

            // useEventSync: false — the non-event-driven path runs its own thread and needs
            // no message pump, which Execute's background thread does not have.
            _output = new WasapiOut(device, AudioClientShareMode.Shared, false, 200);
            _output.PlaybackStopped += (_, _) => onFinished(this);

            try
            {
                _output.Init(_reader);
            }
            catch
            {
                // Shared mode rejects a format the device mixer cannot take — resample to
                // the endpoint's mix format and retry.
                MediaFoundationResampler resampler = new(_reader, device.AudioClient.MixFormat)
                {
                    ResamplerQuality = 60
                };
                _resampler = resampler;
                _output.Init(resampler);
            }

            _output.Play();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _output?.Stop(); } catch { /* ignore */ }
            try { _output?.Dispose(); } catch { /* ignore */ }
            try { _resampler?.Dispose(); } catch { /* ignore */ }
            try { _reader?.Dispose(); } catch { /* ignore */ }
            try { device.Dispose(); } catch { /* ignore */ }
            try { enumerator.Dispose(); } catch { /* ignore */ }

            _output = null;
            _resampler = null;
            _reader = null;
        }
    }
}
