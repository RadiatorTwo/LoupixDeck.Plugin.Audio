using System.Diagnostics;
using System.Runtime.InteropServices;
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

    // Cached session device — see AcquireSessionManager for why it is not built per call.
    private readonly Lock _sessionLock = new();
    private MMDevice? _sessionDevice;
    private AudioSessionManager? _sessionManager;
    private string? _sessionDeviceId;

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
    /// Walks the active sessions of one render endpoint (the current default when
    /// <paramref name="endpointId"/> is null), handing each one to <paramref name="visit"/>
    /// together with its AppId and display name. The session objects are owned by the
    /// collection and must not be used after this method returns.
    /// </summary>
    private void ForEachSession(string? endpointId,
        Action<AudioSessionControl, string, string> visit)
    {
        lock (_sessionLock)
        {
            AudioSessionManager? manager = AcquireSessionManager(endpointId);
            if (manager == null) return;

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

                        string appId = ResolveAppId(session);
                        if (appId.Length == 0) continue;

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
                // The cached device is the likely culprit (unplugged, or the driver
                // restarted), so drop it and let the next call build a fresh one.
                ReleaseSessionDevice();
            }
        }
    }

    /// <summary>
    /// The session manager of the wanted endpoint, reusing the cached one while it still
    /// points at the same device. NAudio offers no way to release an AudioSessionManager
    /// deterministically, so building a new one per call leaves a COM wrapper behind for
    /// the garbage collector — with the mixer polling every 750 ms that shows up as a
    /// steadily climbing handle count.
    /// </summary>
    private AudioSessionManager? AcquireSessionManager(string? endpointId)
    {
        string? wantedId = ResolveEndpointId(endpointId);
        if (wantedId == null) return null;

        if (_sessionManager != null && string.Equals(_sessionDeviceId, wantedId, StringComparison.Ordinal))
            return _sessionManager;

        ReleaseSessionDevice();

        try
        {
            using MMDeviceEnumerator enumerator = new();
            MMDevice device = enumerator.GetDevice(wantedId);
            _sessionDevice = device;
            _sessionDeviceId = wantedId;
            _sessionManager = device.AudioSessionManager;
            return _sessionManager;
        }
        catch (Exception ex)
        {
            LogOnce("session-device", ex);
            ReleaseSessionDevice();
            return null;
        }
    }

    /// <summary>Id of the wanted render endpoint, resolving null to the current default.</summary>
    private string? ResolveEndpointId(string? endpointId)
    {
        if (!string.IsNullOrWhiteSpace(endpointId)) return endpointId;

        try
        {
            using MMDeviceEnumerator enumerator = new();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex)
        {
            LogOnce("default-endpoint", ex);
            return null;
        }
    }

    private void ReleaseSessionDevice()
    {
        // The manager is owned by the device and has no Dispose of its own.
        _sessionManager = null;
        _sessionDevice?.Dispose();
        _sessionDevice = null;
        _sessionDeviceId = null;
    }

    /// <summary>Releases the cached session device. Called by the plugin on shutdown.</summary>
    public void Dispose()
    {
        lock (_sessionLock) ReleaseSessionDevice();
    }

    /// <summary>Process executable name, lower-cased, without extension — see AudioSessionInfo.</summary>
    private static string ResolveAppId(AudioSessionControl session)
    {
        try
        {
            using Process process = Process.GetProcessById((int)session.GetProcessID);
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
        try
        {
            string name = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
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
