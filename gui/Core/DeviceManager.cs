using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HidReorder.Core;

public static class DeviceManager
{
    private static readonly Regex VidRx = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
    private static readonly Regex PidRx = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    private static readonly string[] GenericPrefixes =
        ["HID-compliant", "USB Input Device", "USB Composite"];

    // ── ConfigMgr P/Invoke — BusReportedDeviceDesc ──────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // DEVPKEY_Device_BusReportedDeviceDesc — the product string the firmware burned in
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

    private const int CR_SUCCESS = 0;
    private const uint DEVPROP_TYPE_STRING = 0x12;

    /// <summary>
    /// Walks up to the USB parent of a HID instance and reads the device's
    /// self-reported product name from its firmware descriptor.
    /// </summary>
    private static string? GetBusReportedName(string hidInstanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint hidNode, hidInstanceId, 0) != CR_SUCCESS)
                return null;

            if (CM_Get_Parent(out uint usbNode, hidNode, 0) != CR_SUCCESS)
                return null;

            var key  = BusReportedDeviceDesc;
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

    // ── Device enumeration ───────────────────────────────────────────────────────

    public static List<SimDevice> GetGameControllers(VidResolver resolver)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SimDevice>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity " +
            "WHERE ClassGuid = '{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}'");

        foreach (ManagementObject obj in searcher.Get())
        {
            var instanceId = obj["DeviceID"]?.ToString();
            var wmiName    = obj["Name"]?.ToString() ?? "";
            if (instanceId is null) continue;

            // ConfigManagerErrorCode 22 = disabled by user
            var errorCode = (uint)(obj["ConfigManagerErrorCode"] ?? 0u);
            bool isEnabled = errorCode != 22;

            // Try InstanceId first; fall back to HardwareID for root-enumerated
            // virtual devices (e.g. vJoy) whose InstanceId has no VID_/PID_ tokens
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
            if (!isGameController && !isKnownSim) continue;

            // JSON/curated label
            bool wmiNameIsGeneric = GenericPrefixes.Any(p =>
                wmiName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            string jsonLabel = wmiNameIsGeneric
                ? resolver.Resolve(vid, pid)
                : $"{wmiName}  [{resolver.Resolve(vid, pid)}]";

            // Firmware name from the device itself
            string? busName = GetBusReportedName(instanceId);
            // Discard hub product strings — device sits behind an internal hub whose
            // self-reported name ("USB2.1 Hub", "USB 2.0 Hub", …) is not useful.
            if (busName is not null && busName.Contains("Hub", StringComparison.OrdinalIgnoreCase))
                busName = null;

            // Concat firmware name if it adds information
            string displayName = busName is not null
                ? $"{jsonLabel}  ·  {busName}  [VID_{vid}&PID_{pid}]"
                : $"{jsonLabel}  [VID_{vid}&PID_{pid}]";

            results.Add(new SimDevice
            {
                InstanceId  = instanceId,
                VendorId    = vid,
                ProductId   = pid,
                DisplayName = displayName,
                IsEnabled   = isEnabled,
            });
        }

        return results.OrderBy(d => d.DisplayName).ToList();
    }

    // ── Enable / disable individual device ──────────────────────────────────────

    public static void SetDeviceEnabled(SimDevice dev, bool enable)
    {
        var action = enable ? "Enable-PnpDevice" : "Disable-PnpDevice";
        var script = $"$ErrorActionPreference = 'SilentlyContinue'\n" +
                     $"{action} -InstanceId '{Escape(dev.InstanceId)}' -Confirm:$false";
        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".ps1");
        File.WriteAllText(tmp, script);
        try   { RunPs(tmp); }
        finally { File.Delete(tmp); }
    }

    // ── Reorder ──────────────────────────────────────────────────────────────────

    public static void Reorder(
        SimDevice                firstDevice,
        IReadOnlyList<SimDevice> rest,
        IReadOnlyList<SimDevice> disableOnly,
        IProgress<string>        progress)
    {
        var script = BuildScript(firstDevice, rest, disableOnly);
        var tmp    = Path.ChangeExtension(Path.GetTempFileName(), ".ps1");
        File.WriteAllText(tmp, script);
        try
        {
            progress.Report("Disabling sim devices...");
            RunPs(tmp);

            var offCount = disableOnly.Count;
            var suffix   = offCount > 0
                ? $" ({offCount} device(s) left disabled)"
                : "";
            progress.Report($"Done — {firstDevice.DisplayName} is now slot #1.{suffix}");
        }
        finally { File.Delete(tmp); }
    }

    private static string BuildScript(
        SimDevice first, IReadOnlyList<SimDevice> rest, IReadOnlyList<SimDevice> disableOnly)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine();

        void EmitFind(string varName, IEnumerable<SimDevice> devices)
        {
            var patterns = devices.Select(d => d.VidPidLabel).Distinct().ToList();
            if (patterns.Count == 0) { sb.AppendLine($"${varName} = @()"); return; }
            var regex = string.Join("|", patterns.Select(Escape));
            sb.AppendLine(
                $"${varName} = Get-PnpDevice | Where-Object {{ $_.InstanceId -match '{regex}' }}");
        }

        EmitFind("first",   [first]);
        EmitFind("rest",    rest);
        EmitFind("offOnly", disableOnly);
        sb.AppendLine("$all = @($first) + @($rest) + @($offOnly)");
        sb.AppendLine();
        sb.AppendLine("$all   | Disable-PnpDevice -Confirm:$false");
        sb.AppendLine("Start-Sleep -Seconds 2");
        sb.AppendLine("$first | Enable-PnpDevice  -Confirm:$false");
        sb.AppendLine("Start-Sleep -Seconds 2");
        sb.AppendLine("$rest  | Enable-PnpDevice  -Confirm:$false");

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("'", "''");

    private static void RunPs(string scriptPath)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
        })!;
        p.WaitForExit();
    }
}
