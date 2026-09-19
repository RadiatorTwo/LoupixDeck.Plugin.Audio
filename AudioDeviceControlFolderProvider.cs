using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Sub-folder shown after the user picks a specific audio endpoint. Slot 0 shows
/// the live volume, slot 1 toggles mute. The first rotary adjusts volume by 2%.
/// </summary>
public sealed class AudioDeviceControlFolderProvider : FolderProviderBase
{
    private const float StepScalar = 0.02f;

    private readonly IAudioService _audio;
    private readonly AudioEndpointInfo _endpoint;
    private readonly AudioEndpointKind _kind;
    private readonly AudioAliasStore _aliasStore;
    private readonly IPluginHost _host;

    private IDisposable? _subscription;
    private float _currentVolume;
    private bool _currentMute;

    private readonly Dictionary<int, RotaryOverride> _rotaries;

    public AudioDeviceControlFolderProvider(IAudioService audio, AudioEndpointInfo endpoint,
        AudioEndpointKind kind, AudioAliasStore aliasStore, IPluginHost host)
    {
        _audio = audio;
        _endpoint = endpoint;
        _kind = kind;
        _aliasStore = aliasStore;
        _host = host;

        _rotaries = new Dictionary<int, RotaryOverride>
        {
            [0] = new RotaryOverride
            {
                OnLeft = () => { AdjustVolume(-StepScalar); return Task.CompletedTask; },
                OnRight = () => { AdjustVolume(+StepScalar); return Task.CompletedTask; },
                OnPress = () => { ToggleMute(); return Task.CompletedTask; }
            }
        };
    }

    public override string Title => _aliasStore.Resolve(_endpoint);

    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => _rotaries;

    public override void OnEnter()
    {
        _currentVolume = _audio.GetVolume(_endpoint.Id);
        _currentMute = _audio.GetMute(_endpoint.Id);

        _subscription = _audio.SubscribeVolumeChanges(_endpoint.Id, (vol, muted) =>
        {
            _currentVolume = vol;
            _currentMute = muted;
            RaiseEntriesChanged();
        });

        _aliasStore.Changed += RaiseEntriesChanged;
    }

    public override void OnExit()
    {
        _aliasStore.Changed -= RaiseEntriesChanged;

        _subscription?.Dispose();
        _subscription = null;
    }

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        var percent = (int)Math.Round(_currentVolume * 100f);
        bool isDefault = IsCurrentDefault();

        return new[]
        {
            new FolderEntry
            {
                SlotIndex = 0,
                Text = $"{percent} %",
                BackColor = PluginColor.FromRgb(0x20, 0x40, 0x60),
                TextSize = 22,
                Bold = true
            },
            new FolderEntry
            {
                SlotIndex = 1,
                Text = _host.Tr(_currentMute ? "Unmute" : "Mute"),
                BackColor = _currentMute
                    ? PluginColor.FromRgb(0x70, 0x20, 0x20)
                    : PluginColor.FromRgb(0x30, 0x30, 0x30),
                TextSize = 16,
                OnPress = () => { ToggleMute(); return Task.CompletedTask; }
            },
            new FolderEntry
            {
                SlotIndex = 2,
                Text = _host.Tr(isDefault ? "Is Default" : "Set Default"),
                BackColor = isDefault
                    ? PluginColor.FromRgb(0x20, 0x60, 0x30)
                    : PluginColor.FromRgb(0x30, 0x30, 0x30),
                TextSize = 16,
                OnPress = () =>
                {
                    if (!IsCurrentDefault()) _audio.SetDefaultEndpoint(_endpoint.Id);
                    RaiseEntriesChanged();
                    return Task.CompletedTask;
                }
            }
        };
    }

    /// <summary>
    /// Live default check. The captured endpoint record's IsDefault is a snapshot taken
    /// when the parent folder was built, so it goes stale the moment the default changes.
    /// </summary>
    private bool IsCurrentDefault() =>
        _audio.GetEndpoints(_kind).Any(e =>
            string.Equals(e.Id, _endpoint.Id, StringComparison.Ordinal) && e.IsDefault);

    private void AdjustVolume(float delta)
    {
        var next = Math.Clamp(_currentVolume + delta, 0f, 1f);
        _audio.SetVolume(_endpoint.Id, next);
        // Don't optimistically update — the volume notification callback refreshes the UI.
    }

    private void ToggleMute()
    {
        _audio.SetMute(_endpoint.Id, !_currentMute);
        // Notification callback will refresh.
    }
}
