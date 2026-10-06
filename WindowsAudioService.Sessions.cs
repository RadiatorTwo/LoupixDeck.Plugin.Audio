using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Per-application mixer sessions and the foreground application.</summary>
public sealed partial class WindowsAudioService
{
    /// <summary>AppId of the Windows system-sounds session, which has no process of its own.</summary>
    private const string SystemSoundsAppId = AudioSessionNames.SystemSoundsAppId;

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
    private readonly Dictionary<uint, string> _imagePathByPid = [];

    private sealed record CachedEndpoint(MMDevice Device, AudioSessionManager Manager);

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
                byApp[appId] = new AudioSessionInfo(appId, displayName, volume, muted,
                    ImagePathOf(session.GetProcessID));
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
            _imagePathByPid.Clear();
            return;
        }

        foreach (uint pid in _appIdByPid.Keys.Where(pid => !livePids.Contains(pid)).ToList())
        {
            _appIdByPid.Remove(pid);
            _imagePathByPid.Remove(pid);
        }
    }

    /// <summary>Releases every cached session and endpoint device. Called by the plugin on shutdown.</summary>
    public void Dispose()
    {
        DisposeEndpointCache();
        ReleaseSessionCache();
    }

    public void RefreshDevices()
    {
        ReleaseSessionCache();
        ReleaseVolumeDevices();
        lock (_friendlyNameLock) _friendlyNames.Clear();
        lock (_lastEndpointsLock) _lastEndpoints.Clear();
    }

    private void ReleaseSessionCache()
    {
        lock (_sessionLock)
        {
            foreach (string id in _sessionCache.Keys.ToList()) ReleaseEndpoint(id);
            _appIdByPid.Clear();
            _imagePathByPid.Clear();
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

    /// <summary>The executable of a process whose name was already resolved, or null (system sounds, unreadable process).</summary>
    private string? ImagePathOf(uint pid) => _imagePathByPid.GetValueOrDefault(pid);

    private string QueryProcessName(uint pid)
    {
        // Roughly twenty times cheaper than Process.GetProcessById, and it succeeds for
        // every process that owns an audio session. The managed call stays as a fallback
        // for the ones it cannot open.
        string? imagePath = NativeMethods.QueryProcessImagePath(pid);
        if (imagePath != null)
        {
            _imagePathByPid[pid] = imagePath;
            return Path.GetFileNameWithoutExtension(imagePath).ToLowerInvariant();
        }

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
        if (appId == SystemSoundsAppId) return AudioSessionNames.SystemSounds;

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
}
