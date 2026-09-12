namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// One entry of the per-application mixer. <paramref name="AppId"/> is the stable
/// identity used in saved button assignments: the process executable name, lower-cased
/// and without extension. Several live sessions can share one AppId (a browser runs one
/// per tab), so every operation on an AppId applies to all of them.
/// </summary>
public sealed record AudioSessionInfo(
    string AppId,
    string DisplayName,
    float Volume,
    bool Muted);
