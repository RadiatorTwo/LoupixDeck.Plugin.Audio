using LoupixDeck.Plugin.Audio.Rendering;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// The Linux side of <see cref="AppIdentityCache"/>: an application's name from its <c>.desktop</c>
/// file and its icon from the hicolor theme or the pixmaps folder.
/// <para>
/// A stream says little about its application. pactl reports the executable name (the AppId), the
/// name the application gave the stream, and often <c>application.icon_name</c> and, for Flatpak
/// and portal clients, the desktop-file id. The desktop file is found from those in that order of
/// trust: by id, by <c>StartupWMClass</c>, by the binary in <c>Exec</c>, by <c>Icon</c>.
/// </para>
/// <para>
/// Only PNG icons are read; the plugin has no SVG rasteriser. An application that ships only a
/// scalable icon keeps the speaker glyph, but still gets its name.
/// </para>
/// </summary>
internal sealed class LinuxAppIdentityResolver
{
    /// <summary>Icons larger than this are shrunk on load; the tile draws them at about 32-64 px.</summary>
    private const int MaxIconEdge = 128;

    /// <summary>hicolor sizes in the order they are tried: the tile's size first, then larger ones,
    /// which shrink cleanly, then smaller ones as a last resort.</summary>
    private static readonly int[] IconSizes = [64, 96, 128, 48, 256, 512, 32, 24, 22, 16];

    private readonly Lazy<IReadOnlyList<string>> _dataDirs = new(DataDirs);
    private readonly Lazy<DesktopIndex> _index;

    public LinuxAppIdentityResolver()
    {
        _index = new Lazy<DesktopIndex>(() => DesktopIndex.Build(_dataDirs.Value));
    }

    public AppIdentity Resolve(AudioSessionInfo session)
    {
        DesktopEntry? entry = _index.Value.Find(session.DesktopId, session.AppId, session.IconName);

        (uint[] Pixels, int Size)? icon = null;
        foreach (string? name in new[] { session.IconName, entry?.Icon, session.DesktopId, session.AppId })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            icon = LoadIcon(name.Trim());
            if (icon != null) break;
        }

        return new AppIdentity(entry?.Name, icon?.Pixels, icon?.Size ?? 0);
    }

    // --- icons -------------------------------------------------------------

    private (uint[] Pixels, int Size)? LoadIcon(string name)
    {
        // Icon= may name a file directly.
        if (Path.IsPathRooted(name)) return DecodeFile(name);

        // A bare name with an extension is a pixmaps file name rather than a theme icon name.
        string baseName = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        foreach (string dir in IconThemeDirs())
        {
            foreach (int size in IconSizes)
            {
                string path = Path.Combine(dir, "hicolor", $"{size}x{size}", "apps", baseName + ".png");
                if (File.Exists(path) && DecodeFile(path) is { } icon) return icon;
            }
        }

        foreach (string dir in _dataDirs.Value)
        {
            string path = Path.Combine(dir, "pixmaps", baseName + ".png");
            if (File.Exists(path) && DecodeFile(path) is { } icon) return icon;
        }

        return null;
    }

    private IEnumerable<string> IconThemeDirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return Path.Combine(home, ".icons");
        foreach (string dir in _dataDirs.Value) yield return Path.Combine(dir, "icons");
    }

    private static (uint[] Pixels, int Size)? DecodeFile(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (PngDecoder.TryDecode(File.ReadAllBytes(path)) is not { } image) return null;
            return PngDecoder.ToSquareIcon(image.Pixels, image.Width, image.Height, MaxIconEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // --- data directories ----------------------------------------------------

    /// <summary>
    /// The XDG data directories, most specific first: the user's, the system's, then the Flatpak and
    /// Snap exports, which are on <c>XDG_DATA_DIRS</c> in a desktop session but not necessarily in the
    /// environment LoupixDeck was started from.
    /// </summary>
    private static IReadOnlyList<string> DataDirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> dirs = [];

        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome)) dirs.Add(dataHome);
        else if (home.Length > 0) dirs.Add(Path.Combine(home, ".local", "share"));

        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        dirs.AddRange(string.IsNullOrWhiteSpace(dataDirs)
            ? ["/usr/local/share", "/usr/share"]
            : dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (home.Length > 0) dirs.Add(Path.Combine(home, ".local", "share", "flatpak", "exports", "share"));
        dirs.Add("/var/lib/flatpak/exports/share");
        dirs.Add("/var/lib/snapd/desktop");
        dirs.Add("/usr/share");

        return [.. dirs.Select(d => d.TrimEnd('/')).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    // --- desktop files -------------------------------------------------------

    private sealed record DesktopEntry(string? Name, string? Icon);

    /// <summary>Every application's desktop file, indexed by the keys a stream can be matched on.</summary>
    private sealed class DesktopIndex
    {
        private readonly Dictionary<string, DesktopEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DesktopEntry> _byIdTail = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DesktopEntry> _byWmClass = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DesktopEntry> _byExec = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DesktopEntry> _byIcon = new(StringComparer.OrdinalIgnoreCase);

        public DesktopEntry? Find(string? desktopId, string appId, string? iconName)
        {
            return Lookup(_byId, desktopId)
                   ?? Lookup(_byId, appId)
                   ?? Lookup(_byWmClass, appId)
                   ?? Lookup(_byExec, appId)
                   ?? Lookup(_byIdTail, appId)
                   ?? Lookup(_byIcon, iconName)
                   ?? Lookup(_byIcon, appId);

            static DesktopEntry? Lookup(Dictionary<string, DesktopEntry> map, string? key) =>
                string.IsNullOrWhiteSpace(key) ? null : map.GetValueOrDefault(StripDesktop(key.Trim()));
        }

        public static DesktopIndex Build(IReadOnlyList<string> dataDirs)
        {
            DesktopIndex index = new();
            string[] languages = LocaleNames();

            foreach (string dataDir in dataDirs)
            {
                string root = Path.Combine(dataDir, "applications");
                IEnumerable<string> files;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    files = Directory.EnumerateFiles(root, "*.desktop", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    // The desktop-file id is the path below applications/ with '/' turned into '-'.
                    string id = StripDesktop(Path.GetRelativePath(root, file).Replace('/', '-'));
                    // Earlier directories override later ones, so the first file with an id wins.
                    if (index._byId.ContainsKey(id)) continue;

                    if (Parse(file, languages) is not { } parsed) continue;
                    index.Add(id, parsed);
                }
            }

            return index;
        }

        private void Add(string id, ParsedDesktopFile file)
        {
            DesktopEntry entry = new(file.Name, file.Icon);
            _byId[id] = entry;
            _byIdTail.TryAdd(id[(id.LastIndexOf('.') + 1)..], entry);
            if (!string.IsNullOrWhiteSpace(file.WmClass)) _byWmClass.TryAdd(file.WmClass, entry);
            if (ExecBinary(file.Exec) is { } binary) _byExec.TryAdd(binary, entry);
            if (!string.IsNullOrWhiteSpace(file.Icon) && !Path.IsPathRooted(file.Icon)) _byIcon.TryAdd(file.Icon, entry);
        }

        private static string StripDesktop(string id) =>
            id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? id[..^8] : id;

        /// <summary>
        /// The executable an Exec line starts, as the AppId names it: the file name, lower-cased,
        /// without extension. <c>env VAR=value</c> prefixes are skipped.
        /// </summary>
        private static string? ExecBinary(string? exec)
        {
            if (string.IsNullOrWhiteSpace(exec)) return null;

            foreach (string token in exec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string word = token.Trim('"', '\'');
                if (word == "env" || word.Contains('=')) continue;
                string name = Path.GetFileNameWithoutExtension(word).ToLowerInvariant();
                return name.Length > 0 ? name : null;
            }

            return null;
        }

        private sealed record ParsedDesktopFile(string? Name, string? Icon, string? Exec, string? WmClass);

        /// <summary>
        /// Reads the <c>[Desktop Entry]</c> group of an application's desktop file, or null for a file
        /// that is hidden or not an application. <c>NoDisplay</c> entries are kept: helpers such as a
        /// browser's audio process often hide themselves from menus but are still what plays.
        /// </summary>
        private static ParsedDesktopFile? Parse(string path, string[] languages)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

            bool inEntry = false;
            string? type = null, name = null, icon = null, exec = null, wmClass = null, hidden = null;
            string? localizedName = null;
            int localizedRank = int.MaxValue;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    inEntry = line == "[Desktop Entry]";
                    continue;
                }
                if (!inEntry) continue;

                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();

                switch (key)
                {
                    case "Type": type = value; break;
                    case "Name": name = value; break;
                    case "Icon": icon = value; break;
                    case "Exec": exec = value; break;
                    case "StartupWMClass": wmClass = value; break;
                    case "Hidden": hidden = value; break;
                    default:
                        if (key.StartsWith("Name[", StringComparison.Ordinal) && key.EndsWith(']'))
                        {
                            int rank = Array.IndexOf(languages, key[5..^1]);
                            if (rank >= 0 && rank < localizedRank)
                            {
                                localizedRank = rank;
                                localizedName = value;
                            }
                        }
                        break;
                }
            }

            if (type != "Application" || string.Equals(hidden, "true", StringComparison.OrdinalIgnoreCase))
                return null;

            string? shown = localizedName ?? name;
            return new ParsedDesktopFile(string.IsNullOrWhiteSpace(shown) ? null : shown, icon, exec, wmClass);
        }

        /// <summary>
        /// The <c>Name[…]</c> keys to prefer, best first, from the user's locale: "de_DE.UTF-8@euro"
        /// gives "de_DE@euro", "de_DE", "de@euro", "de".
        /// </summary>
        private static string[] LocaleNames()
        {
            string? locale = new[] { "LC_ALL", "LC_MESSAGES", "LANG" }
                .Select(Environment.GetEnvironmentVariable)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (locale == null || locale is "C" or "POSIX" || locale.StartsWith("C.", StringComparison.Ordinal))
                return [];

            string modifier = string.Empty;
            int at = locale.IndexOf('@');
            if (at >= 0)
            {
                modifier = locale[at..];
                locale = locale[..at];
            }

            int dot = locale.IndexOf('.');
            if (dot >= 0) locale = locale[..dot];

            string language = locale.Split('_')[0];
            List<string> names = [];
            if (modifier.Length > 0) names.Add(locale + modifier);
            names.Add(locale);
            if (modifier.Length > 0) names.Add(language + modifier);
            names.Add(language);
            return [.. names.Distinct(StringComparer.Ordinal)];
        }
    }
}
