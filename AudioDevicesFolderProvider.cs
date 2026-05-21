using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Top-level folder for the Windows-Audio command. Lists each active endpoint of
/// the chosen kind and lets the user open a sub-folder to control volume/mute.
/// </summary>
public sealed class AudioDevicesFolderProvider : FolderProviderBase
{
    private readonly IWindowsAudioService _audio;
    private readonly AudioEndpointKind _kind;

    public AudioDevicesFolderProvider(IWindowsAudioService audio, AudioEndpointKind kind)
    {
        _audio = audio;
        _kind = kind;
    }

    public override string Title =>
        _kind == AudioEndpointKind.Render ? "Output Devices" : "Input Devices";

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        var endpoints = _audio.GetEndpoints(_kind);
        var entries = new List<FolderEntry>(endpoints.Count);

        // Fill the slots in order, skipping the reserved back-button slot.
        var slot = 0;
        foreach (var ep in endpoints)
        {
            if (slot == FolderLayout.BackSlotIndex) slot++;
            if (slot >= FolderLayout.TotalSlots) break;

            var capturedEp = ep;
            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                Text = ShortenName(capturedEp.FriendlyName),
                BackColor = capturedEp.IsDefault
                    ? PluginColor.FromRgb(0x20, 0x60, 0x30)
                    : PluginColor.FromRgb(0x20, 0x20, 0x40),
                TextSize = 13,
                Bold = capturedEp.IsDefault,
                OpensFolder = new AudioDeviceControlFolderProvider(_audio, capturedEp)
            });
            slot++;
        }
        return entries;
    }

    private static string ShortenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Friendly names look like "Speakers (Realtek(R) Audio)" — keep the head, drop the tail.
        var paren = name.IndexOf('(');
        return paren > 1 ? name[..paren].TrimEnd() : name;
    }
}
