using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ControllerManager.Models;
using ControllerManager.Native;
using Microsoft.Win32;

namespace ControllerManager.Services;

public sealed class DeviceEnumerator(VidResolver resolver)
{
    private static readonly Regex VidRx = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
    private static readonly Regex PidRx = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    // Strips &MI_NN (with underscore) and &ColNN / &COLNN (no underscore).
    private static readonly Regex InterfaceTagRx = new(
        @"&(?:MI_|Col)[0-9A-Fa-f]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MiRx = new(
        @"&MI_([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] GenericPrefixes =
        ["HID-compliant", "USB Input Device", "USB Composite"];

    // ── CfgMgr32 P/Invoke ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static DEVPROPKEY BusReportedDeviceDesc => new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4
    };

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder Buffer, uint BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        [Out] byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    private const int  CR_SUCCESS          = 0;
    private const uint DEVPROP_TYPE_STRING = 0x12;

    // Walks up the device tree to find the USB composite device root — the first
    // ancestor whose instance ID contains VID+PID but no &MI_ or &Col tag. That node
    // is shared by all HID interfaces of one physical device, so using its instance ID
    // as the dedup key collapses MI_01 and MI_02 of the same device into one entry
    // while keeping two identical devices on different ports as two entries (their
    // composite roots have different serial/port hashes in the instance ID).
    // Falls back to normalised instance-ID string for virtual/root devices that have
    // no VID/PID ancestor (vJoy HID\HIDCLASS nodes, RZVIRTUAL devices, etc.).
    private static int ReadConfigFlags(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            return Convert.ToInt32(key?.GetValue("ConfigFlags") ?? 0);
        }
        catch { return 0; }
    }

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

                // Stop at the first ancestor that has VID+PID but no interface tags —
                // that is the USB composite (or simple) device root node.
                if (VidRx.IsMatch(parentId) && PidRx.IsMatch(parentId) &&
                    !parentId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                    !parentId.Contains("&Col",  StringComparison.OrdinalIgnoreCase))
                    return parentId;

                node = parent;
            }
        }
        catch { }

        fallback:
        // Strip &MI_NN / &ColNN from the hardware path and the last &NNNN port-index
        // from the instance segment so same-device interfaces produce the same key.
        var slash = instanceId.LastIndexOf('\\');
        if (slash < 0) return instanceId;
        var hwPath   = InterfaceTagRx.Replace(instanceId[..slash], "");
        var instance = instanceId[(slash + 1)..];
        var lastAmp  = instance.LastIndexOf('&');
        if (lastAmp > 0) instance = instance[..lastAmp];
        return $"{hwPath}\\{instance}";
    }

    private static string? GetBusReportedName(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint hidNode, instanceId, 0) != CR_SUCCESS) return null;
            if (CM_Get_Parent(out uint usbNode, hidNode, 0) != CR_SUCCESS) return null;

            var key = BusReportedDeviceDesc;
            uint type = 0, size = 0;
            CM_Get_DevNode_PropertyW(usbNode, ref key, out type, null, ref size, 0);
            if (size == 0 || type != DEVPROP_TYPE_STRING) return null;

            var buf = new byte[size];
            if (CM_Get_DevNode_PropertyW(usbNode, ref key, out _, buf, ref size, 0) != CR_SUCCESS)
                return null;

            var name = Encoding.Unicode.GetString(buf).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    // ── HID input capabilities ──────────────────────────────────────────────────

    /// <summary>
    /// Opens the device interface with zero access and reads its HID capability
    /// descriptor to get the number of input value (axis) and button caps.
    /// Returns (0, 0) on any failure — typical for devices whose driver does not
    /// expose a standard HID input collection (Lian Li fan controllers, audio
    /// mute/volume control surfaces using HID, etc.).
    /// </summary>
    // Counts only Generic Desktop (page 0x01) axes with joystick-relevant usages
    // (0x30–0x3F: X/Y/Z/Rx/Ry/Rz/Slider/Dial/Wheel/Hat) and Button page (0x09)
    // buttons. Vendor-defined usage pages, Consumer Controls, and keyboard usage
    // pages are ignored — those are what cause Stream Decks, Lian Li fan controllers,
    // and USB audio HID interfaces to appear as if they have joystick input.
    private static (int Axes, int Buttons) GetHidInputCaps(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return (0, 0);

        var handle = HidApi.CreateFileW(
            devicePath, 0,
            HidApi.FILE_SHARE_READ | HidApi.FILE_SHARE_WRITE,
            IntPtr.Zero, HidApi.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid) { handle.Dispose(); return (0, 0); }

        IntPtr preparsed = IntPtr.Zero;
        try
        {
            if (!HidApi.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
                return (0, 0);

            if (HidApi.HidP_GetCaps(preparsed, out var caps) != HidApi.HIDP_STATUS_SUCCESS)
                return (0, 0);

            int axes = 0, buttons = 0;

            if (caps.NumberInputValueCaps > 0)
            {
                var vc    = new HidApi.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
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
                var bc    = new HidApi.HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
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
        catch { return (0, 0); }
        finally
        {
            if (preparsed != IntPtr.Zero) HidApi.HidD_FreePreparsedData(preparsed);
            handle.Dispose();
        }
    }

    // ── Enumeration ──────────────────────────────────────────────────────────────

    public List<HidDevice> GetAll(bool showAllHid = false)
    {
        var seenKeys     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results      = new List<HidDevice>();
        var vidPidCount  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // USB function nodes filtered from display but kept as alternative disable targets.
        // Before the USB-filter was added, these nodes appeared in the device list and
        // were used for toggling. Some drivers (MOZA Windows Driver) protect their HID
        // child from Disable-PnpDevice but allow the USB function node — so we store it
        // and prefer it when toggling. Keyed by VID_PID_MI (e.g. "346E_0016_02").
        var usbFunctions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity " +
            "WHERE ClassGuid = '{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}'");

        foreach (ManagementObject obj in searcher.Get())
        {
            var instanceId = obj["DeviceID"]?.ToString();
            var wmiName    = obj["Name"]?.ToString() ?? "";
            if (instanceId is null) continue;

            // USB device/function nodes leak into the HID class query for some devices.
            // Don't display them, but collect USB function nodes (those with &MI_) as
            // alternative disable targets for their HID children.
            if (instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                var vidM2 = VidRx.Match(instanceId);
                var pidM2 = PidRx.Match(instanceId);
                var miM2  = MiRx.Match(instanceId);
                if (vidM2.Success && pidM2.Success && miM2.Success)
                {
                    var key = $"{vidM2.Groups[1].Value.ToUpperInvariant()}" +
                              $"_{pidM2.Groups[1].Value.ToUpperInvariant()}" +
                              $"_{miM2.Groups[1].Value.ToUpperInvariant()}";
                    usbFunctions.TryAdd(key, instanceId);
                }
                continue;
            }

            if (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) continue;

            var errorCode = (uint)(obj["ConfigManagerErrorCode"] ?? 0u);

            var vidM = VidRx.Match(instanceId);
            var pidM = PidRx.Match(instanceId);
            if (!vidM.Success || !pidM.Success)
            {
                var hwIds = obj["HardwareID"] as string[] ?? [];
                var hwId  = hwIds.FirstOrDefault(h => VidRx.IsMatch(h) && PidRx.IsMatch(h)) ?? "";
                vidM = VidRx.Match(hwId);
                pidM = PidRx.Match(hwId);
                if (!vidM.Success || !pidM.Success) continue;
            }

            var vid    = vidM.Groups[1].Value.ToUpperInvariant();
            var pid    = pidM.Groups[1].Value.ToUpperInvariant();
            var vidPid = $"{vid}_{pid}";

            bool isGameController = wmiName.Contains("game controller",
                StringComparison.OrdinalIgnoreCase);
            bool isKnownSim = resolver.IsKnownSimVid(vid);

            if (!showAllHid && !isGameController && !isKnownSim) continue;

            if (!seenKeys.Add(GetDedupeKey(instanceId))) continue;

            // Probe HID input capabilities. Devices that pass the VID filter but
            // have no standard input report (Lian Li fan hubs, C-Media audio
            // surfaces) end up with (0,0) and are dropped from the default view.
            var devicePath = HidApi.ToDevicePath(instanceId);
            var (axes, buttons) = GetHidInputCaps(devicePath);

            if (!showAllHid && !isGameController && axes == 0 && buttons == 0)
                continue;

            bool wmiNameIsGeneric = GenericPrefixes.Any(p =>
                wmiName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            string vendorLabel = wmiNameIsGeneric
                ? resolver.Resolve(vid, pid)
                : $"{wmiName}  [{resolver.Resolve(vid, pid)}]";

            string? busName = GetBusReportedName(instanceId);
            if (busName is not null && busName.Contains("Hub", StringComparison.OrdinalIgnoreCase))
                busName = null;

            string friendlyName = busName is not null
                ? $"{vendorLabel}  ·  {busName}"
                : vendorLabel;

            vidPidCount.TryGetValue(vidPid, out int count);
            vidPidCount[vidPid] = count + 1;

            // Look up the USB function node for this device (same VID/PID/MI) so we
            // can prefer it when toggling devices whose HID child is driver-protected.
            var miMatch  = MiRx.Match(instanceId);
            var usbKey   = miMatch.Success
                ? $"{vid}_{pid}_{miMatch.Groups[1].Value.ToUpperInvariant()}"
                : null;
            usbFunctions.TryGetValue(usbKey ?? "", out var altId);

            // ConfigManagerErrorCode 22 = CM_PROB_DISABLED (clean disable via Device Manager).
            // Some drivers (e.g. MOZA) disable via the USB function node and return exit 3010
            // (reboot-required), so the HID child never gets code 22 — its parent's ConfigFlags
            // is the authoritative source. Check the altId first, then fall back to instanceId.
            bool disabledByFlags = ReadConfigFlags(altId) == 1 || ReadConfigFlags(instanceId) == 1;
            bool isEnabled = errorCode != 22 && !disabledByFlags;

            results.Add(new HidDevice
            {
                InstanceId            = instanceId,
                VendorId              = vid,
                ProductId             = pid,
                FriendlyName          = friendlyName,
                VendorLabel           = vendorLabel,
                IsEnabled             = isEnabled,
                DuplicateIndex        = count,
                DeviceInterfacePath   = devicePath,
                AxisCount             = axes,
                ButtonCount           = buttons,
                AlternativeInstanceId = altId ?? "",
            });
        }

        // Append #1/#2 suffix only when multiple physical devices share a VID+PID
        foreach (var d in results)
        {
            var key = $"{d.VendorId}_{d.ProductId}";
            if (vidPidCount.TryGetValue(key, out int total) && total > 1)
                d.FriendlyName += $"  #{d.DuplicateIndex + 1}";
        }

        return [.. results.OrderBy(d => d.FriendlyName)];
    }
}
