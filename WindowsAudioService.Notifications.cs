using NAudio.CoreAudioApi;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Endpoint volume notifications, handed off the WASAPI notification thread.</summary>
public sealed partial class WindowsAudioService
{
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

        // WASAPI raises this on its own notification thread, and a handler that calls back into
        // the endpoint API from it kills the registration: after the first notification the
        // client is never called again (measured: 20 notifications for 10 volume changes with an
        // inert handler, 1 with a handler that reads volume/mute back). Consumers legitimately
        // read the endpoint - the strip bars re-resolve the default device and the host renders
        // the dial value - so the notification is handed to a pump that runs them off this
        // thread, and the thread returns to WASAPI immediately.
        var pump = new NotificationPump(onChange);
        AudioEndpointVolumeNotificationDelegate handler = data => pump.Post(data.MasterVolume, data.Muted);

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

        return new VolumeSubscription(device, enumerator, handler, pump);
    }

    private sealed class VolumeSubscription : IDisposable
    {
        private MMDevice? _device;
        private MMDeviceEnumerator? _enumerator;
        private AudioEndpointVolumeNotificationDelegate? _handler;
        private NotificationPump? _pump;

        public VolumeSubscription(MMDevice device, MMDeviceEnumerator enumerator,
            AudioEndpointVolumeNotificationDelegate handler, NotificationPump pump)
        {
            _device = device;
            _enumerator = enumerator;
            _handler = handler;
            _pump = pump;
        }

        public void Dispose()
        {
            try
            {
                if (_device?.AudioEndpointVolume != null && _handler != null)
                    _device.AudioEndpointVolume.OnVolumeNotification -= _handler;
            }
            catch { /* ignore */ }

            _pump?.Stop();
            _device?.Dispose();
            _enumerator?.Dispose();
            _device = null;
            _enumerator = null;
            _handler = null;
            _pump = null;
        }
    }

    /// <summary>
    /// Carries a volume notification off the WASAPI notification thread to the subscriber.
    /// <para>
    /// Conflating rather than queueing: a dial turn produces a notification per detent and the
    /// subscriber only ever wants the current value, so a pending notification is overwritten
    /// instead of piling up worker tasks behind a render. At most one worker runs at a time, so
    /// the subscriber still sees its values in order.
    /// </para>
    /// </summary>
    private sealed class NotificationPump(Action<float, bool> onChange)
    {
        private readonly object _gate = new();
        private (float Volume, bool Muted)? _pending;
        private bool _running;
        private bool _stopped;

        public void Post(float volume, bool muted)
        {
            lock (_gate)
            {
                if (_stopped) return;
                _pending = (volume, muted);
                if (_running) return;
                _running = true;
            }

            Task.Run(Drain);
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
                _pending = null;
            }
        }

        private void Drain()
        {
            while (true)
            {
                (float Volume, bool Muted) next;
                lock (_gate)
                {
                    if (_stopped || _pending == null)
                    {
                        _running = false;
                        return;
                    }

                    next = _pending.Value;
                    _pending = null;
                }

                try { onChange(next.Volume, next.Muted); }
                catch { /* swallow subscriber failure */ }
            }
        }
    }
}
