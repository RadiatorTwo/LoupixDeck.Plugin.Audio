using System.Collections.Concurrent;
using System.Diagnostics;

namespace LoupixDeck.Plugin.Audio;

/// <summary>What the mixer shows for an application beyond its process name.</summary>
/// <param name="FriendlyName">The executable's file description ("Google Chrome"), or null when it has none.</param>
/// <param name="Icon">Square 0xAARRGGBB pixels of the shell icon, or null when there is none to show.</param>
internal sealed record AppIdentity(string? FriendlyName, uint[]? Icon, int IconSize)
{
    public static AppIdentity None { get; } = new(null, null, 0);
}

/// <summary>
/// Looks up the friendly name and the icon of an executable once and keeps them, because the mixer
/// asks for every tile several times a second and reading a file's version block or its icon is far
/// too slow for that. Windows only; elsewhere every lookup answers <see cref="AppIdentity.None"/>.
/// </summary>
internal sealed class AppIdentityCache
{
    private readonly ConcurrentDictionary<string, AppIdentity> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public AppIdentity Resolve(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath) || !OperatingSystem.IsWindows())
            return AppIdentity.None;

        return _byPath.GetOrAdd(executablePath, Load);
    }

    private static AppIdentity Load(string path)
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
