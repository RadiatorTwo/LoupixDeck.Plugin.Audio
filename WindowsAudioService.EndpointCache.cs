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

    private readonly DeviceChangeNotifier _deviceChanges = new();

    public IDisposable SubscribeDeviceChanges(Action onChange)
    {
        // Registered here as well as on the first volume read: a button that shows only the
        // current output reads no volume, and would otherwise never hear of a change.
        lock (_volumeLock) EnsureNotificationClient();
        return _deviceChanges.Subscribe(onChange);
    }

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

    /// <summary>
    /// Called from the notification thread when an endpoint was removed or changed state: forgets
    /// its device and the endpoint list, and tells the subscribers. No COM call here — see
    /// <see cref="DeviceChangeNotifier"/>.
    /// </summary>
    private void OnEndpointChanged(string endpointId)
    {
        lock (_volumeLock) ReleaseVolumeDevice(endpointId);
        OnEndpointListChanged();
    }

    /// <summary>Called from the notification thread when the set of endpoints changed.</summary>
    private void OnEndpointListChanged()
    {
        // Expires the endpoint list the mixer walks, so a new device's sessions show up at once
        // instead of after EndpointListLifetime.
        Interlocked.Exchange(ref _renderEndpointsStamp, 0);
        _deviceChanges.Raise();
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

        ReleaseVolumeDevices();
    }

    /// <summary>Releases every cached endpoint volume; the notification registration stays.</summary>
    private void ReleaseVolumeDevices()
    {
        lock (_volumeLock)
        {
            foreach (string id in _volumeDevices.Keys.ToList()) ReleaseVolumeDevice(id);
        }
    }

    /// <summary>
    /// Forwards endpoint and default-device changes. Windows raises these on its own thread and
    /// expects the callback to return quickly, so it only drops cache entries and marks a change.
    /// </summary>
    private sealed class EndpointChangeClient(WindowsAudioService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.OnEndpointChanged(deviceId);
        public void OnDeviceAdded(string pwstrDeviceId) => owner.OnEndpointListChanged();
        public void OnDeviceRemoved(string deviceId) => owner.OnEndpointChanged(deviceId);

        // Raised once per role. The plugin reads the multimedia default, so the other two are noise.
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) owner._deviceChanges.Raise();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
