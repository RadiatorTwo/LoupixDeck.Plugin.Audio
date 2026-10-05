using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LoupixDeck.Plugin.Audio;

/// <summary>Win32 imports used by the session and foreground lookups.</summary>
public sealed partial class WindowsAudioService
{
    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle process, int flags, StringBuilder buffer, ref int size);

        /// <summary>Enough to read the image name, and granted for processes we cannot open fully.</summary>
        private const int ProcessQueryLimitedInformation = 0x1000;

        /// <summary>Full image path of a process, or null when it cannot be opened.</summary>
        public static string? QueryProcessImagePath(uint processId)
        {
            using SafeProcessHandle handle =
                OpenProcess(ProcessQueryLimitedInformation, false, (int)processId);
            if (handle.IsInvalid) return null;

            StringBuilder buffer = new(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
    }
}
