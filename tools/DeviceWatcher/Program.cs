// DeviceWatcher — live HID device visibility monitor for testing ControllerManager profiles.
//
// Uses the same device enumeration as the Devices tab:
//   CM_Get_Device_ID_ListW  →  SetupDiGetDeviceInterfaceDetailW  →  HidD_Get*
//   Gaming filter: UsagePage=0x05 or (UsagePage=0x01 & Usage=0x04/0x05)
//
// Usage:
//   DeviceWatcher.exe           (run standalone, Q or Ctrl-C to exit)
//   Set as the game exe in a ControllerManager profile to test hide/show behaviour.
//
// States:
//   ● OPEN     CreateFile succeeded — device is fully accessible
//   ○ HIDDEN   ERROR_ACCESS_DENIED — HidHide is hiding this device
//   - DISABLED No HID interface / device not accessible
//   ? UNKNOWN  Unexpected error

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DeviceWatcher;

static class Program
{
    const int PollMs = 500;

    // ── P/Invoke ──────────────────────────────────────────────────────────────────

    // kernel32
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryDosDevice(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

    // CfgMgr32
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    static extern int CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder Buffer, uint BufferLen, uint ulFlags);

    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const uint CM_GETIDLIST_FILTER_CLASS   = 0x200;
    const int  CR_SUCCESS = 0;

    // SetupAPI
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }

    sealed class DevInfoHandle : SafeHandleMinusOneIsInvalid
    {
        DevInfoHandle() : base(true) { }
        protected override bool ReleaseHandle() { SetupDiDestroyDeviceInfoList(handle); return true; }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern DevInfoHandle SetupDiGetClassDevsW(in Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiEnumDeviceInterfaces(DevInfoHandle DeviceInfoSet, IntPtr DeviceInfoData, in Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(DevInfoHandle DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    const uint DIGCF_DEVICEINTERFACE = 0x10;

    // hid.dll
    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct HIDP_CAPS { public ushort Usage; public ushort UsagePage; /* rest unused */ [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public ushort[] Reserved; }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetProductString(SafeFileHandle h, [Out] char[] buf, uint len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetManufacturerString(SafeFileHandle h, [Out] char[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    const int    HIDP_STATUS_SUCCESS  = unchecked((int)0x00110000);
    const uint   GENERIC_READ         = 0x80000000;
    const uint   FILE_SHARE_ALL       = 0x7;
    const uint   OPEN_EXISTING        = 3;
    const int    ERROR_ACCESS_DENIED  = 5;
    const ushort VALVE_VID            = 0x28DE;

    static readonly Guid HidClassGuid     = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");
    static readonly Guid HidInterfaceGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    static readonly Regex VidRx = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PidRx = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TagRx = new(@"&(?:MI_|Col)[0-9A-Fa-f]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Entry point ───────────────────────────────────────────────────────────────

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible  = false;
        Console.Title          = "DeviceWatcher";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        _ = Task.Run(() => { while (!cts.IsCancellationRequested && !(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)) { } cts.Cancel(); });

        // State: instanceId → (name, path, lastState, lastChangeTime)
        var rows = new List<DeviceRow>();

        Console.Clear();

        while (!cts.Token.IsCancellationRequested)
        {
            var devices = EnumerateGamingDevices();

            // Rebuild row list if device set changed
            bool rebuild = devices.Count != rows.Count
                || devices.Any(d => !rows.Any(r => r.InstanceId == d.InstanceId));

            if (rebuild)
            {
                rows = devices.Select(d => new DeviceRow(d.InstanceId, d.FriendlyName, d.Path)).ToList();
                Console.Clear();
                DrawHeader();
            }

            // Update each row
            for (int i = 0; i < rows.Count; i++)
            {
                var state = ProbeState(rows[i].Path);
                if (state != rows[i].State)
                {
                    rows[i].State          = state;
                    rows[i].LastChangeTime = DateTime.Now;
                }
                DrawRow(rows[i], HeaderRows + i);
            }

            // Clear any rows below if list shrank (handled by Console.Clear on rebuild)

            try { await Task.Delay(PollMs, cts.Token); } catch (OperationCanceledException) { break; }
        }

        Console.CursorVisible = true;
        Console.ResetColor();
        Console.Clear();
        Console.WriteLine("DeviceWatcher exited.");
    }

    // ── Display ────────────────────────────────────────────────────────────────────

    const int HeaderRows = 4;

    static void DrawHeader()
    {
        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  DeviceWatcher — HID gaming devices     (Q or Ctrl-C to exit)");
        Console.ResetColor();
        Console.WriteLine(new string('─', 80));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {"State",-12}  {"Device",-44}  Last changed");
        Console.ResetColor();
        Console.WriteLine(new string('─', 80));
    }

    static void DrawRow(DeviceRow r, int line)
    {
        Console.SetCursorPosition(0, line);
        var (icon, color) = StateStyle(r.State);
        Console.ForegroundColor = color;
        Console.Write($"  {icon}  {StateLabel(r.State),-9}");
        Console.ResetColor();
        var ts = r.LastChangeTime.HasValue ? r.LastChangeTime.Value.ToString("HH:mm:ss.fff") : "—";
        Console.Write($"  {Truncate(r.FriendlyName, 44),-44}  {ts}".PadRight(74));
        Console.WriteLine();
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static string StateLabel(DeviceState s) => s switch
    {
        DeviceState.Open    => "OPEN",
        DeviceState.Hidden  => "HIDDEN",
        DeviceState.Disabled => "DISABLED",
        _                   => "UNKNOWN",
    };

    static (string icon, ConsoleColor color) StateStyle(DeviceState s) => s switch
    {
        DeviceState.Open    => ("●", ConsoleColor.Green),
        DeviceState.Hidden  => ("○", ConsoleColor.Red),
        DeviceState.Disabled => ("-", ConsoleColor.DarkYellow),
        _                   => ("?", ConsoleColor.DarkGray),
    };

    // ── Accessibility probe ────────────────────────────────────────────────────────

    static DeviceState ProbeState(string? path)
    {
        if (string.IsNullOrEmpty(path)) return DeviceState.Disabled;
        // Use GENERIC_READ — zero-access opens may bypass WDF filter driver callbacks
        // and never reach HidHide's OnDeviceFileCreate, making hidden devices appear open.
        using var h = CreateFile(path, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return DeviceState.Open;
        return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? DeviceState.Hidden : DeviceState.Disabled;
    }

    // ── Device enumeration (mirrors DeviceEnumerator.GetAll) ──────────────────────

    record DeviceInfo(string InstanceId, string FriendlyName, string? Path);

    static List<DeviceInfo> EnumerateGamingDevices()
    {
        var results  = new List<DeviceInfo>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var classStr = HidClassGuid.ToString("B").ToUpperInvariant();
        uint flags   = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint needed, classStr, flags) != CR_SUCCESS) return results;
        var buf = new char[needed];
        if (CM_Get_Device_ID_ListW(classStr, buf, needed, flags) != CR_SUCCESS) return results;

        var ids = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        foreach (var id in ids)
        {
            if (id.StartsWith("USB\\",  StringComparison.OrdinalIgnoreCase)) continue;
            if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) continue;

            var path = GetSymbolicLink(id) ?? DerivedPath(id);

            var info = QueryDeviceInfo(id, path);
            if (!IsGamingDevice(info.Vid, info.Pid, info.UsagePage, info.Usage)
                && !info.AccessDenied) continue;

            var key = GetDedupeKey(id);
            if (!seenKeys.Add(key)) continue;

            var name = BuildName(info.Vendor, info.Product, id);
            results.Add(new DeviceInfo(id, name, path));
        }

        return [.. results.OrderBy(d => d.FriendlyName)];
    }

    static bool IsGamingDevice(ushort vid, ushort pid, ushort usagePage, ushort usage)
    {
        if (vid == VALVE_VID && (pid == 0x1205 || pid == 0x1142)) return true;
        if (usagePage == 0x05) return true;
        if (usagePage == 0x01 && (usage == 0x04 || usage == 0x05)) return true;
        return false;
    }

    // ── HID device info ───────────────────────────────────────────────────────────

    record HidInfo(ushort Vid, ushort Pid, ushort UsagePage, ushort Usage,
                   string Vendor, string Product, bool AccessDenied);

    static HidInfo QueryDeviceInfo(string instanceId, string? path)
    {
        ushort vid = 0, pid = 0, up = 0, usage = 0;
        string vendor = "", product = "";

        if (string.IsNullOrEmpty(path))
        {
            var vidM = VidRx.Match(instanceId);
            var pidM = PidRx.Match(instanceId);
            if (vidM.Success) vid = Convert.ToUInt16(vidM.Groups[1].Value, 16);
            if (pidM.Success) pid = Convert.ToUInt16(pidM.Groups[1].Value, 16);
            return new(vid, pid, up, usage, vendor, product, false);
        }

        using var h = CreateFile(path, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            bool denied = Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            var vm = VidRx.Match(instanceId); var pm = PidRx.Match(instanceId);
            if (vm.Success) vid = Convert.ToUInt16(vm.Groups[1].Value, 16);
            if (pm.Success) pid = Convert.ToUInt16(pm.Groups[1].Value, 16);
            return new(vid, pid, up, usage, vendor, product, denied);
        }

        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (HidD_GetAttributes(h, ref attrs)) { vid = attrs.VendorID; pid = attrs.ProductID; }

        var sbuf = new char[127];
        if (HidD_GetManufacturerString(h, sbuf, (uint)(sbuf.Length * 2)))
        { int e = Array.IndexOf(sbuf, '\0'); vendor  = e > 0 ? new string(sbuf, 0, e) : ""; }
        if (HidD_GetProductString(h, sbuf, (uint)(sbuf.Length * 2)))
        { int e = Array.IndexOf(sbuf, '\0'); product = e > 0 ? new string(sbuf, 0, e) : ""; }

        if (HidD_GetPreparsedData(h, out var pre) && pre != IntPtr.Zero)
        {
            try { if (HidP_GetCaps(pre, out var caps) == HIDP_STATUS_SUCCESS) { up = caps.UsagePage; usage = caps.Usage; } }
            finally { HidD_FreePreparsedData(pre); }
        }

        return new(vid, pid, up, usage, vendor, product, false);
    }

    static string BuildName(string vendor, string product, string instanceId)
    {
        var parts = new List<string>();
        foreach (var w in (vendor + " " + product).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (!parts.Any(p => p.Equals(w, StringComparison.OrdinalIgnoreCase))) parts.Add(w);
        if (parts.Count > 0) return string.Join(" ", parts);

        // Fallback: format VID:PID from instance ID
        var vm = VidRx.Match(instanceId); var pm = PidRx.Match(instanceId);
        if (vm.Success && pm.Success) return $"VID_{vm.Groups[1].Value.ToUpper()}&PID_{pm.Groups[1].Value.ToUpper()}";
        return instanceId;
    }

    // ── SetupDi symbolic link ─────────────────────────────────────────────────────

    static string? GetSymbolicLink(string instanceId)
    {
        using var h = SetupDiGetClassDevsW(in HidInterfaceGuid, instanceId, IntPtr.Zero, DIGCF_DEVICEINTERFACE);
        if (h.IsInvalid) return null;
        var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
        if (!SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, in HidInterfaceGuid, 0, ref ifd)) return null;
        SetupDiGetDeviceInterfaceDetailW(h, ref ifd, IntPtr.Zero, 0, out uint sz, IntPtr.Zero);
        if (sz == 0) return null;
        var ptr = Marshal.AllocHGlobal((int)sz);
        try
        {
            Marshal.WriteInt32(ptr, Environment.Is64BitProcess ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(h, ref ifd, ptr, sz, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(ptr + 4);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static string DerivedPath(string instanceId)
    {
        var n = instanceId.Replace('\\', '#').ToLowerInvariant();
        return $@"\\?\hid#{n}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";
    }

    // ── Dedup (composite root walk, mirrors DeviceEnumerator) ────────────────────

    static string GetDedupeKey(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CR_SUCCESS) goto fallback;
            for (int d = 0; d < 6; d++)
            {
                if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) break;
                if (CM_Get_Device_ID_Size(out uint sz, parent, 0) != CR_SUCCESS) break;
                var sb = new StringBuilder((int)(sz + 1));
                if (CM_Get_Device_IDW(parent, sb, sz + 1, 0) != CR_SUCCESS) break;
                var pid = sb.ToString();
                if (VidRx.IsMatch(pid) && PidRx.IsMatch(pid)
                    && !pid.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                    && !pid.Contains("&Col",  StringComparison.OrdinalIgnoreCase))
                    return pid;
                node = parent;
            }
        }
        catch { }
        fallback:
        var slash = instanceId.LastIndexOf('\\');
        if (slash < 0) return instanceId;
        var hw = TagRx.Replace(instanceId[..slash], "");
        var inst = instanceId[(slash + 1)..];
        var amp  = inst.LastIndexOf('&');
        return $"{hw}\\{(amp > 0 ? inst[..amp] : inst)}";
    }
}

// ── Types ─────────────────────────────────────────────────────────────────────

enum DeviceState { Open, Hidden, Disabled, Unknown }

class DeviceRow(string instanceId, string friendlyName, string? path)
{
    public string    InstanceId    { get; } = instanceId;
    public string    FriendlyName  { get; } = friendlyName;
    public string?   Path          { get; } = path;
    public DeviceState State       { get; set; } = DeviceState.Unknown;
    public DateTime? LastChangeTime { get; set; }
}
