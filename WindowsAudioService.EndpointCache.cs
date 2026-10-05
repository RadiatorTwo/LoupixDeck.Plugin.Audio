using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The endpoint volume objects of the render path, cached per endpoint id.
/// <para>
/// A dial's level is pulled once per frame per dial (<c>Audio.Volume.GetValue</c> reads volume and
/// mute), and building an enumerator plus an <see cref="MMDevice"/> for every read costs COM
/// round trips and, because NAudio releases some of its wrappers only through the garbage
/// collector, handles. The cached <see cref="AudioEndpointVolume"/> is reused until the endpoint
/// is removed, changes state or a call on it fails.
/// </para>
/// </summary>
public sealed partial class WindowsAudioService
{
    private readonly Lock _volumeLock = new();
    private readonly Dictionary<string, MMDevice> _volumeDevices = new(StringComparer.Ordinal);

    // Kept alive while registered: Windows calls the client back through this enumerator.
    private MMDeviceEnumerator? _notificationEnumerator;
    private EndpointChangeClient? _notificationClient;

    /// <summary>
    /// Runs <paramref name="action"/> against the cached endpoint volume of <paramref name="endpointId"/>.
    /// A failing call drops the cached device and is tried once more on a fresh one, so an endpoint that
    /// was re-plugged under the same id recovers without waiting for a notification. Returns
    /// <paramref name="fallback"/> when the endpoint cannot be opened.
    /// </summary>
    private T WithEndpointVolume<T>(string endpointId, Func<AudioEndpointVolume, T> action, T fallback)
    {
        lock (_volumeLock)
        {
            EnsureNotificationClient();

            for (int attempt = 0; ; attempt++)
            {
                AudioEndpointVolume? volume = AcquireEndpointVolume(endpointId);
                if (volume == null) return fallback;

                try
                {
                    return action(volume);
                }
                catch (COMException) when (attempt == 0)
                {
                    ReleaseVolumeDevice(endpointId);
                }
            }
        }
    }

    private AudioEndpointVolume? AcquireEndpointVolume(string endpointId)
    {
        if (_volumeDevices.TryGetValue(endpointId, out MMDevice? cached))
            return cached.AudioEndpointVolume;

        MMDevice? device = GetDevice(endpointId);
        if (device == null) return null;

        try
        {
            AudioEndpointVolume volume = device.AudioEndpointVolume;
            _volumeDevices[endpointId] = device;
            return volume;
        }
        catch
        {
            device.Dispose();
            return null;
        }
    }

    private void ReleaseVolumeDevice(string endpointId)
    {
        if (_volumeDevices.Remove(endpointId, out MMDevice? device))
            device.Dispose();
    }

    /// <summary>
    /// Registers for endpoint notifications once, so a removed or disabled endpoint drops its cached
    /// device. Without the registration the cache still works: a call on a dead device fails and the
    /// retry in <see cref="WithEndpointVolume{T}"/> replaces it.
    /// </summary>
    private void EnsureNotificationClient()
    {
        if (_notificationEnumerator != null) return;

        MMDeviceEnumerator enumerator = new();
        EndpointChangeClient client = new(this);
        try
        {
            enumerator.RegisterEndpointNotificationCallback(client);
        }
        catch (Exception ex)
        {
            LogOnce("register-endpoint-notifications", ex);
        }

        _notificationEnumerator = enumerator;
        _notificationClient = client;
    }

    /// <summary>Called from the notification thread: forgets the device, nothing more.</summary>
    private void OnEndpointChanged(string endpointId)
    {
        lock (_volumeLock) ReleaseVolumeDevice(endpointId);
    }

    /// <summary>Releases the cached endpoint volumes and the notification registration.</summary>
    private void DisposeEndpointCache()
    {
        MMDeviceEnumerator? enumerator;
        EndpointChangeClient? client;
        lock (_volumeLock)
        {
            enumerator = _notificationEnumerator;
            client = _notificationClient;
            _notificationEnumerator = null;
            _notificationClient = null;
        }

        // Outside the lock: unregistering waits for a running callback, and the callback takes the lock.
        if (enumerator != null && client != null)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(client); }
            catch { /* the audio service may already be gone */ }
        }
        enumerator?.Dispose();

        lock (_volumeLock)
        {
            foreach (string id in _volumeDevices.Keys.ToList()) ReleaseVolumeDevice(id);
        }
    }

    /// <summary>
    /// Forwards removal and state changes of an endpoint. Windows raises these on its own thread and
    /// expects the callback to return quickly, so it only drops a cache entry.
    /// </summary>
    private sealed class EndpointChangeClient(WindowsAudioService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.OnEndpointChanged(deviceId);
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) => owner.OnEndpointChanged(deviceId);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
