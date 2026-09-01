using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Windows Core Audio implementation, used when the plugin runs on Windows.</summary>
public sealed class WindowsAudioService : IAudioService
{
    private readonly List<Playback> _playbacks = [];
    private readonly Lock _playbackLock = new();

    public bool IsSupported => true;

    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = kind == AudioEndpointKind.Render ? DataFlow.Render : DataFlow.Capture;

        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = def.ID;
        }
        catch
        {
            // No default endpoint configured — leave defaultId null.
        }

        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var result = new List<AudioEndpointInfo>(devices.Count);
        foreach (var d in devices)
        {
            try
            {
                result.Add(new AudioEndpointInfo(d.ID, d.FriendlyName, d.ID == defaultId));
            }
            finally
            {
                d.Dispose();
            }
        }
        return result;
    }

    public float GetVolume(string endpointId)
    {
        using var dev = GetDevice(endpointId);
        return dev?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0f;
    }

    public void SetVolume(string endpointId, float scalar01)
    {
        using var dev = GetDevice(endpointId);
        if (dev?.AudioEndpointVolume == null) return;
        dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(scalar01, 0f, 1f);
    }

    public bool GetMute(string endpointId)
    {
        using var dev = GetDevice(endpointId);
        return dev?.AudioEndpointVolume?.Mute ?? false;
    }

    public void SetMute(string endpointId, bool muted)
    {
        using var dev = GetDevice(endpointId);
        if (dev?.AudioEndpointVolume == null) return;
        dev.AudioEndpointVolume.Mute = muted;
    }

    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);

        var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(endpointId);
        }
        catch
        {
            enumerator.Dispose();
            return EmptyDisposable.Instance;
        }

        AudioEndpointVolumeNotificationDelegate handler = data =>
        {
            try { onChange(data.MasterVolume, data.Muted); }
            catch { /* swallow callback failure */ }
        };

        try
        {
            device.AudioEndpointVolume.OnVolumeNotification += handler;
        }
        catch
        {
            device.Dispose();
            enumerator.Dispose();
            return EmptyDisposable.Instance;
        }

        return new VolumeSubscription(device, enumerator, handler);
    }

    public void PlayFile(string filePath, string? endpointId)
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
            playback.Start(filePath, OnPlaybackFinished);
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

    private void OnPlaybackFinished(Playback playback)
    {
        lock (_playbackLock) _playbacks.Remove(playback);
        playback.Dispose();
    }

    private static MMDevice? GetDevice(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try { return enumerator.GetDevice(endpointId); }
        catch { return null; }
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

        public void Start(string filePath, Action<Playback> onFinished)
        {
            _reader = new AudioFileReader(filePath);

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

    private sealed class VolumeSubscription : IDisposable
    {
        private MMDevice? _device;
        private MMDeviceEnumerator? _enumerator;
        private AudioEndpointVolumeNotificationDelegate? _handler;

        public VolumeSubscription(MMDevice device, MMDeviceEnumerator enumerator,
            AudioEndpointVolumeNotificationDelegate handler)
        {
            _device = device;
            _enumerator = enumerator;
            _handler = handler;
        }

        public void Dispose()
        {
            try
            {
                if (_device?.AudioEndpointVolume != null && _handler != null)
                    _device.AudioEndpointVolume.OnVolumeNotification -= _handler;
            }
            catch { /* ignore */ }

            _device?.Dispose();
            _enumerator?.Dispose();
            _device = null;
            _enumerator = null;
            _handler = null;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
