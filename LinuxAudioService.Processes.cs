using System.Diagnostics;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Running the command line tools this backend shells out to.</summary>
public sealed partial class LinuxAudioService
{
    /// <summary>How long a one-shot tool call may take before it is killed.</summary>
    private const int ToolTimeoutMs = 2000;

    /// <summary>Runs a tool and returns its stdout, or an empty string when it cannot run.</summary>
    private static string RunTool(string fileName, params string[] args)
    {
        ProcessStartInfo psi = new(fileName);
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        TryRun(psi, out string stdout);
        return stdout;
    }

    /// <summary>
    /// Runs a process to completion and hands back its stdout. Returns false when it could not
    /// start, ran longer than <see cref="ToolTimeoutMs"/> (it is killed then) or exited non-zero.
    /// <para>
    /// stdout and stderr are drained on two dedicated threads while the timeout runs. Reading stdout
    /// to the end first would block until the process exits, so a stuck PulseAudio or PipeWire socket
    /// would hang the caller for good; and an unread stderr can fill its pipe and stall the process
    /// itself. Dedicated threads rather than async reads: the callers are thread-pool threads, and
    /// under a burst a starved pool would let a successful call time out and read as failed.
    /// </para>
    /// </summary>
    private static bool TryRun(ProcessStartInfo psi, out string stdout)
    {
        stdout = string.Empty;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        try
        {
            using Process proc = Process.Start(psi)!;
            string output = string.Empty;
            Thread outputReader = new(() => output = proc.StandardOutput.ReadToEnd()) { IsBackground = true };
            Thread errorReader = new(() => proc.StandardError.ReadToEnd()) { IsBackground = true };
            outputReader.Start();
            errorReader.Start();

            if (!proc.WaitForExit(ToolTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch { /* exited in the meantime */ }
                return false;
            }

            // Both pipes close with the process, so the reads finish right after it.
            if (!outputReader.Join(ToolTimeoutMs)) return false;
            errorReader.Join(ToolTimeoutMs);

            stdout = output;
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A pactl invocation pinned to the C locale. pactl translates its output through gettext,
    /// and every parser here matches the English text ("Mute: yes", "Name:", "Event 'change'
    /// on sink"): on a German system mute reads back as "Stumm: ja", so it always parsed as
    /// unmuted and a toggle could never unmute (LoupixDeck#297).
    /// </summary>
    private static ProcessStartInfo PactlStartInfo(params string[] args)
    {
        // ArgumentList passes every value as one argument as-is, so a device name with spaces or
        // quotes cannot break the command line the way an interpolated string could.
        ProcessStartInfo psi = new("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["LC_ALL"] = "C";
        return psi;
    }

    /// <summary>Runs pactl and returns its stdout; empty when it failed or timed out.</summary>
    private static string RunPactl(params string[] args)
    {
        TryRun(PactlStartInfo(args), out string stdout);
        return stdout;
    }

    /// <summary>Runs pactl and reports whether it succeeded, for callers that must tell a failed
    /// call from an empty answer.</summary>
    private static bool TryRunPactl(out string stdout, params string[] args) =>
        TryRun(PactlStartInfo(args), out stdout);

    /// <summary>Probes whether a CLI tool is installed and runnable.</summary>
    private static bool DetectTool(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            // Nobody reads the redirected pipes, but a --version answer fits easily into them.
            if (!p.WaitForExit(1000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { /* exited in the meantime */ }
                return false;
            }

            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A CLI tool that is probed on first use and can be probed again on demand.</summary>
    private sealed class ToolProbe(string fileName, string arguments)
    {
        private volatile int _state; // 0 = not probed yet, 1 = present, 2 = absent

        public bool Value
        {
            get
            {
                int state = _state;
                if (state == 0)
                {
                    state = DetectTool(fileName, arguments) ? 1 : 2;
                    _state = state;
                }

                return state == 1;
            }
        }

        /// <summary>Forgets the earlier answer and probes the tool again.</summary>
        public bool Refresh()
        {
            _state = 0;
            return Value;
        }
    }
}
