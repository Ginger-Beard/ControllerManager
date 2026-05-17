using System.Runtime.InteropServices;

namespace HIDReorder.Native;

internal static class SetupApi
{
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private const uint DIGCF_PRESENT    = 0x02;
    private const uint DIGCF_ALLCLASSES = 0x04;
    private const uint DIF_PROPERTYCHANGE = 0x12;
    private const uint DICS_ENABLE      = 0x01;
    private const uint DICS_DISABLE     = 0x02;
    private const uint DICS_FLAG_GLOBAL = 0x01;

    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private static readonly Guid   HidGuid        = new("{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint  cbSize;
        public Guid  ClassGuid;
        public uint  DevInst;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    /// <summary>
    /// Enables or disables a device by its Instance ID.
    /// </summary>
    public static bool SetDeviceEnabled(string instanceId, bool enable)
    {
        var guid   = HidGuid;
        var devSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (devSet == INVALID_HANDLE) return false;

        try
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf     = new char[256];

            for (uint i = 0; SetupDiEnumDeviceInfo(devSet, i, ref devInfo); i++)
            {
                if (!SetupDiGetDeviceInstanceId(devSet, ref devInfo, buf, (uint)buf.Length, out _))
                    continue;

                var id = new string(buf).TrimEnd('\0');
                if (!id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) continue;

                var pcp = new SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize          = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                        InstallFunction = DIF_PROPERTYCHANGE,
                    },
                    StateChange = enable ? DICS_ENABLE : DICS_DISABLE,
                    Scope       = DICS_FLAG_GLOBAL,
                    HwProfile   = 0,
                };

                SetupDiSetClassInstallParams(devSet, ref devInfo,
                    ref pcp, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>());
                return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devSet, ref devInfo);
            }

            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devSet);
        }
    }
}
