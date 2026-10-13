using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// macOS implementation backed by CoreAudio. Covers the devices, their volume and mute and the
/// default output and input. macOS has no API for the volume of another application's audio, so
/// the mixer and the per-app commands have nothing to work with here.
/// </summary>
public sealed partial class MacAudioService : IAudioService
{
    private const string OutputPrefix = "output:";
    private const string InputPrefix = "input:";

    /// <summary>The channels tried when a device has no main volume control (left and right).</summary>
    private static readonly uint[] StereoChannels = [1, 2];

    public IPluginLogger? Logger { get; set; }

    public bool IsSupported => true;

    /// <summary>CoreAudio is part of the operating system, and sounds play through afplay, which is too.</summary>
    public IReadOnlyList<PluginRequirement> GetRequirements() => [];

    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind)
    {
        uint scope = Scope(kind);
        uint defaultId = DefaultDeviceId(kind);
        List<AudioEndpointInfo> result = [];

        foreach (uint deviceId in GetDeviceIds())
        {
            if (!IsSelectable(deviceId, scope)) continue;

            string? uid = TryGetString(deviceId, PropUid);
            if (string.IsNullOrEmpty(uid)) continue;

            string name = TryGetString(deviceId, PropName) ?? uid;
            result.Add(new AudioEndpointInfo(EndpointId(kind, uid), name, deviceId == defaultId));
        }

        return result;
    }

    public string? GetDefaultEndpointId(AudioEndpointKind kind)
    {
        uint deviceId = DefaultDeviceId(kind);
        if (deviceId == 0) return null;

        string? uid = TryGetString(deviceId, PropUid);
        return string.IsNullOrEmpty(uid) ? null : EndpointId(kind, uid);
    }

    public float GetVolume(string endpointId)
    {
        if (!TryResolve(endpointId, out uint deviceId, out uint scope)) return 0f;

        if (TryGetVirtualMainVolume(deviceId, scope, out float main)) return main;
        if (TryGetFloat(deviceId, PropVolumeScalar, scope, ElementMain, out float level)) return level;

        // Some devices only have per-channel controls; report the louder side, as the slider does.
        float? loudest = null;
        foreach (uint channel in StereoChannels)
        {
            if (TryGetFloat(deviceId, PropVolumeScalar, scope, channel, out float value))
                loudest = Math.Max(loudest ?? 0f, value);
        }

        // A device without any volume control (S/PDIF, HDMI) plays at a fixed full level.
        return loudest ?? 1f;
    }

    public void SetVolume(string endpointId, float scalar01)
    {
        if (!TryResolve(endpointId, out uint deviceId, out uint scope)) return;

        float clamped = Math.Clamp(scalar01, 0f, 1f);
        if (TrySetVirtualMainVolume(deviceId, scope, clamped)) return;

        if (IsSettable(deviceId, PropVolumeScalar, scope, ElementMain))
        {
            TrySetFloat(deviceId, PropVolumeScalar, scope, ElementMain, clamped);
            return;
        }

        foreach (uint channel in StereoChannels)
        {
            if (IsSettable(deviceId, PropVolumeScalar, scope, channel))
                TrySetFloat(deviceId, PropVolumeScalar, scope, channel, clamped);
        }
    }

    public bool GetMute(string endpointId) =>
        TryResolve(endpointId, out uint deviceId, out uint scope)
        && TryGetUInt32(deviceId, PropMute, scope, out uint muted)
        && muted != 0;

    public void SetMute(string endpointId, bool muted)
    {
        if (!TryResolve(endpointId, out uint deviceId, out uint scope)) return;
        if (IsSettable(deviceId, PropMute, scope, ElementMain))
            TrySetUInt32(deviceId, PropMute, scope, muted ? 1u : 0u);
    }

    public bool SetDefaultEndpoint(string endpointId)
    {
        (AudioEndpointKind kind, string uid) = SplitId(endpointId);
        uint deviceId = TranslateUid(uid);
        if (deviceId == 0) return false;

        uint selector = kind == AudioEndpointKind.Render ? PropDefaultOutput : PropDefaultInput;
        if (!TrySetUInt32(SystemObject, selector, ScopeGlobal, deviceId))
        {
            Logger?.Warn($"Audio: could not make '{uid}' the default {(kind == AudioEndpointKind.Render ? "output" : "input")}.");
            return false;
        }

        return DefaultDeviceId(kind) == deviceId;
    }

    /// <summary>Nothing to drop: every answer is read from CoreAudio, which is cheap, when asked.</summary>
    public void RefreshDevices() { }

    // macOS offers no public API to read or change the level of another application's audio.
    public IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId) => [];
    public float? GetSessionVolume(string? endpointId, string appId) => null;
    public void SetSessionVolume(string? endpointId, string appId, float scalar01) { }
    public bool? GetSessionMute(string? endpointId, string appId) => null;
    public void SetSessionMute(string? endpointId, string appId, bool muted) { }
    public string? GetForegroundAppId() => null;

    // --- helpers ---------------------------------------------------------

    /// <summary>
    /// IDs are the CoreAudio device UID with the direction in front ("output:UID", "input:UID"): a
    /// headset is one device with both directions, and the UID alone would not say which is meant.
    /// The UID stays the same across reconnects and restarts; the numeric device id does not.
    /// </summary>
    private static string EndpointId(AudioEndpointKind kind, string uid) =>
        (kind == AudioEndpointKind.Render ? OutputPrefix : InputPrefix) + uid;

    private static (AudioEndpointKind Kind, string Uid) SplitId(string id)
    {
        if (id.StartsWith(InputPrefix, StringComparison.Ordinal))
            return (AudioEndpointKind.Capture, id[InputPrefix.Length..]);
        if (id.StartsWith(OutputPrefix, StringComparison.Ordinal))
            return (AudioEndpointKind.Render, id[OutputPrefix.Length..]);
        return (AudioEndpointKind.Render, id);
    }

    private static bool TryResolve(string endpointId, out uint deviceId, out uint scope)
    {
        (AudioEndpointKind kind, string uid) = SplitId(endpointId);
        scope = Scope(kind);
        deviceId = TranslateUid(uid);
        return deviceId != 0;
    }

    private static uint DefaultDeviceId(AudioEndpointKind kind) =>
        TryGetUInt32(SystemObject, kind == AudioEndpointKind.Render ? PropDefaultOutput : PropDefaultInput,
            ScopeGlobal, out uint deviceId) ? deviceId : 0;

    /// <summary>
    /// Whether the device belongs in the list for that direction: the ones the Sound menu offers.
    /// Hidden devices and the ones that cannot be a default (an aggregate's private sub-devices)
    /// are left out.
    /// </summary>
    private static bool IsSelectable(uint deviceId, uint scope)
    {
        if (!HasStreams(deviceId, scope)) return false;
        if (TryGetUInt32(deviceId, PropHidden, ScopeGlobal, out uint hidden) && hidden != 0) return false;
        return !TryGetUInt32(deviceId, PropCanBeDefault, scope, out uint canBeDefault) || canBeDefault != 0;
    }
}
