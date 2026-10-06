using System.Diagnostics;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The buttons under the settings page. The host translates each label; the text an action
/// returns is composed here, so it goes through <see cref="Tr"/>. A failure is thrown, which the
/// host shows as "Failed: …" in its error style.
/// </summary>
public sealed partial class AudioPlugin
{
    /// <summary>Level of the test sound: loud enough to hear, not a shock at full device volume.</summary>
    private const float TestToneVolume = 0.5f;

    public IReadOnlyList<PluginSettingAction> SettingsActions => _audio.IsSupported
        ?
        [
            new PluginSettingAction { Label = "Play test sound", Invoke = PlayTestSound },
            new PluginSettingAction { Label = "Refresh devices", Invoke = RefreshDevices },
            new PluginSettingAction { Label = "Open sound folder", Invoke = OpenSoundFolder }
        ]
        : [];

    /// <summary>Plays a short beep on the current default output.</summary>
    private Task<string> PlayTestSound() => Task.Run(() =>
    {
        try
        {
            _audio.PlayFile(TestTone.EnsureFile(), endpointId: null, TestToneVolume);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Audio: test sound failed: {ex.Message}");
            throw new InvalidOperationException(ex.Message, ex);
        }

        return Tr("Playing a test sound on the default output");
    });

    /// <summary>
    /// Drops every cached device answer and reads the endpoints again. The host rebuilds the
    /// settings form after an action, so a device that only now shows up gets its fields at once.
    /// </summary>
    private Task<string> RefreshDevices() => Task.Run(() =>
    {
        _audio.RefreshDevices();
        AudioDeviceParameter.InvalidateDefaultEndpoint();
        AudioCurrentOutputCommand.InvalidateLabel();

        int outputs = Endpoints(AudioEndpointKind.Render).Count;
        int inputs = Endpoints(AudioEndpointKind.Capture).Count;
        // The counts are values, so only the fixed part is a key.
        return string.Format(Tr("Found {0} outputs and {1} inputs"), outputs, inputs);
    });

    /// <summary>Opens the configured sound folder in the system file manager.</summary>
    private Task<string> OpenSoundFolder()
    {
        string? configured = _soundLibrary?.ConfiguredFolder;
        if (configured == null)
            throw new InvalidOperationException(Tr("Set a sound folder in the Audio plugin settings"));

        string? folder = _soundLibrary?.FolderPath;
        if (folder == null)
            throw new InvalidOperationException(string.Format(Tr("Sound folder not found: {0}"), configured));

        ProcessStartInfo psi = OperatingSystem.IsWindows()
            // Shell execution of a folder opens it in Explorer.
            ? new ProcessStartInfo(folder) { UseShellExecute = true }
            : new ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false };

        try
        {
            using Process? _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Audio: could not open the sound folder '{folder}': {ex.Message}");
            throw new InvalidOperationException(ex.Message, ex);
        }

        return Task.FromResult(Tr("Sound folder opened"));
    }
}
