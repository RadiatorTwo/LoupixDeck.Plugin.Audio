using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using LoupixDeck.PluginSdk;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Windows Core Audio implementation, used when the plugin runs on Windows.</summary>
public sealed class WindowsAudioService : IAudioService, IDisposable
{
    private readonly List<Playback> _playbacks = [];
    private readonly Lock _playbackLock = new();

    private readonly HashSet<string> _loggedFailures = new(StringComparer.Ordinal);
    private readonly Lock _logLock = new();

    /// <summary>AppId of the Windows system-sounds session, which has no process of its own.</summary>
    private const string SystemSoundsAppId = "system";

    /// <summary>
    /// The Windows audio engine. It owns render sessions on every endpoint a virtual audio
    /// device loops through, but it is plumbing rather than an application anyone would
    /// turn down, so it never reaches the mixer.
    /// </summary>
    private const string AudioEngineAppId = "audiodg";

    /// <summary>
    /// How long a resolved endpoint list is reused. NAudio's EnumerateAudioEndPoints leaves
    /// one COM wrapper per endpoint behind on every call for the garbage collector, so
    /// calling it on each mixer poll made the handle count climb by one per endpoint per
    /// poll. A newly connected device reaches the mixer within this interval instead.
    /// </summary>
    private static readonly TimeSpan EndpointListLifetime = TimeSpan.FromSeconds(5);

    // One cached device per endpoint — see AcquireSessionManager for why they are not
    // built per call — plus the resolved process name of every pid that owns a session.
    private readonly Lock _sessionLock = new();
    private IReadOnlyList<string> _renderEndpointIds = [];
    private long _renderEndpointsStamp;
    private readonly Dictionary<string, CachedEndpoint> _sessionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _appIdByPid = [];

    private sealed record CachedEndpoint(MMDevice Device, AudioSessionManager Manager);

    /// <summary>Logger handed in by the plugin after construction; null until then.</summary>
    public IPluginLogger? Logger { get; set; }

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

    public IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId)
    {
        int ownPid = Environment.ProcessId;
        Dictionary<string, AudioSessionInfo> byApp = new(StringComparer.Ordinal);

        ForEachSession(endpointId, (session, appId, displayName) =>
        {
            if (session.GetProcessID == ownPid) return;

            float volume = session.SimpleAudioVolume.Volume;
            bool muted = session.SimpleAudioVolume.Mute;

            // Several sessions per app: keep the loudest, and count the app as muted
            // only when every one of its sessions is.
            if (byApp.TryGetValue(appId, out AudioSessionInfo? existing))
            {
                byApp[appId] = existing with
                {
                    Volume = Math.Max(existing.Volume, volume),
                    Muted = existing.Muted && muted
                };
            }
            else
            {
                byApp[appId] = new AudioSessionInfo(appId, displayName, volume, muted);
            }
        });

        return [.. byApp.Values.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public float? GetSessionVolume(string? endpointId, string appId)
    {
        float? result = null;
        ForEachSession(endpointId, (session, id, _) =>
        {
            if (!string.Equals(id, appId, StringComparison.Ordinal)) return;
            float volume = session.SimpleAudioVolume.Volume;
            result = result is { } current ? Math.Max(current, volume) : volume;
        });
        return result;
    }

    public void SetSessionVolume(string? endpointId, string appId, float scalar01)
    {
        float clamped = Math.Clamp(scalar01, 0f, 1f);
        ForEachSession(endpointId, (session, id, _) =>
        {
            if (string.Equals(id, appId, StringComparison.Ordinal))
                session.SimpleAudioVolume.Volume = clamped;
        });
    }

    public bool? GetSessionMute(string? endpointId, string appId)
    {
        bool? result = null;
        ForEachSession(endpointId, (session, id, _) =>
        {
            if (!string.Equals(id, appId, StringComparison.Ordinal)) return;
            bool muted = session.SimpleAudioVolume.Mute;
            result = result is { } current ? current && muted : muted;
        });
        return result;
    }

    public void SetSessionMute(string? endpointId, string appId, bool muted)
    {
        ForEachSession(endpointId, (session, id, _) =>
        {
            if (string.Equals(id, appId, StringComparison.Ordinal))
                session.SimpleAudioVolume.Mute = muted;
        });
    }

    public string? GetForegroundAppId()
    {
        try
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero) return null;

            _ = NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return null;

            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            LogOnce("foreground-app", ex);
            return null;
        }
    }

    public bool SetDefaultEndpoint(string endpointId)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return PolicyConfig.SetDefaultEndpoint(endpointId);
        }
        catch (Exception ex)
        {
            LogOnce("set-default-endpoint", ex);
            return false;
        }
    }

    /// <summary>
    /// Walks the active sessions of one render endpoint, or of every active render
    /// endpoint when <paramref name="endpointId"/> is null, handing each one to
    /// <paramref name="visit"/> together with its AppId and display name. The session
    /// objects are owned by the collection and must not be used after this returns.
    /// </summary>
    private void ForEachSession(string? endpointId,
        Action<AudioSessionControl, string, string> visit)
    {
        lock (_sessionLock)
        {
            HashSet<uint> livePids = [];

            foreach (string id in ResolveEndpointIds(endpointId))
            {
                AudioSessionManager? manager = AcquireSessionManager(id);
                if (manager == null) continue;

                try
                {
                    // Picks up the applications that started playing since the last call.
                    manager.RefreshSessions();

                    SessionCollection sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        AudioSessionControl session = sessions[i];
                        try
                        {
                            if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                            livePids.Add(session.GetProcessID);

                            string appId = ResolveAppId(session);
                            if (appId.Length == 0 || appId == AudioEngineAppId) continue;

                            visit(session, appId, ResolveDisplayName(session, appId));
                        }
                        catch (Exception ex)
                        {
                            LogOnce("session-visit", ex);
                        }
                        finally
                        {
                            session.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogOnce("session-enumerate", ex);
                    // That endpoint is gone or its driver restarted. Drop it so the next
                    // call rebuilds it instead of reusing a dead device.
                    ReleaseEndpoint(id);
                }
            }

            PruneProcessNames(livePids);
        }
    }

    /// <summary>
    /// The endpoints to walk: the one the caller named, or every active render endpoint.
    /// Applications do not all play on the default device — a virtual mixer such as
    /// SteelSeries Sonar gives each application a device of its own — so looking only at
    /// the default would silently miss them.
    /// </summary>
    private IReadOnlyList<string> ResolveEndpointIds(string? endpointId)
    {
        if (!string.IsNullOrWhiteSpace(endpointId)) return [endpointId];

        if (_renderEndpointIds.Count > 0 &&
            Stopwatch.GetElapsedTime(_renderEndpointsStamp) < EndpointListLifetime)
        {
            return _renderEndpointIds;
        }

        try
        {
            using MMDeviceEnumerator enumerator = new();
            MMDeviceCollection devices =
                enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            List<string> ids = new(devices.Count);
            foreach (MMDevice device in devices)
            {
                try { ids.Add(device.ID); }
                finally { device.Dispose(); }
            }

            // An endpoint that went away must not keep its cached device alive.
            foreach (string stale in _sessionCache.Keys.Where(key => !ids.Contains(key)).ToList())
                ReleaseEndpoint(stale);

            _renderEndpointIds = ids;
            _renderEndpointsStamp = Stopwatch.GetTimestamp();
            return ids;
        }
        catch (Exception ex)
        {
            LogOnce("endpoint-enumerate", ex);
            // Better to keep working with the endpoints we knew about than to report none.
            return _renderEndpointIds;
        }
    }

    /// <summary>
    /// The session manager of one endpoint, reusing the cached one. NAudio offers no way
    /// to release an AudioSessionManager deterministically, so building a new one per call
    /// leaves a COM wrapper behind for the garbage collector — with the mixer polling
    /// every 750 ms that shows up as a steadily climbing handle count.
    /// </summary>
    private AudioSessionManager? AcquireSessionManager(string endpointId)
    {
        if (_sessionCache.TryGetValue(endpointId, out CachedEndpoint? cached))
            return cached.Manager;

        try
        {
            using MMDeviceEnumerator enumerator = new();
            MMDevice device = enumerator.GetDevice(endpointId);
            CachedEndpoint entry = new(device, device.AudioSessionManager);
            _sessionCache[endpointId] = entry;
            return entry.Manager;
        }
        catch (Exception ex)
        {
            LogOnce("session-device", ex);
            return null;
        }
    }

    private void ReleaseEndpoint(string endpointId)
    {
        if (!_sessionCache.Remove(endpointId, out CachedEndpoint? cached)) return;
        // The manager is owned by the device and has no Dispose of its own.
        cached.Device.Dispose();
    }

    /// <summary>Drops the names of processes that no longer own a session.</summary>
    private void PruneProcessNames(HashSet<uint> livePids)
    {
        if (livePids.Count == 0)
        {
            _appIdByPid.Clear();
            return;
        }

        foreach (uint pid in _appIdByPid.Keys.Where(pid => !livePids.Contains(pid)).ToList())
            _appIdByPid.Remove(pid);
    }

    /// <summary>Releases every cached session device. Called by the plugin on shutdown.</summary>
    public void Dispose()
    {
        lock (_sessionLock)
        {
            foreach (string id in _sessionCache.Keys.ToList()) ReleaseEndpoint(id);
            _appIdByPid.Clear();
            _renderEndpointIds = [];
            _renderEndpointsStamp = 0;
        }
    }

    /// <summary>
    /// Process executable name, lower-cased, without extension — see AudioSessionInfo.
    /// The system-sounds session has no process of its own and gets a fixed id.
    /// </summary>
    private string ResolveAppId(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession) return SystemSoundsAppId;

        uint pid = session.GetProcessID;
        if (pid == 0) return string.Empty;

        if (_appIdByPid.TryGetValue(pid, out string? known)) return known;

        // Cached because the managed fallback costs about 2 ms per call and the mixer
        // re-reads every session of every endpoint several times a second. A pid is only
        // reused once its process is gone, and its sessions go with it, so a stale entry
        // cannot outlive the poll that prunes it.
        string name = QueryProcessName(pid);
        _appIdByPid[pid] = name;
        return name;
    }

    private string QueryProcessName(uint pid)
    {
        // Roughly twenty times cheaper than Process.GetProcessById, and it succeeds for
        // every process that owns an audio session. The managed call stays as a fallback
        // for the ones it cannot open.
        string? imagePath = NativeMethods.QueryProcessImagePath(pid);
        if (imagePath != null)
            return Path.GetFileNameWithoutExtension(imagePath).ToLowerInvariant();

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception)
        {
            // The process died between enumeration and the lookup — nothing to control.
            return string.Empty;
        }
    }

    private static string ResolveDisplayName(AudioSessionControl session, string appId)
    {
        if (appId == SystemSoundsAppId) return "System Sounds";

        try
        {
            string name = session.DisplayName;
            // Some sessions report an unexpanded resource reference such as
            // "@%SystemRoot%\System32\AudioSrv.Dll,-202", which reads worse than the AppId.
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) return name;
        }
        catch (Exception)
        {
            // Some sessions expose no display name; the AppId is the better fallback.
        }
        return appId;
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

    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle process, int flags, StringBuilder buffer, ref int size);

        /// <summary>Enough to read the image name, and granted for processes we cannot open fully.</summary>
        private const int ProcessQueryLimitedInformation = 0x1000;

        /// <summary>Full image path of a process, or null when it cannot be opened.</summary>
        public static string? QueryProcessImagePath(uint processId)
        {
            using SafeProcessHandle handle =
                OpenProcess(ProcessQueryLimitedInformation, false, (int)processId);
            if (handle.IsInvalid) return null;

            StringBuilder buffer = new(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
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

        /// <summary>The file this playback was started with; empty until <see cref="Start"/>.</summary>
        public string FilePath { get; private set; } = string.Empty;

        public void Start(string filePath, Action<Playback> onFinished)
        {
            FilePath = filePath;
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
