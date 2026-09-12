using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>One playable file below the configured sound folder.</summary>
/// <param name="RelativePath">Path relative to the sound folder, using '/' as separator.</param>
/// <param name="DisplayName">File name without extension — the menu label.</param>
/// <param name="FullPath">Absolute path on disk.</param>
public sealed record SoundFile(string RelativePath, string DisplayName, string FullPath);

/// <summary>
/// Resolves the user-configured sound folder and turns the files below it into
/// round-trip-safe command parameter tokens.
/// </summary>
/// <remarks>
/// A raw file path must never be stored as a command parameter: the host splits a
/// parameter list on ',' and ends it at the first ')', without any quoting or escaping
/// (see <c>CommandStringParser.GetParameters</c>). A file called <c>airhorn (1).wav</c>
/// would be truncated, and a comma in the name would split into two parameters.
/// The token therefore is the folder-relative path, percent-encoded — .NET escapes
/// everything outside <c>A-Za-z0-9-._~</c>, which covers '(', ')', ',', '&amp;' and spaces.
/// Storing it relative also keeps assignments working when the folder is moved.
/// </remarks>
public sealed class SoundLibrary(IPluginSettings settings)
{
    internal const string FolderKey = "soundFolder";

    /// <summary>Settings key of the "stop on second press" toggle.</summary>
    internal const string StopOnSecondPressKey = "soundStopOnSecondPress";

    /// <summary>
    /// Extensions offered in the menu. Platform-dependent on purpose: Windows decodes via
    /// Media Foundation, which has no Vorbis support, so <c>.ogg</c> would only fail at
    /// press time. Linux plays it natively through paplay.
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions =
        OperatingSystem.IsWindows()
            ? new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".flac", ".m4a", ".aiff", ".aif" }
            : new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aiff", ".aif" };

    /// <summary>Path comparison follows the platform: Windows is case-insensitive, Linux is not.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Whether pressing a sound button again stops that sound instead of starting it a
    /// second time. Absent from an older settings file, which reads as false — the
    /// overlapping behaviour the plugin has always had.
    /// </summary>
    public bool StopOnSecondPress => settings.Get<bool>(StopOnSecondPressKey);

    /// <summary>The raw folder setting as entered by the user, or null when unset.</summary>
    public string? ConfiguredFolder
    {
        get
        {
            string? configured = settings.Get<string>(FolderKey);
            return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        }
    }

    /// <summary>The configured folder, or null when unset or missing on disk.</summary>
    public string? FolderPath
    {
        get
        {
            string? trimmed = ConfiguredFolder;
            if (trimmed == null) return null;

            try
            {
                return Directory.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
            }
            catch
            {
                // Malformed path (invalid characters, too long) — treat as unconfigured.
                return null;
            }
        }
    }

    /// <summary>
    /// All supported audio files below the configured folder, recursively, ordered by
    /// their relative path. Empty when no folder is configured or it cannot be read.
    /// </summary>
    public IReadOnlyList<SoundFile> Enumerate()
    {
        string? root = FolderPath;
        if (root == null) return [];

        try
        {
            List<SoundFile> files = [];
            foreach (string full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(full))) continue;

                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                files.Add(new SoundFile(
                    relative,
                    Path.GetFileNameWithoutExtension(full),
                    full));
            }

            files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, PathComparison));
            return files;
        }
        catch
        {
            // Permission denied, folder vanished mid-scan — the menu simply stays empty.
            return [];
        }
    }

    /// <summary>Encodes a folder-relative path into a parameter-safe token.</summary>
    public static string Encode(string relativePath) =>
        Uri.EscapeDataString(relativePath.Replace('\\', '/'));

    /// <summary>
    /// Turns a stored token back into an absolute file path, or null when it cannot be
    /// resolved. An absolute path is accepted as-is so a hand-edited assignment keeps
    /// working; a relative token must stay below the configured folder.
    /// </summary>
    public string? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(token.Trim());
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(decoded)) return null;

        try
        {
            if (Path.IsPathRooted(decoded))
                return File.Exists(decoded) ? Path.GetFullPath(decoded) : null;

            string? root = FolderPath;
            if (root == null) return null;

            string full = Path.GetFullPath(
                Path.Combine(root, decoded.Replace('/', Path.DirectorySeparatorChar)));

            // Keep the token from escaping the folder via "..".
            string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootPrefix, PathComparison)) return null;

            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
