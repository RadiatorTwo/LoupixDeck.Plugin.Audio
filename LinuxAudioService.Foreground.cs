using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Audio;

/// <summary>The application owning the focused window, resolved through xprop.</summary>
public sealed partial class LinuxAudioService
{
    private static readonly Lazy<bool> HasXprop = new(() => DetectTool("xprop", "-version"));

    /// <summary>How long a resolved foreground app stays valid. Long enough to keep the dial's
    /// render path off xprop on every frame, short enough that an alt-tab shows up at once.</summary>
    private const long ForegroundCacheMs = 400;

    private readonly Lock _foregroundLock = new();
    private string? _foregroundAppId;
    private long _foregroundStamp = long.MinValue;

    /// <summary>
    /// Resolves the application owning the focused window through X11 (<c>xprop</c>), the same
    /// mechanism the host uses for app-focus page switching, so it covers X11 and XWayland
    /// sessions. Returns null on a pure Wayland session (no <c>DISPLAY</c>), without xprop, or
    /// when the focused window exposes no PID.
    /// <para>
    /// The result is cached for <see cref="ForegroundCacheMs"/> because the dial reads it on the
    /// render path: without the cache every frame would spawn two xprop processes.
    /// </para>
    /// </summary>
    public string? GetForegroundAppId()
    {
        lock (_foregroundLock)
        {
            long now = Environment.TickCount64;
            if (now - _foregroundStamp < ForegroundCacheMs) return _foregroundAppId;

            _foregroundStamp = now;
            _foregroundAppId = ResolveForegroundAppId();
            return _foregroundAppId;
        }
    }

    private string? ResolveForegroundAppId()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        if (!HasXprop.Value) return null;

        Match window = Regex.Match(RunTool("xprop", "-root", "_NET_ACTIVE_WINDOW"), @"window id # (0x[0-9a-fA-F]+)");
        if (!window.Success) return null;

        string windowId = window.Groups[1].Value;
        // 0x0 is the "no active window" answer, not a window to query.
        if (string.Equals(windowId, "0x0", StringComparison.OrdinalIgnoreCase)) return null;

        Match pidMatch = Regex.Match(RunTool("xprop", "-id", windowId, "_NET_WM_PID"), @"_NET_WM_PID\(CARDINAL\) = (\d+)");
        if (!pidMatch.Success) return null;
        if (!int.TryParse(pidMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            return null;

        IReadOnlyList<SinkInput> inputs = SinkInputs();

        // The stream's own PID is the exact answer whenever the window's process (or one of its
        // ancestors, which is how a browser tab's window maps onto the process that plays the
        // sound) owns a stream. This also survives a mismatch between the window's executable
        // name and the one PulseAudio reports for the stream.
        for (int walk = pid, depth = 0; walk > 1 && depth < 8; walk = ParentPid(walk), depth++)
        {
            foreach (SinkInput input in inputs)
                if (input.Pid == walk && input.AppId.Length > 0) return input.AppId;
        }

        // No stream yet: the executable name still identifies the app, so a dial bound to the
        // foreground sentinel names the right target once that app starts playing.
        string? binary = ProcessBinaryName(pid);
        return string.IsNullOrEmpty(binary) ? null : binary;
    }

    /// <summary>Executable name of a process, lowercased and without extension, matching the
    /// AppId <see cref="ParseSinkInputs"/> derives from <c>application.process.binary</c>.</summary>
    private static string? ProcessBinaryName(int pid)
    {
        string dir = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            // The exe symlink carries the full name; comm is truncated at 15 characters, so it
            // is only the fallback for a process whose exe link cannot be read.
            string? target = File.ResolveLinkTarget($"{dir}/exe", returnFinalTarget: false)?.Name;
            if (!string.IsNullOrEmpty(target))
                return Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        }
        catch { /* permission denied or process gone */ }

        try
        {
            string comm = File.ReadAllText($"{dir}/comm").Trim();
            return comm.Length == 0 ? null : Path.GetFileNameWithoutExtension(comm).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>PPid from <c>/proc/&lt;pid&gt;/stat</c>, or 0 when it cannot be read.</summary>
    private static int ParentPid(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            // Field 4 is the PPid, but field 2 (comm) may contain spaces and parentheses, so
            // parsing starts after its closing parenthesis.
            int close = stat.LastIndexOf(')');
            if (close < 0) return 0;

            string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int ppid) ? ppid : 0;
        }
        catch { return 0; }
    }
}
