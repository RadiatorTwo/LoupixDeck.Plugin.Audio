using System.Collections.Concurrent;
using System.Diagnostics;

namespace LoupixDeck.Plugin.Audio;

/// <summary>What the mixer shows for an application beyond its process name.</summary>
/// <param name="FriendlyName">The executable's file description ("Google Chrome") on Windows, the
/// desktop file's name on Linux, or null when there is none.</param>
/// <param name="Icon">Square 0xAARRGGBB pixels of the application's icon, or null when there is none to show.</param>
internal sealed record AppIdentity(string? FriendlyName, uint[]? Icon, int IconSize)
{
    public static AppIdentity None { get; } = new(null, null, 0);
}

/// <summary>
/// Looks up the friendly name and the icon of an application once and keeps them, because the mixer
/// asks for every tile several times a second and reading a file's version block, a desktop file or
/// an icon is far too slow for that. A lookup that found nothing is kept too.
/// <para>
/// Windows reads both from the executable, so it is keyed by the session's executable path. Linux
/// has no path; <see cref="LinuxAppIdentityResolver"/> works from what pactl reports, keyed by the
/// AppId. Elsewhere every lookup answers <see cref="AppIdentity.None"/>.
/// </para>
/// </summary>
internal sealed class AppIdentityCache
{
    private readonly ConcurrentDictionary<string, AppIdentity> _byPath = new(StringComparer.OrdinalIgnoreCase);

    // Lazy, so the first lookup of an app reads the disk once even when several threads ask at once.
    private readonly ConcurrentDictionary<string, Lazy<AppIdentity>> _byAppId = new(StringComparer.Ordinal);
    private readonly LinuxAppIdentityResolver? _linux = OperatingSystem.IsLinux() ? new() : null;

    public AppIdentity Resolve(AudioSessionInfo? session)
    {
        if (session == null) return AppIdentity.None;

        if (OperatingSystem.IsWindows())
        {
            return string.IsNullOrEmpty(session.ExecutablePath)
                ? AppIdentity.None
                : _byPath.GetOrAdd(session.ExecutablePath, LoadWindows);
        }

        if (_linux == null) return AppIdentity.None;

        return _byAppId.GetOrAdd(session.AppId,
            _ => new Lazy<AppIdentity>(() => LoadLinux(_linux, session))).Value;
    }

    private static AppIdentity LoadLinux(LinuxAppIdentityResolver linux, AudioSessionInfo session)
    {
        try
        {
            return linux.Resolve(session);
        }
        catch (Exception)
        {
            // An unreadable desktop file or icon must not take the mixer down; the tile keeps its
            // speaker glyph and the stream's own name.
            return AppIdentity.None;
        }
    }

    private static AppIdentity LoadWindows(string path)
    {
        string? name = null;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) name = description;
        }
        catch (Exception)
        {
            // No version block, or the file is not readable: the session's own name stays in use.
        }

        uint[]? pixels = null;
        int size = 0;
        try
        {
            if (OperatingSystem.IsWindows() && WindowsIconExtractor.TryExtract(path) is { } icon)
            {
                pixels = icon.Pixels;
                size = icon.Size;
            }
        }
        catch (Exception)
        {
            // The tile draws its speaker glyph instead.
        }

        return new AppIdentity(name, pixels, size);
    }
}
