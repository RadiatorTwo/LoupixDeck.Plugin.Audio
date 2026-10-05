using System.Globalization;
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
    /// Name of the first parameter, carrying the sound token. It must stay first: the host
    /// drops empty pieces when splitting a parameter list, which shifts the positions of the
    /// parameters after an empty one, and the token is never empty.
    /// </summary>
    public const string SoundParameterName = "sound";

    /// <summary>
    /// Name of the second parameter, the sound's own playback level in percent. A binding
    /// saved before it existed has no second parameter and plays at full level, as before.
    /// </summary>
    public const string VolumeParameterName = "volume";

    private const int DefaultVolumePercent = 100;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.PlaySound",
        DisplayName = "Audio: Play Sound",
        Group = "Audio",
        Icon = AudioButtonLayouts.Sound,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Sound, null),
        Description = "Play an assigned audio file on the configured device",
        HiddenFromMenu = true,
        ParameterTemplate = "({sound},{volume})",
        Parameters =
        [
            // No DefaultValue here: a command-defined default outranks the value the menu
            // supplies (see CommandBuilder), which would discard the picked file.
            new CommandParameter(SoundParameterName, typeof(string)),
            // The menu supplies no level, so this default is what a new binding starts with.
            new CommandParameter(VolumeParameterName, typeof(int))
            {
                DefaultValue = DefaultVolumePercent.ToString(CultureInfo.InvariantCulture)
            }
        ]
    };

    /// <summary>The binding's level as a 0..1 scalar; full level when absent or not a number.</summary>
    private static float ResolveVolume(string[] parameters)
    {
        int percent = DefaultVolumePercent;
        if (parameters.Length > 1 &&
            int.TryParse(parameters[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            percent = Math.Clamp(parsed, 0, 100);
        }

        return percent / 100f;
    }

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

            audio.PlayFile(path, playbackDevices.SelectedId, ResolveVolume(parameters));
        }
        catch (Exception ex)
        {
            host.Logger?.Error("Audio.PlaySound failed", ex);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Stops every sound this plugin is currently playing, regardless of the
/// "stop on second press" setting — that option toggles a single sound, this command is
/// the panic button for all of them at once.
/// </summary>
internal sealed class AudioStopSoundCommand(IAudioService audio, IPluginHost host) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.StopSounds",
        DisplayName = "Audio: Stop Sounds",
        Group = "Audio",
        Icon = AudioButtonLayouts.Stop,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Stop, "Stop Sounds"),
        Description = "Stop every sound started by Play Sound",
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.SimpleButton | ButtonTargets.TouchButton | ButtonTargets.RotaryEncoder;

    public Task Execute(CommandContext ctx)
    {
        try
        {
            // Only this plugin's own playbacks are affected; audio of other applications
            // is untouched. Nothing running makes this a no-op.
            audio.StopAllPlayback();
        }
        catch (Exception ex)
        {
            host.Logger?.Error("Audio.StopSounds failed", ex);
        }

        return Task.CompletedTask;
    }
}
