using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// UNSUPPORTED API. Windows exposes no documented way to change the default audio
/// endpoint; every tool that does it (including Microsoft's own) uses the undocumented
/// IPolicyConfig interface of the PolicyConfigClient coclass. The GUIDs below are stable
/// from Windows 7 through Windows 11, but nothing guarantees the next release keeps them.
/// Every failure here must stay non-fatal — the caller falls back to doing nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PolicyConfig
{
    private static readonly Guid PolicyConfigClientClsid =
        new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    /// <summary>Returns true when Windows accepted the switch.</summary>
    public static bool SetDefaultEndpoint(string endpointId)
    {
        Type? type = Type.GetTypeFromCLSID(PolicyConfigClientClsid);
        if (type == null) return false;

        object? instance = Activator.CreateInstance(type);
        if (instance is not IPolicyConfig config)
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
            return false;
        }

        try
        {
            // Set all three roles, otherwise Windows keeps routing communication audio
            // (and some apps' media audio) to the previous endpoint.
            config.SetDefaultEndpoint(endpointId, ERole.Console);
            config.SetDefaultEndpoint(endpointId, ERole.Multimedia);
            config.SetDefaultEndpoint(endpointId, ERole.Communications);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Only the last vtable entry is used, but every preceding slot must be declared
        // so the vtable offsets line up. Their signatures are deliberately opaque.
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }
}
