using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Plays the audio file assigned to the button on the globally configured playback
/// device. Each press starts its own playback, so presses overlap — unless "stop on
/// second press" is enabled, where a press while the sound runs stops it instead.
/// </summary>
internal sealed class AudioPlaySoundCommand(
    IAudioService audio,
    SoundLibrary library,
    PlaybackDeviceStore playbackDevices,
    IPluginHost host) : IPluginCommand
{
    /// <summary>
    /// Name of the single parameter carrying the sound token. Exactly one parameter on
    /// purpose: the host drops empty pieces when splitting a parameter list, which would
    /// shift the positions of any following parameter.
    /// </summary>
    public const string SoundParameterName = "sound";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.PlaySound",
        DisplayName = "Audio: Play Sound",
        Group = "Audio",
        Icon = "\U000F075A",
        Description = "Play an assigned audio file on the configured device",
        HiddenFromMenu = true,
        ParameterTemplate = "({sound})",
        // No DefaultValue here: a command-defined default outranks the value the menu
        // supplies (see CommandBuilder), which would discard the picked file.
        Parameters = [new CommandParameter(SoundParameterName, typeof(string))]
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.SimpleButton | ButtonTargets.TouchButton | ButtonTargets.RotaryEncoder;

    public Task Execute(CommandContext ctx)
    {
        try
        {
            string[] parameters = ctx.Parameters;
            string? token = parameters.Length > 0 ? parameters[0] : null;

            string? path = library.Resolve(token);
            if (path == null)
            {
                host.Logger?.Warn(
                    $"Audio.PlaySound: no playable file for '{token}'. " +
                    "Check the sound folder in the plugin settings.");
                return Task.CompletedTask;
            }

            // The running playbacks are the toggle state: once the sound has ended on its
            // own there is nothing to stop, and the press starts it again.
            if (library.StopOnSecondPress && audio.StopFile(path)) return Task.CompletedTask;

            audio.PlayFile(path, playbackDevices.SelectedId);
        }
        catch (Exception ex)
        {
            host.Logger?.Error("Audio.PlaySound failed", ex);
        }

        return Task.CompletedTask;
    }
}
