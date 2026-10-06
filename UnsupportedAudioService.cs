using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

internal sealed class UnsupportedAudioService : IAudioService
{
    public bool IsSupported => false;
    public IReadOnlyList<PluginRequirement> GetRequirements() => [];
    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(AudioEndpointKind kind) => [];
    public string? GetDefaultEndpointId(AudioEndpointKind kind) => null;
    public float GetVolume(string endpointId) => 0f;
    public void SetVolume(string endpointId, float scalar01) { }
    public bool GetMute(string endpointId) => false;
    public void SetMute(string endpointId, bool muted) { }
    public IDisposable SubscribeVolumeChanges(string endpointId, Action<float, bool> onChange)
        => NoopDisposable.Instance;
    public void PlayFile(string filePath, string? endpointId, float volume) { }
    public void StopAllPlayback() { }
    public bool StopFile(string filePath) => false;
    public IReadOnlyList<AudioSessionInfo> GetSessions(string? endpointId) => [];
    public float? GetSessionVolume(string? endpointId, string appId) => null;
    public void SetSessionVolume(string? endpointId, string appId, float scalar01) { }
    public bool? GetSessionMute(string? endpointId, string appId) => null;
    public void SetSessionMute(string? endpointId, string appId, bool muted) { }
    public string? GetForegroundAppId() => null;
    public bool SetDefaultEndpoint(string endpointId) => false;
    public void RefreshDevices() { }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
