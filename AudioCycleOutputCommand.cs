using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Makes the next output device the system default, so one button switches between speakers and
/// headset. Without a device list it walks the devices the picker shows (the ones not hidden in the
/// settings); with one it walks exactly those, in the order given. The button shows the device in
/// use, like <see cref="AudioCurrentOutputCommand"/>, so a press is answered by the new name.
/// </summary>
internal sealed class AudioCycleOutputCommand(
    IAudioService audio, AudioAliasStore aliasStore, AudioVisibilityStore visibility) : IDisplayCommand
{
    public const string DevicesName = "devices";

    /// <summary>
    /// Separates the entries of the device list. Not a comma: the host splits a binding's parameters
    /// on commas, so a comma-separated list would arrive as several parameters.
    /// </summary>
    public const char DeviceSeparator = '|';

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.CycleOutput",
        DisplayName = "Audio: Cycle Output Device",
        Group = "Audio",
        Icon = AudioButtonLayouts.Cycle,
        ButtonLayout = AudioButtonLayouts.IconWithCaption(AudioButtonLayouts.Cycle, "Output", tall: true),
        Description = "Switch to the next output device",
        HiddenFromMenu = true,
        ParameterTemplate = "({devices})",
        Parameters =
        [
            // Empty means every device the picker shows. Entries are device names, aliases or ids.
            new CommandParameter(DevicesName, typeof(string)) { DefaultValue = string.Empty }
        ]
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public string GetText(CommandContext ctx) => AudioCurrentOutputCommand.Label(ctx, audio, aliasStore);

    public Task Execute(CommandContext ctx) => AudioDeviceParameter.Guard(ctx, Descriptor.CommandName, () =>
    {
        IReadOnlyList<AudioEndpointInfo> endpoints = audio.GetEndpoints(AudioEndpointKind.Render);
        IReadOnlyList<AudioEndpointInfo> cycle = Candidates(ctx.Parameters, endpoints);
        if (cycle.Count == 0)
        {
            AudioDeviceParameter.ShowOverlay(ctx, ctx.Host.Tr("No devices"));
            return;
        }

        // From the current default to the one after it. A default outside the list starts the list over.
        int current = -1;
        for (int i = 0; i < cycle.Count; i++)
        {
            if (cycle[i].IsDefault)
            {
                current = i;
                break;
            }
        }

        AudioEndpointInfo next = cycle[(current + 1) % cycle.Count];
        if (!next.IsDefault && !audio.SetDefaultEndpoint(next.Id))
        {
            ctx.Host.Logger.Warn($"Audio: could not make '{next.Id}' the default device.");
            AudioDeviceParameter.ShowOverlay(ctx, ctx.Host.Tr("Failed"));
            return;
        }

        // Everything that shows or follows the default has to see the new one now, not after its cache expires.
        AudioDeviceParameter.InvalidateDefaultEndpoint();
        AudioCurrentOutputCommand.InvalidateLabel();
        ctx.Host.RequestButtonRefresh(Descriptor.CommandName);
        ctx.Host.RequestButtonRefresh("Audio.CurrentOutput");

        AudioDeviceParameter.ShowOverlay(ctx, aliasStore.Resolve(next));
    });

    /// <summary>
    /// The devices to cycle through: the configured list in its order, or every visible device. An
    /// entry matches a device by id, alias or system name, ignoring case, and failing that by a part
    /// of the alias or name. The part is what makes a Windows name usable at all: names such as
    /// "Speakers (Realtek(R) Audio)" carry a closing parenthesis, which ends the binding's parameter
    /// list, so "Realtek" is the way to name that device. An entry that matches no connected device
    /// is skipped, so an unplugged headset simply drops out of the cycle.
    /// </summary>
    private IReadOnlyList<AudioEndpointInfo> Candidates(string[]? parameters, IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        string? list = parameters is { Length: > 0 } ? parameters[0] : null;
        if (string.IsNullOrWhiteSpace(list)) return visibility.Visible(endpoints);

        List<AudioEndpointInfo> result = [];
        foreach (string entry in list.Split(DeviceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AudioEndpointInfo? match = endpoints.FirstOrDefault(ep => Matches(ep, entry))
                                       ?? endpoints.FirstOrDefault(ep => Contains(ep, entry));
            if (match != null && !result.Contains(match)) result.Add(match);
        }
        return result;
    }

    private bool Matches(AudioEndpointInfo ep, string entry) =>
        string.Equals(ep.Id, entry, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(aliasStore.Resolve(ep), entry, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ep.FriendlyName, entry, StringComparison.OrdinalIgnoreCase);

    private bool Contains(AudioEndpointInfo ep, string entry) =>
        aliasStore.Resolve(ep).Contains(entry, StringComparison.OrdinalIgnoreCase) ||
        ep.FriendlyName.Contains(entry, StringComparison.OrdinalIgnoreCase);
}
