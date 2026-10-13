using System.Runtime.InteropServices;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Volume, mute and device-set changes through CoreAudio property listeners. CoreAudio calls the
/// listener on its own notification thread, so every callback only hands the work to the thread pool.
/// </summary>
public sealed partial class MacAudioService : IDisposable
{
    /// <summary>One delegate for every registration, held here so the GC never collects it while
    /// CoreAudio still holds its function pointer. The client data says which callback is meant.</summary>
    private static readonly PropertyListener Dispatch = OnProperty;

    private readonly DeviceChangeNotifier _deviceChanges = new();
    private readonly Lock _subscriptionLock = new();
    private Registration? _deviceSetRegistration;

    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        if (!TryResolve(endpointId, out uint deviceId, out uint scope)) return EmptyDisposable.Instance;

        List<PropertyAddress> addresses = [];
        foreach (uint element in (uint[])[ElementMain, .. StereoChannels])
        {
            if (Has(deviceId, PropVolumeScalar, scope, element))
                addresses.Add(new PropertyAddress(PropVolumeScalar, scope, element));
        }
        if (Has(deviceId, PropMute, scope))
            addresses.Add(new PropertyAddress(PropMute, scope, ElementMain));

        if (addresses.Count == 0) return EmptyDisposable.Instance;

        return new Registration(deviceId, addresses,
            () => onChange(GetVolume(endpointId), GetMute(endpointId)));
    }

    public IDisposable SubscribeDeviceChanges(Action onChange)
    {
        lock (_subscriptionLock)
        {
            // One set of system listeners feeds every subscriber.
            _deviceSetRegistration ??= new Registration(SystemObject,
                [
                    new PropertyAddress(PropDevices, ScopeGlobal, ElementMain),
                    new PropertyAddress(PropDefaultOutput, ScopeGlobal, ElementMain),
                    new PropertyAddress(PropDefaultInput, ScopeGlobal, ElementMain),
                ],
                _deviceChanges.Raise);
        }

        return _deviceChanges.Subscribe(onChange);
    }

    public void Dispose()
    {
        lock (_subscriptionLock)
        {
            _deviceSetRegistration?.Dispose();
            _deviceSetRegistration = null;
        }

        StopAllPlayback();
    }

    private static int OnProperty(uint objectId, uint addressCount, IntPtr addresses, IntPtr clientData)
    {
        if (GCHandle.FromIntPtr(clientData).Target is Registration registration)
            registration.Notify();
        return 0;
    }

    /// <summary>Listeners for a set of properties of one object, removed again on dispose.</summary>
    private sealed class Registration : IDisposable
    {
        private readonly uint _objectId;
        private readonly List<PropertyAddress> _addresses;
        private readonly Action _onChange;
        private readonly GCHandle _handle;
        private int _pending;
        private int _disposed;

        public Registration(uint objectId, List<PropertyAddress> addresses, Action onChange)
        {
            _objectId = objectId;
            _onChange = onChange;
            _handle = GCHandle.Alloc(this);
            _addresses = [];

            foreach (PropertyAddress address in addresses)
            {
                PropertyAddress copy = address;
                if (AudioObjectAddPropertyListener(objectId, ref copy, Dispatch, GCHandle.ToIntPtr(_handle)) == 0)
                    _addresses.Add(address);
            }
        }

        /// <summary>Runs the callback on the thread pool. A change to both channels of a stereo
        /// device arrives as two notifications; one pending run covers both.</summary>
        public void Notify()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Exchange(ref _pending, 1) != 0) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Interlocked.Exchange(ref _pending, 0);
                if (Volatile.Read(ref _disposed) != 0) return;
                try { _onChange(); }
                catch { /* a failing subscriber must not take down the CoreAudio notification path */ }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            foreach (PropertyAddress address in _addresses)
            {
                PropertyAddress copy = address;
                AudioObjectRemovePropertyListener(_objectId, ref copy, Dispatch, GCHandle.ToIntPtr(_handle));
            }

            // Removal waits for a callback in flight, so nothing can reach the handle after this.
            _handle.Free();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
