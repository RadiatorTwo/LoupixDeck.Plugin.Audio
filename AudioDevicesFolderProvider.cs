using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Top-level folder for the Windows-Audio command. Lists each active endpoint of
/// the chosen kind and lets the user open a sub-folder to control volume/mute.
/// </summary>
public sealed class AudioDevicesFolderProvider : FolderProviderBase
{
    private readonly IAudioService _audio;
    private readonly AudioEndpointKind _kind;
    private readonly AudioAliasStore _aliasStore;
    private readonly FolderGridInfo _grid;

    public AudioDevicesFolderProvider(IAudioService audio, AudioEndpointKind kind, AudioAliasStore aliasStore,
        FolderGridInfo grid)
    {
        _audio = audio;
        _kind = kind;
        _aliasStore = aliasStore;
        _grid = grid;
    }

    public override string Title =>
        _kind == AudioEndpointKind.Render ? "Output Devices" : "Input Devices";

    public override void OnEnter() => _aliasStore.Changed += RaiseEntriesChanged;

    public override void OnExit() => _aliasStore.Changed -= RaiseEntriesChanged;

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        var endpoints = _audio.GetEndpoints(_kind);
        var entries = new List<FolderEntry>(endpoints.Count);

        // Fill the slots in reading order, skipping the reserved back-button slot.
        var index = 0;
        foreach (var ep in endpoints)
        {
            var slot = _grid.SlotForIndex(index++);
            if (slot < 0) break; // grid full

            var capturedEp = ep;
            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                Text = _aliasStore.Resolve(capturedEp),
                BackColor = capturedEp.IsDefault
                    ? PluginColor.FromRgb(0x20, 0x60, 0x30)
                    : PluginColor.FromRgb(0x20, 0x20, 0x40),
                TextSize = 13,
                Bold = capturedEp.IsDefault,
                OpensFolder = new AudioDeviceControlFolderProvider(_audio, capturedEp, _kind, _aliasStore)
            });
        }
        return entries;
    }
}
