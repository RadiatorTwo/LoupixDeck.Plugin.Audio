using LoupixDeck.PluginSdk;
using NAudio.CoreAudioApi;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Windows Core Audio implementation, used when the plugin runs on Windows.</summary>
public sealed partial class WindowsAudioService : IAudioService, IDisposable
{
    private readonly HashSet<string> _loggedFailures = new(StringComparer.Ordinal);
    private readonly Lock _logLock = new();

    /// <summary>Logger handed in by the plugin after construction; null until then.</summary>
    public IPluginLogger? Logger { get; set; }

    public bool IsSupported => true;

    // WASAPI ships with Windows, so there is nothing that could be missing.
    public IReadOnlyList<PluginRequirement> GetRequirements() => [];

    /// <summary>
    /// The active endpoints of one kind. When the enumeration fails — for example while the Windows
    /// audio service restarts — the last list that was read is returned instead of throwing, because
    /// the callers run on timers and menu builders where an exception would take down far more than
    /// one stale list costs.
    /// </summary>
    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind)
    {
        try
        {
            IReadOnlyList<AudioEndpointInfo> endpoints = EnumerateEndpoints(kind);
            lock (_lastEndpointsLock) _lastEndpoints[kind] = endpoints;
            return endpoints;
        }
        catch (Exception ex)
        {
            LogOnce($"enumerate {kind} endpoints", ex);
            lock (_lastEndpointsLock)
                return _lastEndpoints.TryGetValue(kind, out IReadOnlyList<AudioEndpointInfo>? last) ? last : [];
        }
    }

    private readonly Lock _lastEndpointsLock = new();
    private readonly Dictionary<AudioEndpointKind, IReadOnlyList<AudioEndpointInfo>> _lastEndpoints = [];

    private List<AudioEndpointInfo> EnumerateEndpoints(AudioEndpointKind kind)
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
                result.Add(new AudioEndpointInfo(d.ID, FriendlyNameOf(d), d.ID == defaultId));
            }
            finally
            {
                d.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// The endpoint's friendly name, memoised per endpoint id.
    /// <para>
    /// Reading it goes to the device's property store and costs ~27 ms per endpoint, which is
    /// the whole price of enumerating: eleven endpoints take ~290 ms while the enumeration
    /// itself is under a millisecond. The dial preset menu builds its list per open - twice, as
    /// it happens - so that price landed on every right-click. The name of an endpoint that
    /// exists does not change on its own; a rename or a new device shows up when the entry
    /// expires.
    /// </para>
    /// </summary>
    private string FriendlyNameOf(MMDevice device)
    {
        string id = device.ID;
        long now = Environment.TickCount64;

        lock (_friendlyNameLock)
        {
            if (_friendlyNames.TryGetValue(id, out var cached) && now - cached.Stamp < FriendlyNameCacheMs)
                return cached.Name;
        }

        string name;
        try { name = device.FriendlyName; }
        catch { return id; }

        lock (_friendlyNameLock)
        {
            _friendlyNames[id] = (name, now);
        }

        return name;
    }

    /// <summary>How long a friendly name is reused. Long enough that opening a menu is free,
    /// short enough that a renamed device corrects itself without a restart.</summary>
    private const long FriendlyNameCacheMs = 60_000;

    private readonly Lock _friendlyNameLock = new();
    private readonly Dictionary<string, (string Name, long Stamp)> _friendlyNames = new(StringComparer.Ordinal);

    public string? GetDefaultEndpointId(AudioEndpointKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = kind == AudioEndpointKind.Render ? DataFlow.Render : DataFlow.Capture;
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            // No default endpoint configured for that flow.
            return null;
        }
    }

    public float GetVolume(string endpointId) =>
        WithEndpointVolume(endpointId, volume => volume.MasterVolumeLevelScalar, 0f);

    public void SetVolume(string endpointId, float scalar01) =>
        WithEndpointVolume(endpointId, volume =>
        {
            volume.MasterVolumeLevelScalar = Math.Clamp(scalar01, 0f, 1f);
            return true;
        }, false);

    public bool GetMute(string endpointId) =>
        WithEndpointVolume(endpointId, volume => volume.Mute, false);

    public void SetMute(string endpointId, bool muted) =>
        WithEndpointVolume(endpointId, volume =>
        {
            volume.Mute = muted;
            return true;
        }, false);

    public bool SetDefaultEndpoint(string endpointId)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return PolicyConfig.SetDefaultEndpoint(endpointId, Logger);
        }
        catch (Exception ex)
        {
            LogOnce("set-default-endpoint", ex);
            return false;
        }
    }

    /// <summary>
    /// Logs one failure per operation for the lifetime of the service. Session enumeration
    /// runs on a repeating timer while the mixer folder is open, so an unlogged-once
    /// failure would flood the log at several lines per second.
    /// </summary>
    private void LogOnce(string operation, Exception ex)
    {
        lock (_logLock)
        {
            if (!_loggedFailures.Add(operation)) return;
        }
        Logger?.Warn($"Audio: {operation} failed: {ex.Message}");
    }

    private static MMDevice? GetDevice(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try { return enumerator.GetDevice(endpointId); }
        catch { return null; }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
