namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The subscribers of <see cref="IAudioService.SubscribeDeviceChanges"/>, shared by both backends.
/// <para>
/// Events arrive on the backend's own thread — the WASAPI notification thread, which must not call
/// back into the audio API, or the pactl monitor's reader. <see cref="Raise"/> therefore only marks
/// a change pending; the subscribers run on a worker after <see cref="SettleDelay"/>. That also
/// folds a burst into one call: Windows reports a default change once per role, and plugging in a
/// headset adds a device, changes its state and moves the default within a few milliseconds.
/// </para>
/// </summary>
internal sealed class DeviceChangeNotifier
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly List<Action> _subscribers = [];
    private bool _pending;

    public IDisposable Subscribe(Action onChange)
    {
        lock (_gate) _subscribers.Add(onChange);
        return new Subscription(this, onChange);
    }

    /// <summary>Notes a change. Returns at once; the subscribers run later on a worker.</summary>
    public void Raise()
    {
        lock (_gate)
        {
            if (_pending || _subscribers.Count == 0) return;
            _pending = true;
        }

        _ = Task.Delay(SettleDelay).ContinueWith(_ => Drain(), TaskScheduler.Default);
    }

    private void Drain()
    {
        Action[] subscribers;
        lock (_gate)
        {
            _pending = false;
            subscribers = [.. _subscribers];
        }

        foreach (Action subscriber in subscribers)
        {
            try { subscriber(); }
            catch { /* a failing subscriber must not keep the others from hearing about the change */ }
        }
    }

    private sealed class Subscription(DeviceChangeNotifier owner, Action onChange) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner._subscribers.Remove(onChange);
        }
    }
}
