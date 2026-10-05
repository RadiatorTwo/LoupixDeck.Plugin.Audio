using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Rewrites of bindings saved by earlier versions of this plugin.</summary>
public sealed partial class AudioPlugin
{
    /// <summary>
    /// Moves dials the user configured before <c>Audio.Volume</c> existed onto it. Without this
    /// they would sit on the old triad forever: it still works, but a dial there runs one
    /// command per detent and cannot report its level, and nothing would ever retire the old
    /// shape. The host runs each rule at most once per config and only on a dial that carries
    /// exactly the old binding, so a dial the user has since composed differently is safe.
    /// </summary>
    public override IEnumerable<CommandMigration> GetCommandMigrations()
    {
        yield return new CommandMigration
        {
            Id = "volume-dial",
            From = new Dictionary<RotaryAction, string>
            {
                [RotaryAction.CounterClockwise] = "Audio.VolumeDown",
                [RotaryAction.Clockwise] = "Audio.VolumeUp",
                [RotaryAction.Press] = "Audio.MuteToggle",
            },
            To = "Audio.Volume",
            // The endpoint and the user's step size carry over; the mute command never had a
            // step, so the value comes from whichever turn slot declared one.
            Parameters = new Dictionary<string, string>
            {
                [AudioDeviceParameter.DeviceIdName] = $"{{{AudioDeviceParameter.DeviceIdName}}}",
                [AudioDeviceParameter.StepName] = $"{{{AudioDeviceParameter.StepName}}}",
            },
        };

        yield return new CommandMigration
        {
            Id = "app-volume-dial",
            From = new Dictionary<RotaryAction, string>
            {
                [RotaryAction.CounterClockwise] = "Audio.AppVolumeDown",
                [RotaryAction.Clockwise] = "Audio.AppVolumeUp",
                [RotaryAction.Press] = "Audio.AppMuteToggle",
            },
            To = "Audio.AppVolume",
            Parameters = new Dictionary<string, string>
            {
                [AudioAppParameter.AppIdName] = $"{{{AudioAppParameter.AppIdName}}}",
                [AudioAppParameter.StepName] = $"{{{AudioAppParameter.StepName}}}",
            },
        };
    }
}
