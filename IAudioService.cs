namespace LoupixDeck.Plugin.Audio;

public enum AudioEndpointKind { Render, Capture }

public sealed record AudioEndpointInfo(string Id, string FriendlyName, bool IsDefault);

public interface IAudioService
{
    bool IsSupported { get; }

    IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind);

    /// <summary>0..1 scalar.</summary>
    float GetVolume(string endpointId);

    /// <summary>0..1 scalar — clamped internally.</summary>
    void SetVolume(string endpointId, float scalar01);

    bool GetMute(string endpointId);
    void SetMute(string endpointId, bool muted);

    /// <summary>
    /// Subscribes to volume/mute notifications for an endpoint. Dispose the returned token
    /// to unsubscribe.
    /// </summary>
    IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange);

    /// <summary>
    /// Starts playing a file on the given render endpoint, or on the system default when
    /// <paramref name="endpointId"/> is null. Returns immediately; every call starts its own
    /// playback, so repeated presses overlap. Throws when the playback cannot be started.
    /// </summary>
    void PlayFile(string filePath, string? endpointId);

    /// <summary>Stops and releases every playback started by <see cref="PlayFile"/>.</summary>
    void StopAllPlayback();

    /// <summary>
    /// Stops every running playback of <paramref name="filePath"/> and reports whether any
    /// was running. The running playbacks are the toggle state of "stop on second press":
    /// a sound that ended on its own has already left the list, so the next press starts it
    /// again instead of being swallowed as a stop.
    /// </summary>
    bool StopFile(string filePath);

    /// <summary>
    /// Live mixer sessions of the given render endpoint, one entry per distinct
    /// application. Pass null to use the current default output. Sessions belonging to
    /// the host process itself are excluded. Returns an empty list when unsupported.
    /// </summary>
    IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId);

    /// <summary>0..1 scalar of the loudest session of that app, or null when the app has no session.</summary>
    float? GetSessionVolume(string? endpointId, string appId);

    /// <summary>Sets every session of that app to the given 0..1 scalar (clamped internally).</summary>
    void SetSessionVolume(string? endpointId, string appId, float scalar01);

    /// <summary>True when every session of that app is muted; null when the app has no session.</summary>
    bool? GetSessionMute(string? endpointId, string appId);

    /// <summary>Sets the mute flag on every session of that app.</summary>
    void SetSessionMute(string? endpointId, string appId, bool muted);

    /// <summary>
    /// AppId of the application owning the foreground window, or null when it cannot be
    /// determined. On Linux this covers X11 and XWayland windows (resolved through xprop); a
    /// native Wayland window exposes no such property, so it resolves to null.
    /// </summary>
    string? GetForegroundAppId();

    /// <summary>
    /// Makes the endpoint the system default for its kind. Returns false when the switch
    /// failed; the caller must treat that as non-fatal.
    /// </summary>
    bool SetDefaultEndpoint(string endpointId);
}
