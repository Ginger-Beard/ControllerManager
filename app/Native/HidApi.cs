using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HIDReorder.Native;

/// <summary>
/// P/Invoke surface for Windows HID API (hid.dll) and supporting kernel32 calls.
///
/// Struct layouts mirror the Windows SDK hidpi.h definitions exactly. The HID
/// parser will write garbage / corrupt memory if these don't match — every
/// field size and order matters. Notably HIDP_CAPS is 68 bytes, HIDP_VALUE_CAPS
/// and HIDP_BUTTON_CAPS are 72 bytes each.
/// </summary>
internal static class HidApi
{
    // ── Constants ───────────────────────────────────────────────────────────────

    public const uint GENERIC_READ      = 0x80000000;
    public const uint FILE_SHARE_READ   = 0x00000001;
    public const uint FILE_SHARE_WRITE  = 0x00000002;
    public const uint OPEN_EXISTING     = 3;
    public const int  HIDP_STATUS_SUCCESS = unchecked((int)0x00110000);

    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_PAGE_BUTTON  = 0x09;

    private const byte HidP_Input    = 0;
    // Output = 1, Feature = 2 — unused for now

    public static readonly Guid HidClassGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    // ── Path helper ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a PnP instance ID like "HID\VID_0FD9&amp;PID_0086&amp;MI_00\7&amp;abc&amp;0&amp;0000"
    /// into the corresponding device interface path:
    ///   \\?\HID#VID_0FD9&amp;PID_0086&amp;MI_00#7&amp;abc&amp;0&amp;0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
    /// suitable for CreateFile.
    /// </summary>
    public static string ToDevicePath(string instanceId)
    {
        var hashed = instanceId.Replace('\\', '#');
        return $@"\\?\{hashed}#{HidClassGuid:B}";
    }

    // ── Axis name helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a HID Generic Desktop usage code to a human-readable axis name.
    /// Falls back to "Axis N" for anything outside the recognised range.
    /// </summary>
    public static string GetAxisName(ushort usagePage, ushort usage, int fallbackIndex)
    {
        if (usagePage == HID_USAGE_PAGE_GENERIC)
        {
            return usage switch
            {
                0x30 => "X",
                0x31 => "Y",
                0x32 => "Z",
                0x33 => "Rx",
                0x34 => "Ry",
                0x35 => "Rz",
                0x36 => "Slider",
                0x37 => "Dial",
                0x38 => "Wheel",
                0x39 => "Hat",
                _    => $"Axis {fallbackIndex}",
            };
        }
        return $"Axis {fallbackIndex}";
    }

    // ── kernel32 ────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint   dwDesiredAccess,
        uint   dwShareMode,
        IntPtr lpSecurityAttributes,
        uint   dwCreationDisposition,
        uint   dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[]         lpBuffer,
        uint           nNumberOfBytesToRead,
        out uint       lpNumberOfBytesRead,
        IntPtr         lpOverlapped);

    // ── hid.dll ─────────────────────────────────────────────────────────────────

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetValueCaps(
        byte reportType, [Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetButtonCaps(
        byte reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps,
        ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetUsageValue(
        byte reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData,
        byte[] report, uint reportLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetUsages(
        byte reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength,
        IntPtr preparsedData, byte[] report, uint reportLength);

    public const byte HidP_Input_ReportType = HidP_Input;

    // ── Struct layouts ──────────────────────────────────────────────────────────

    /// <summary>
    /// HIDP_CAPS — exactly 68 bytes. All fields are USHORT (2 bytes) in the SDK
    /// definition. The Reserved[17] block is 17 ushorts = 34 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        // Reserved[17] — 17 USHORTs
        public ushort Reserved0;  public ushort Reserved1;  public ushort Reserved2;
        public ushort Reserved3;  public ushort Reserved4;  public ushort Reserved5;
        public ushort Reserved6;  public ushort Reserved7;  public ushort Reserved8;
        public ushort Reserved9;  public ushort Reserved10; public ushort Reserved11;
        public ushort Reserved12; public ushort Reserved13; public ushort Reserved14;
        public ushort Reserved15; public ushort Reserved16;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>
    /// HIDP_VALUE_CAPS — 72 bytes total. Layout mirrors Windows SDK exactly.
    /// The union at the end is treated as the "Range" variant (UsageMin/Max).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte   ReportID;
        public byte   IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte   IsRange;
        public byte   IsStringRange;
        public byte   IsDesignatorRange;
        public byte   IsAbsolute;
        public byte   HasNull;
        public byte   Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2_0;
        public ushort Reserved2_1;
        public ushort Reserved2_2;
        public ushort Reserved2_3;
        public ushort Reserved2_4;
        public uint   UnitsExp;
        public uint   Units;
        public int    LogicalMin;
        public int    LogicalMax;
        public int    PhysicalMin;
        public int    PhysicalMax;

        // Union — Range variant
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    /// <summary>
    /// HIDP_BUTTON_CAPS — 72 bytes total. The Reserved block here is 9 ULONGs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte   ReportID;
        public byte   IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte   IsRange;
        public byte   IsStringRange;
        public byte   IsDesignatorRange;
        public byte   IsAbsolute;
        public ushort ReportCount;
        public ushort Reserved2;

        // Reserved[9] — 9 ULONGs = 36 bytes
        public uint Reserved0;  public uint Reserved1;  public uint Reserved3;
        public uint Reserved4;  public uint Reserved5;  public uint Reserved6;
        public uint Reserved7;  public uint Reserved8;  public uint Reserved9;

        // Union — Range variant
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }
}
