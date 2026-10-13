using System.Runtime.InteropServices;

namespace LoupixDeck.Plugin.Audio;

/// <summary>CoreAudio and CoreFoundation entry points, property selectors and helpers.</summary>
public sealed partial class MacAudioService
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObject = 1;
    private const uint ElementMain = 0;

    private static readonly uint ScopeGlobal = FourCc("glob");
    private static readonly uint ScopeInput = FourCc("inpt");
    private static readonly uint ScopeOutput = FourCc("outp");

    private static readonly uint PropDevices = FourCc("dev#");
    private static readonly uint PropDefaultOutput = FourCc("dOut");
    private static readonly uint PropDefaultInput = FourCc("dIn ");
    private static readonly uint PropName = FourCc("lnam");
    private static readonly uint PropUid = FourCc("uid ");
    private static readonly uint PropTranslateUid = FourCc("uidd");
    private static readonly uint PropStreams = FourCc("stm#");
    private static readonly uint PropCanBeDefault = FourCc("dflt");
    private static readonly uint PropHidden = FourCc("hidn");
    private static readonly uint PropVolumeScalar = FourCc("volm");
    private static readonly uint PropMute = FourCc("mute");

    /// <summary>
    /// kAudioHardwareServiceDeviceProperty_VirtualMainVolume: the one level the menu bar slider moves,
    /// served by AudioToolbox on top of whatever per-channel controls the device has.
    /// </summary>
    private static readonly uint PropVirtualMainVolume = FourCc("vmvc");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    /// <summary>AudioObjectPropertyListenerProc.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PropertyListener(uint objectId, uint addressCount, IntPtr addresses, IntPtr clientData);

    [DllImport(CoreAudio)]
    private static extern byte AudioObjectHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectIsPropertySettable(uint objectId, ref PropertyAddress address, out byte settable);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectSetPropertyData(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectAddPropertyListener(uint objectId, ref PropertyAddress address,
        PropertyListener listener, IntPtr clientData);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectRemovePropertyListener(uint objectId, ref PropertyAddress address,
        PropertyListener listener, IntPtr clientData);

    [DllImport(AudioToolbox)]
    private static extern byte AudioHardwareServiceHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceIsPropertySettable(uint objectId, ref PropertyAddress address, out byte settable);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceGetPropertyData(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, IntPtr data);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceSetPropertyData(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, uint dataSize, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern long CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
    private static extern void CFStringGetCharacters(IntPtr text, CFRange range, [Out] char[] buffer);

    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
    private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string chars, long length);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange(long location, long length)
    {
        public long Location = location;
        public long Length = length;
    }

    private static uint FourCc(string code) =>
        ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];

    private static uint Scope(AudioEndpointKind kind) => kind == AudioEndpointKind.Render ? ScopeOutput : ScopeInput;

    private static bool Has(uint objectId, uint selector, uint scope, uint element = ElementMain)
    {
        PropertyAddress address = new(selector, scope, element);
        return AudioObjectHasProperty(objectId, ref address) != 0;
    }

    private static bool IsSettable(uint objectId, uint selector, uint scope, uint element)
    {
        PropertyAddress address = new(selector, scope, element);
        return AudioObjectIsPropertySettable(objectId, ref address, out byte settable) == 0 && settable != 0;
    }

    private static bool TryGetUInt32(uint objectId, uint selector, uint scope, out uint value, uint element = ElementMain)
    {
        value = 0;
        PropertyAddress address = new(selector, scope, element);
        uint size = sizeof(uint);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return false;
            value = (uint)Marshal.ReadInt32(buffer);
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TrySetUInt32(uint objectId, uint selector, uint scope, uint value, uint element = ElementMain)
    {
        PropertyAddress address = new(selector, scope, element);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, (int)value);
            return AudioObjectSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(uint), buffer) == 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryGetFloat(uint objectId, uint selector, uint scope, uint element, out float value)
    {
        value = 0f;
        PropertyAddress address = new(selector, scope, element);
        uint size = sizeof(float);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return false;
            value = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer));
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TrySetFloat(uint objectId, uint selector, uint scope, uint element, float value)
    {
        PropertyAddress address = new(selector, scope, element);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            Marshal.WriteInt32(buffer, BitConverter.SingleToInt32Bits(value));
            return AudioObjectSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(float), buffer) == 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryGetVirtualMainVolume(uint deviceId, uint scope, out float value)
    {
        value = 0f;
        PropertyAddress address = new(PropVirtualMainVolume, scope, ElementMain);
        if (AudioHardwareServiceHasProperty(deviceId, ref address) == 0) return false;

        uint size = sizeof(float);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            if (AudioHardwareServiceGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return false;
            value = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer));
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TrySetVirtualMainVolume(uint deviceId, uint scope, float value)
    {
        PropertyAddress address = new(PropVirtualMainVolume, scope, ElementMain);
        if (AudioHardwareServiceHasProperty(deviceId, ref address) == 0) return false;
        if (AudioHardwareServiceIsPropertySettable(deviceId, ref address, out byte settable) != 0 || settable == 0) return false;

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            Marshal.WriteInt32(buffer, BitConverter.SingleToInt32Bits(value));
            return AudioHardwareServiceSetPropertyData(deviceId, ref address, 0, IntPtr.Zero, sizeof(float), buffer) == 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>A CFStringRef property, which the caller of AudioObjectGetPropertyData owns.</summary>
    private static string? TryGetString(uint objectId, uint selector)
    {
        PropertyAddress address = new(selector, ScopeGlobal, ElementMain);
        uint size = (uint)IntPtr.Size;
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return null;
            IntPtr text = Marshal.ReadIntPtr(buffer);
            if (text == IntPtr.Zero) return null;
            try { return FromCFString(text); }
            finally { CFRelease(text); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string FromCFString(IntPtr text)
    {
        long length = CFStringGetLength(text);
        char[] chars = new char[length];
        CFStringGetCharacters(text, new CFRange(0, length), chars);
        return new string(chars);
    }

    private static uint[] GetDeviceIds()
    {
        PropertyAddress address = new(PropDevices, ScopeGlobal, ElementMain);
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out uint size) != 0 || size == 0)
            return [];

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return [];
            int[] raw = new int[size / sizeof(uint)];
            Marshal.Copy(buffer, raw, 0, raw.Length);
            return Array.ConvertAll(raw, id => (uint)id);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The device the UID names right now, or 0 when it is not connected.</summary>
    private static uint TranslateUid(string uid)
    {
        IntPtr text = CFStringCreateWithCharacters(IntPtr.Zero, uid, uid.Length);
        if (text == IntPtr.Zero) return 0;

        PropertyAddress address = new(PropTranslateUid, ScopeGlobal, ElementMain);
        uint size = sizeof(uint);
        IntPtr qualifier = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteIntPtr(qualifier, text);
            if (AudioObjectGetPropertyData(SystemObject, ref address, (uint)IntPtr.Size, qualifier, ref size, buffer) != 0)
                return 0;
            return (uint)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(qualifier);
            CFRelease(text);
        }
    }

    /// <summary>Whether the device has streams in that direction.</summary>
    private static bool HasStreams(uint deviceId, uint scope)
    {
        PropertyAddress address = new(PropStreams, scope, ElementMain);
        return AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out uint size) == 0 && size > 0;
    }
}
