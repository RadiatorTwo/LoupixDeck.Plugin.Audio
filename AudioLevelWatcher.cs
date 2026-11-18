namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Watches the volume and mute state of every endpoint and reports any change, wherever it came
/// from: a dial, a touch key, the system tray or another application.
/// <para>
/// The host draws a dial's value (the segmented strip, the tap zones of a free-draw strip, the
/// wheel) and a touch key's value layers only when it is told the value moved. Without this the
/// plugin only said so after its own dial turned, so a mute from a touch key or the system left the
/// strip showing the old state (issue #646).
/// </para>
/// The subscriptions are rebuilt when the device set changes, since an endpoint that comes back
/// never fires on a subscription made before it went away.
/// </summary>
internal sealed class AudioLevelWatcher(IAudioService audio, Action onChange) : IDisposable
{
    private readonly Lock _lock = new();
    private List<IDisposable> _subscriptions = [];
    private bool _disposed;

    /// <summary>Subscribes to every endpoint the backend lists now, dropping the previous set.</summary>
    public void Resubscribe()
    {
        List<string> ids;
        try
        {
            ids = audio.GetEndpoints(AudioEndpointKind.Render)
                .Concat(audio.GetEndpoints(AudioEndpointKind.Capture))
                .Select(ep => ep.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            // Enumeration is best effort: the next device change tries again.
            return;
        }

        List<IDisposable> fresh = [];
        foreach (string id in ids)
        {
            try { fresh.Add(audio.SubscribeVolumeChanges(id, (_, _) => onChange())); }
            catch { /* an endpoint without notifications is simply not watched */ }
        }

        List<IDisposable> old;
        lock (_lock)
        {
            if (_disposed)
            {
                old = fresh;
            }
            else
            {
                old = _subscriptions;
                _subscriptions = fresh;
            }
        }

        DisposeAll(old);
    }

    public void Dispose()
    {
        List<IDisposable> old;
        lock (_lock)
        {
            _disposed = true;
            old = _subscriptions;
            _subscriptions = [];
        }

        DisposeAll(old);
    }

    private static void DisposeAll(List<IDisposable> subscriptions)
    {
        foreach (IDisposable subscription in subscriptions)
        {
            try { subscription.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
