using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ControllerManager.Models;
using ControllerManager.Native;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ControllerManager.Services;

/// <summary>
/// Enumerates HID devices using the same approach as HidHide's own client:
///   - CM_Get_Device_ID_ListW (present HID class devices, no WMI)
///   - SetupDiGetDeviceInterfaceDetailW for the symbolic link / device path
///   - HidD_GetProductString + HidD_GetManufacturerString for device names
///   - Gaming device filter: UsagePage=0x05 or (UsagePage=0x01 and Usage=0x04/0x05),
///     matching HidHide's GamingDevice() function exactly
/// </summary>
public sealed class DeviceEnumerator
{
    // HID device class GUID — same as GUID_DEVCLASS_HIDCLASS
    private static readonly Guid HidClassGuid     = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");
    // HID device interface GUID — used by SetupDi to find symbolic links
    private static readonly Guid HidInterfaceGuid = HidApi.HidClassGuid; // {4d1e55b2-...}

    // Valve-specific gaming device overrides (from HidHide source: GamingDevice())
    private const ushort ValveVID       = 0x28DE;
    private const ushort SteamDeckPID   = 0x1205;
    private const ushort SteamCtrlPID   = 0x1142;

    // ── CfgMgr32 ─────────────────────────────────────────────────────────────────

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(
        out uint pulLen, string pszFilter, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(
        string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

    // Include only currently-present devices (phantom/absent devices are excluded).
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const uint CM_GETIDLIST_FILTER_CLASS   = 0x200;

    // ── CfgMgr32: property lookup (for description fallback + ConfigFlags) ────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static DEVPROPKEY DEVPKEY_Device_DeviceDesc => new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2
    };
    private static DEVPROPKEY DEVPKEY_Device_ContainerId => new()
    {
        fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2
    };

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Parent(
        out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(
        out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(
        uint dnDevInst, StringBuilder Buffer, uint BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        [Out] byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    private const int  CR_SUCCESS    = 0;
    private const int  CR_BUFFER_SMALL = 0x0000001A;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;

    // ── VID/PID regex (for AlternativeInstanceId fallback) ───────────────────────

    private static readonly Regex VidRx = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PidRx = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MiRx  = new(@"&MI_([0-9A-Fa-f]+)",   RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InterfaceTagRx = new(
        @"&(?:MI_|Col)[0-9A-Fa-f]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────────

    public List<HidDevice> GetAll(bool showAllHid = false)
    {
        var instanceIds  = GetHidClassDeviceIds();
        var seenKeys     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results      = new List<HidDevice>();
        var vidPidCount  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // USB function nodes: collected during enumeration for pnputil AlternativeInstanceId.
        var usbFunctions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // First pass: collect USB function nodes (same logic as before).
        foreach (var id in instanceIds)
        {
            if (!id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) continue;
            var vidM2 = VidRx.Match(id);
            var pidM2 = PidRx.Match(id);
            var miM2  = MiRx.Match(id);
            if (vidM2.Success && pidM2.Success && miM2.Success)
            {
                var key = $"{vidM2.Groups[1].Value.ToUpperInvariant()}" +
                          $"_{pidM2.Groups[1].Value.ToUpperInvariant()}" +
                          $"_{miM2.Groups[1].Value.ToUpperInvariant()}";
                usbFunctions.TryAdd(key, id);
            }
        }

        // Second pass: enumerate HID interfaces.
        foreach (var instanceId in instanceIds)
        {
            if (instanceId.StartsWith("USB\\",  StringComparison.OrdinalIgnoreCase)) continue;
            if (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) continue;

            // Get the HID symbolic link (device path) via SetupDi — same method HidHide uses.
            // When a device is PnP-disabled the interface is deregistered and SetupDi returns
            // null. Fall back to the derived path so pnputil-disabled devices stay visible.
            var symbolicLink = SetupApi.GetSymbolicLink(HidInterfaceGuid, instanceId)
                               ?? HidApi.ToDevicePath(instanceId);

            // Open the device to read HID string descriptors and capabilities.
            var info = QueryHidDeviceInfo(instanceId, symbolicLink);

            // Always keep devices we can't open (HidHide-blocked or pnputil-disabled) in the
            // default view — they were shown before the toggle, they should stay shown after.
            // Only apply the gaming filter to fully-accessible devices.
            if (!showAllHid && !info.IsAccessDenied
                && !IsGamingDevice(info.VendorId, info.ProductId, info.UsagePage, info.Usage))
                continue;

            // Dedup: collapse MI_00 / MI_01 of the same physical device into one row.
            var dedupeKey = GetDedupeKey(instanceId);
            if (!seenKeys.Add(dedupeKey)) continue;

            // Build friendly name: vendor + product string (HidHide's approach),
            // fallback to DEVPKEY_Device_DeviceDesc when the device is inaccessible.
            var friendlyName = BuildFriendlyName(
                info.Vendor, info.Product, GetDeviceDescription(instanceId));

            var vid    = info.VendorId.ToString("X4").ToUpperInvariant();
            var pid    = info.ProductId.ToString("X4").ToUpperInvariant();
            var vidPid = $"{vid}_{pid}";

            vidPidCount.TryGetValue(vidPid, out int dupIdx);
            vidPidCount[vidPid] = dupIdx + 1;

            // AlternativeInstanceId: the USB function node for pnputil path (MOZA workaround).
            var miMatch  = MiRx.Match(instanceId);
            var usbKey   = miMatch.Success
                ? $"{vid}_{pid}_{miMatch.Groups[1].Value.ToUpperInvariant()}"
                : null;
            usbFunctions.TryGetValue(usbKey ?? "", out var altId);

            // IsEnabled: reflects PnP state (ConfigManagerErrorCode=22 or ConfigFlags=1).
            bool disabledByFlags = ReadConfigFlags(altId) == 1 || ReadConfigFlags(instanceId) == 1;
            bool isEnabled       = !disabledByFlags && !IsPnpDisabled(instanceId);

            results.Add(new HidDevice
            {
                InstanceId            = instanceId,
                VendorId              = vid,
                ProductId             = pid,
                FriendlyName          = friendlyName,
                VendorLabel           = friendlyName,
                IsEnabled             = isEnabled,
                DuplicateIndex        = dupIdx,
                DeviceInterfacePath   = symbolicLink,
                AxisCount             = info.AxisCount,
                ButtonCount           = info.ButtonCount,
                AlternativeInstanceId = altId ?? "",
            });
        }

        // Append #1/#2 suffix only when multiple physical devices share a VID+PID.
        foreach (var d in results)
        {
            var key = $"{d.VendorId}_{d.ProductId}";
            if (vidPidCount.TryGetValue(key, out int total) && total > 1)
                d.FriendlyName += $"  #{d.DuplicateIndex + 1}";
        }

        return [.. results.OrderBy(d => d.FriendlyName)];
    }

    // ── HidHide-compatible gaming device filter ───────────────────────────────────

    // Mirrors HidHide's GamingDevice() function in HID.cpp exactly:
    //   UsagePage 0x05 (Game Controls), or
    //   UsagePage 0x01 (Generic Desktop) + Usage 0x04 (Joystick) or 0x05 (Gamepad), or
    //   Valve Steam Controller / Steam Deck (specific VID+PID pairs).
    private static bool IsGamingDevice(ushort vid, ushort pid, ushort usagePage, ushort usage)
    {
        if (vid == ValveVID && (pid == SteamDeckPID || pid == SteamCtrlPID)) return true;
        if (usagePage == 0x05) return true;
        if (usagePage == 0x01 && (usage == 0x04 || usage == 0x05)) return true;
        return false;
    }

    // ── HID device info query ─────────────────────────────────────────────────────

    private record HidDeviceInfo(
        ushort VendorId, ushort ProductId,
        ushort UsagePage, ushort Usage,
        string Vendor, string Product,
        int AxisCount, int ButtonCount,
        bool IsAccessDenied = false);   // true = HidHide blocked; always show regardless of filter

    private const int ERROR_ACCESS_DENIED = 5;

    private static HidDeviceInfo QueryHidDeviceInfo(string instanceId, string symbolicLink)
    {
        ushort vid = 0, pid = 0, usagePage = 0, usage = 0;
        string vendor = "", product = "";
        int axes = 0, buttons = 0;

        var handle = HidApi.CreateFileW(
            symbolicLink,
            HidApi.GENERIC_READ,
            HidApi.FILE_SHARE_READ | HidApi.FILE_SHARE_WRITE,
            IntPtr.Zero, HidApi.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            bool denied = Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            handle.Dispose();

            // Extract VID/PID from the instance ID string as fallback — available even when
            // the device is inaccessible (HidHide-blocked or pnputil-disabled).
            var vidM = VidRx.Match(instanceId);
            var pidM = PidRx.Match(instanceId);
            if (vidM.Success) vid = Convert.ToUInt16(vidM.Groups[1].Value, 16);
            if (pidM.Success) pid = Convert.ToUInt16(pidM.Groups[1].Value, 16);

            return new(vid, pid, usagePage, usage, vendor, product, axes, buttons,
                       IsAccessDenied: denied);
        }

        try
        {
            // VID / PID / version
            var attrs = new HidApi.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HidApi.HIDD_ATTRIBUTES>() };
            if (HidApi.HidD_GetAttributes(handle, ref attrs))
            {
                vid = attrs.VendorID;
                pid = attrs.ProductID;
            }

            // Manufacturer + product strings (same buffer size HidHide uses: 127 chars)
            var buf = new char[HidApi.HidStringMax];
            uint bufBytes = (uint)(buf.Length * sizeof(char));
            if (HidApi.HidD_GetManufacturerString(handle, buf, bufBytes))
                vendor = new string(buf).TrimEnd('\0');
            if (HidApi.HidD_GetProductString(handle, buf, bufBytes))
                product = new string(buf).TrimEnd('\0');

            // Capabilities: usage page + usage + axis/button counts
            IntPtr preparsed = IntPtr.Zero;
            if (HidApi.HidD_GetPreparsedData(handle, out preparsed) && preparsed != IntPtr.Zero)
            {
                try
                {
                    if (HidApi.HidP_GetCaps(preparsed, out var caps) == HidApi.HIDP_STATUS_SUCCESS)
                    {
                        usagePage = caps.UsagePage;
                        usage     = caps.Usage;
                        (axes, buttons) = CountInputCaps(caps, preparsed);
                    }
                }
                finally { HidApi.HidD_FreePreparsedData(preparsed); }
            }
        }
        finally { handle.Dispose(); }

        return new(vid, pid, usagePage, usage, vendor, product, axes, buttons);
    }

    // Counts Generic Desktop joystick axes and Button-page buttons — same filter as before.
    private static (int Axes, int Buttons) CountInputCaps(HidApi.HIDP_CAPS caps, IntPtr preparsed)
    {
        int axes = 0, buttons = 0;

        if (caps.NumberInputValueCaps > 0)
        {
            var vc = new HidApi.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
            ushort vl = caps.NumberInputValueCaps;
            if (HidApi.HidP_GetValueCaps(HidApi.HidP_Input_ReportType, vc, ref vl, preparsed)
                    == HidApi.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < vl; i++)
                {
                    if (vc[i].UsagePage != 0x01) continue;
                    ushort uMin = vc[i].UsageMin;
                    ushort uMax = vc[i].IsRange != 0 ? vc[i].UsageMax : uMin;
                    for (ushort u = uMin; u <= uMax; u++)
                        if (u >= 0x30 && u <= 0x3F) axes++;
                }
            }
        }

        if (caps.NumberInputButtonCaps > 0)
        {
            var bc = new HidApi.HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
            ushort bl = caps.NumberInputButtonCaps;
            if (HidApi.HidP_GetButtonCaps(HidApi.HidP_Input_ReportType, bc, ref bl, preparsed)
                    == HidApi.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < bl; i++)
                {
                    if (bc[i].UsagePage != 0x09) continue;
                    ushort uMin = bc[i].UsageMin;
                    ushort uMax = bc[i].IsRange != 0 ? bc[i].UsageMax : uMin;
                    buttons += uMax - uMin + 1;
                }
            }
        }

        return (axes, buttons);
    }

    // ── Name construction (mirrors HidHide's MergeModelInformation approach) ──────

    private static string BuildFriendlyName(string vendor, string product, string? description)
    {
        // Split both strings into words, add each unique word (case-insensitive) in order.
        var parts = new List<string>();
        foreach (var word in SplitWords(vendor).Concat(SplitWords(product)))
        {
            if (!parts.Any(p => p.Equals(word, StringComparison.OrdinalIgnoreCase)))
                parts.Add(word);
        }

        if (parts.Count > 0) return string.Join(" ", parts);

        // Fallback: device description property (same as HidHide when strings are empty)
        return !string.IsNullOrWhiteSpace(description) ? description : "(unknown device)";
    }

    private static IEnumerable<string> SplitWords(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // ── Enumeration: CM_Get_Device_ID_ListW ───────────────────────────────────────

    private static List<string> GetHidClassDeviceIds()
    {
        var classGuidStr = HidClassGuid.ToString("B").ToUpperInvariant();
        uint flags       = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;

        if (CM_Get_Device_ID_List_SizeW(out uint needed, classGuidStr, flags) != CR_SUCCESS)
            return [];

        var buf = new char[needed];
        if (CM_Get_Device_ID_ListW(classGuidStr, buf, needed, flags) != CR_SUCCESS)
            return [];

        // Multi-string: null-separated, double-null terminated.
        return new string(buf)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    // ── Device description fallback (DEVPKEY_Device_DeviceDesc) ──────────────────

    private static string? GetDeviceDescription(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                return null;

            var key = DEVPKEY_Device_DeviceDesc;
            uint type = 0, size = 0;
            // First call: get required buffer size.
            CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref size, 0);
            if (size == 0 || type != DEVPROP_TYPE_STRING) return null;

            var raw = new byte[size];
            if (CM_Get_DevNode_PropertyW(node, ref key, out _, raw, ref size, 0) != CR_SUCCESS)
                return null;

            return Encoding.Unicode.GetString(raw).TrimEnd('\0').Trim();
        }
        catch { return null; }
    }

    // ── Deduplication (unchanged: walk up to USB composite root) ─────────────────

    private static string GetDedupeKey(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CR_SUCCESS) goto fallback;

            for (int depth = 0; depth < 6; depth++)
            {
                if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) break;
                if (CM_Get_Device_ID_Size(out uint size, parent, 0) != CR_SUCCESS) break;

                var sb = new StringBuilder((int)(size + 1));
                if (CM_Get_Device_IDW(parent, sb, size + 1, 0) != CR_SUCCESS) break;
                var parentId = sb.ToString();

                if (VidRx.IsMatch(parentId) && PidRx.IsMatch(parentId) &&
                    !parentId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                    !parentId.Contains("&Col", StringComparison.OrdinalIgnoreCase))
                    return parentId;

                node = parent;
            }
        }
        catch { }

        fallback:
        var slash = instanceId.LastIndexOf('\\');
        if (slash < 0) return instanceId;
        var hwPath   = InterfaceTagRx.Replace(instanceId[..slash], "");
        var instance = instanceId[(slash + 1)..];
        var lastAmp  = instance.LastIndexOf('&');
        if (lastAmp > 0) instance = instance[..lastAmp];
        return $"{hwPath}\\{instance}";
    }

    // ── PnP enabled state (unchanged) ────────────────────────────────────────────

    private static bool IsPnpDisabled(string instanceId)
    {
        // CM_Get_DevNode_Status to check DN_NEED_RESTART / DN_HAS_PROBLEM with CM_PROB_DISABLED.
        // Simpler: rely on ConfigFlags check plus a WMI-free CM approach.
        // For now, fall back to ConfigFlags which covers the 3010 / pnputil disable path.
        return ReadConfigFlags(instanceId) == 1;
    }

    private static int ReadConfigFlags(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            return Convert.ToInt32(key?.GetValue("ConfigFlags") ?? 0);
        }
        catch { return 0; }
    }
}
