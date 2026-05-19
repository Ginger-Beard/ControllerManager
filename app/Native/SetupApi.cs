using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ControllerManager.Native;

/// <summary>P/Invoke surface for SetupAPI (setupapi.dll).</summary>
internal static class SetupApi
{
    // ── Constants ────────────────────────────────────────────────────────────────

    public const uint DIGCF_PRESENT        = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    private const int INVALID_HANDLE = -1;

    // ── SafeHandle wrapper ───────────────────────────────────────────────────────

    public sealed class DeviceInfoSetHandle : SafeHandleMinusOneIsInvalid
    {
        private DeviceInfoSetHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            SetupDiDestroyDeviceInfoList(handle);
            return true;
        }
    }

    // ── Structs ──────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int  cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────────

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern DeviceInfoSetHandle SetupDiGetClassDevsW(
        in Guid ClassGuid, string? Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        DeviceInfoSetHandle DeviceInfoSet,
        IntPtr DeviceInfoData,
        in Guid InterfaceClassGuid,
        uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        DeviceInfoSetHandle DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // ── Helper ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the symbolic link (device interface path) for the given device instance path
    /// and interface GUID, or null if the device doesn't expose that interface.
    /// </summary>
    public static string? GetSymbolicLink(Guid interfaceGuid, string deviceInstancePath)
    {
        // Ask for device interface info for this specific instance path.
        using var hDevInfo = SetupDiGetClassDevsW(
            in interfaceGuid, deviceInstancePath, IntPtr.Zero, DIGCF_DEVICEINTERFACE);

        if (hDevInfo.IsInvalid) return null;

        var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
        if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, in interfaceGuid, 0, ref ifData))
            return null; // device doesn't expose this interface

        // First call: get required buffer size.
        SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref ifData, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
        if (needed == 0) return null;

        // Second call: fill the buffer.
        // SP_DEVICE_INTERFACE_DETAIL_DATA_W layout: DWORD cbSize (4) then WCHAR[] DevicePath.
        // cbSize must be sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) = 6 on x86, 8 on x64.
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            Marshal.WriteInt32(buf, Environment.Is64BitProcess ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref ifData, buf, needed, out _, IntPtr.Zero))
                return null;

            // DevicePath starts at offset 4 (after DWORD cbSize).
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
