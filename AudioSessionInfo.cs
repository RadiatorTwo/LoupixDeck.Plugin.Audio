using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// One entry of the per-application mixer. <paramref name="AppId"/> is the stable
/// identity used in saved button assignments: the process executable name, lower-cased
/// and without extension. Several live sessions can share one AppId (a browser runs one
/// per tab), so every operation on an AppId applies to all of them.
/// <paramref name="ExecutablePath"/> is where the application's icon and friendly name come from;
/// it is null wherever the platform cannot tell (Linux, the system-sounds session).
/// </summary>
public sealed record AudioSessionInfo(
    string AppId,
    string DisplayName,
    float Volume,
    bool Muted,
    string? ExecutablePath = null);

/// <summary>The name a session is shown under, wherever the plugin draws one.</summary>
internal static class AudioSessionNames
{
    /// <summary>AppId of the Windows system-sounds session, which has no process of its own.</summary>
    public const string SystemSoundsAppId = "system";

    /// <summary>English name of the system-sounds session; translated where it is drawn, since the
    /// audio service that names it has no host.</summary>
    public const string SystemSounds = "System Sounds";

    /// <summary>The executable's file description ("Google Chrome") when it has one, otherwise what
    /// the session calls itself; the system-sounds session in the user's language.</summary>
    public static string Display(AudioSessionInfo session, AppIdentity identity, IPluginHost host) =>
        session.AppId == SystemSoundsAppId
            ? host.Tr(SystemSounds)
            : identity.FriendlyName ?? session.DisplayName;
}
