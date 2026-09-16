using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

internal static class AudioAppParameter
{
    public const string AppIdName = "appId";
    public const string StepName = "step";
    public const string PercentName = "percent";

    /// <summary>Reserved AppId resolved to the foreground window's process at execution time.</summary>
    public const string ForegroundAppId = "@foreground";

    public const int DefaultStepPercent = 5;
    public const int DefaultPercent = 50;

    public static IReadOnlyList<CommandParameter> StepParameters { get; } =
    [
        new CommandParameter(AppIdName, typeof(string)),
        new CommandParameter(StepName, typeof(int))
        {
            DefaultValue = DefaultStepPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    public static IReadOnlyList<CommandParameter> PercentParameters { get; } =
    [
        new CommandParameter(AppIdName, typeof(string)),
        new CommandParameter(PercentName, typeof(int))
        {
            DefaultValue = DefaultPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    public static IReadOnlyList<CommandParameter> AppIdParameters { get; } =
        [new CommandParameter(AppIdName, typeof(string))];

    /// <summary>
    /// Resolves the bound AppId, expanding the foreground sentinel. Returns null when the
    /// binding carries no app or the foreground app cannot be determined.
    /// </summary>
    public static string? ResolveAppId(CommandContext ctx, IAudioService audio)
    {
        string[]? p = ctx.Parameters;
        if (p == null || p.Length == 0) return null;

        string id = p[0];
        if (string.IsNullOrWhiteSpace(id)) return null;

        return string.Equals(id, ForegroundAppId, StringComparison.Ordinal)
            ? audio.GetForegroundAppId()
            : id;
    }

    /// <summary>
    /// The bound app, reporting on the dial when there is none. Silence is the wrong answer here:
    /// the foreground sentinel resolves to nothing whenever the window in front belongs to no
    /// process we can read — including LoupixDeck's own window while the user is configuring — and
    /// a dial that does nothing without saying why is indistinguishable from a broken binding.
    /// </summary>
    public static string? ResolveAppIdOrReport(CommandContext ctx, IAudioService audio)
    {
        string? appId = ResolveAppId(ctx, audio);
        if (appId == null)
            AudioDeviceParameter.ShowOverlay(ctx, "No app in front");

        return appId;
    }

    /// <summary>
    /// Says that the app has no audio session to change, which is what a resolved app that is not
    /// playing anything looks like from here.
    /// </summary>
    public static void ReportNoSession(CommandContext ctx, string appId) =>
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId}: no audio");

    public static int ResolveInt(CommandContext ctx, int fallback)
    {
        string[]? p = ctx.Parameters;
        if (p != null && p.Length > 1 &&
            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }
        return fallback;
    }
}

internal sealed class AudioAppVolumeUpCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppVolumeUp",
        DisplayName = "Audio: App Volume Up",
        Group = "Audio",
        Icon = "\U000F057E",
        Description = "Raise the volume of one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{step})",
        Parameters = AudioAppParameter.StepParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return Task.CompletedTask;

        float? current = audio.GetSessionVolume(null, appId);
        if (current == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return Task.CompletedTask;
        }

        float step = Math.Abs(AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultStepPercent)) / 100f;
        float next = Math.Clamp(current.Value + step, 0f, 1f);
        audio.SetSessionVolume(null, appId, next);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(next)}");
        return Task.CompletedTask;
    }
}

internal sealed class AudioAppVolumeDownCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppVolumeDown",
        DisplayName = "Audio: App Volume Down",
        Group = "Audio",
        Icon = "\U000F057F",
        Description = "Lower the volume of one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{step})",
        Parameters = AudioAppParameter.StepParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return Task.CompletedTask;

        float? current = audio.GetSessionVolume(null, appId);
        if (current == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return Task.CompletedTask;
        }

        float step = Math.Abs(AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultStepPercent)) / 100f;
        float next = Math.Clamp(current.Value - step, 0f, 1f);
        audio.SetSessionVolume(null, appId, next);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(next)}");
        return Task.CompletedTask;
    }
}

internal sealed class AudioAppMuteToggleCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppMuteToggle",
        DisplayName = "Audio: App Mute Toggle",
        Group = "Audio",
        Icon = "\U000F075F",
        Description = "Toggle mute for one application",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId})",
        Parameters = AudioAppParameter.AppIdParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return Task.CompletedTask;

        bool? muted = audio.GetSessionMute(null, appId);
        if (muted == null)
        {
            AudioAppParameter.ReportNoSession(ctx, appId);
            return Task.CompletedTask;
        }

        audio.SetSessionMute(null, appId, !muted.Value);
        AudioDeviceParameter.ShowOverlay(ctx, !muted.Value ? "🔇" : $"🔊 {appId}");
        return Task.CompletedTask;
    }
}

internal sealed class AudioAppSetVolumeCommand(IAudioService audio) : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Audio.AppSetVolume",
        DisplayName = "Audio: Set App Volume",
        Group = "Audio",
        Icon = "\U000F057E",
        Description = "Set one application to a fixed volume",
        HiddenFromMenu = true,
        ParameterTemplate = "({appId},{percent})",
        Parameters = AudioAppParameter.PercentParameters
    };

    public ButtonTargets SupportedTargets =>
        ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        string? appId = AudioAppParameter.ResolveAppIdOrReport(ctx, audio);
        if (appId == null) return Task.CompletedTask;

        float target = Math.Clamp(
            AudioAppParameter.ResolveInt(ctx, AudioAppParameter.DefaultPercent), 0, 100) / 100f;
        audio.SetSessionVolume(null, appId, target);
        AudioDeviceParameter.ShowOverlay(ctx, $"{appId} {AudioDeviceParameter.FormatVolume(target)}");
        return Task.CompletedTask;
    }
}
