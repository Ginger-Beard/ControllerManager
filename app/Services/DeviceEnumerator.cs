using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HIDReorder.Models;

namespace HIDReorder.Services;

public sealed class DeviceEnumerator(VidResolver resolver)
{
    private static readonly Regex VidRx = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
    private static readonly Regex PidRx = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    private static readonly string[] GenericPrefixes =
        ["HID-compliant", "USB Input Device", "USB Composite"];

    // ── BusReportedDeviceDesc P/Invoke ───────────────────────────────────────────

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

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        [Out] byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    private const int CR_SUCCESS        = 0;
    private const uint DEVPROP_TYPE_STRING = 0x12;

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

    // ── Enumeration ──────────────────────────────────────────────────────────────

    public List<HidDevice> GetAll(bool showAllHid = false)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<HidDevice>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity " +
            "WHERE ClassGuid = '{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}'");

        foreach (ManagementObject obj in searcher.Get())
        {
            var instanceId = obj["DeviceID"]?.ToString();
            var wmiName    = obj["Name"]?.ToString() ?? "";
            if (instanceId is null) continue;

            var errorCode = (uint)(obj["ConfigManagerErrorCode"] ?? 0u);
            bool isEnabled = errorCode != 22;

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

            if (!seen.Add(vidPid)) continue;

            bool isGameController = wmiName.Contains("game controller",
                StringComparison.OrdinalIgnoreCase);
            bool isKnownSim = resolver.IsKnownSimVid(vid);

            if (!showAllHid && !isGameController && !isKnownSim) continue;

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

            results.Add(new HidDevice
            {
                InstanceId   = instanceId,
                VendorId     = vid,
                ProductId    = pid,
                FriendlyName = friendlyName,
                VendorLabel  = vendorLabel,
                IsEnabled    = isEnabled,
            });
        }

        return [.. results.OrderBy(d => d.FriendlyName)];
    }
}
