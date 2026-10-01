using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ControllerManager.Models;

namespace ControllerManager.Services;

/// <summary>
/// Phase-1 USB diagnostics: enumerates every present USB-enumerator devnode and
/// classifies each by identity stability (serial- vs port-derived), parent hub /
/// port, and driver/PnP problem code. Pure CfgMgr property reads — no device file
/// opens, no admin-only IOCTLs — so it covers HidHide-blocked devices too.
///
/// The headline signal is <see cref="UsbIdentityKind.PortDerived"/>: a device with
/// no firmware serial gets a location-based instance ID that can shift when the
/// hub's device population changes, scrambling anything (audio channel maps, input
/// bindings) keyed on it. See [[project_diagnostics_tab]].
/// </summary>
public sealed class UsbDiagnostics
{
    // ── Public API ────────────────────────────────────────────────────────────────

    /// <summary>Enumerate all present USB devnodes, sorted top-level devices first.</summary>
    public List<UsbDeviceNode> Enumerate()
    {
        var nodes = new List<UsbDeviceNode>();

        foreach (var instanceId in GetUsbInstanceIds())
        {
            try { nodes.Add(BuildNode(instanceId)); }
            catch (Exception ex) { Logger.WriteException($"UsbDiagnostics.BuildNode({instanceId})", ex); }
        }

        // Composite parents before their MI children, then by description.
        return [.. nodes
            .OrderBy(n => n.IsCompositeChild)
            .ThenBy(n => n.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Human-readable report for the one-shot CLI dump (and a stand-in for the
    /// future Copy-report button). Leads with a summary of port-derived and
    /// problem devices, then the full table.
    /// </summary>
    public string BuildReport()
    {
        var nodes = Enumerate();
        var sb = new StringBuilder();

        sb.AppendLine($"USB Diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"{nodes.Count} present USB devnode(s)");
        sb.AppendLine();

        var portDerived = nodes.Where(n => n.IdentityKind == UsbIdentityKind.PortDerived).ToList();
        var problems    = nodes.Where(n => n.HasProblem).ToList();

        sb.AppendLine("── Summary ─────────────────────────────────────────────");
        sb.AppendLine($"Port-derived identity (mappings may move): {portDerived.Count}");
        foreach (var n in portDerived)
            sb.AppendLine($"    • {Name(n)}  [{n.VendorId}:{n.ProductId}]  on {Hub(n)}");
        sb.AppendLine($"Driver/PnP problems: {problems.Count}");
        foreach (var n in problems)
            sb.AppendLine($"    • {Name(n)}  → {ProblemText(n.ProblemCode)} (code {n.ProblemCode})");
        sb.AppendLine();

        sb.AppendLine("── All USB devices ─────────────────────────────────────");
        foreach (var n in nodes)
        {
            var indent = n.IsCompositeChild ? "    " : "";
            sb.AppendLine($"{indent}{Name(n)}  [{n.VendorId}:{n.ProductId}]");
            sb.AppendLine($"{indent}    identity : {IdentityText(n)}");
            sb.AppendLine($"{indent}    instance : {n.InstanceId}");
            if (!n.IsCompositeChild)
                sb.AppendLine($"{indent}    location : {Hub(n)}{(n.PortNumber is { } p ? $", port {p}" : "")}" +
                              $"{(string.IsNullOrEmpty(n.LocationInfo) ? "" : $"  ({n.LocationInfo})")}");
            if (n.HasProblem)
                sb.AppendLine($"{indent}    PROBLEM  : {ProblemText(n.ProblemCode)} (code {n.ProblemCode})");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Snapshot & compare ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Capture the current USB device identities as a persistable snapshot.</summary>
    public UsbSnapshot CaptureSnapshot() => new()
    {
        CapturedAt = DateTime.Now,
        Devices = [.. Enumerate().Select(n => new UsbSnapshotEntry
        {
            InstanceId       = n.InstanceId,
            VendorId         = n.VendorId,
            ProductId        = n.ProductId,
            Description      = n.Description,
            IdentityKind     = n.IdentityKind.ToString(),
            UniqueToken      = n.UniqueToken,
            ParentHubName    = n.ParentHubName,
            PortNumber       = n.PortNumber,
            ContainerId      = n.ContainerId,
            IsCompositeChild = n.IsCompositeChild,
        })],
    };

    public static void SaveSnapshot(UsbSnapshot snapshot, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts));

    public static UsbSnapshot? LoadSnapshot(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UsbSnapshot>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) { Logger.WriteException("UsbDiagnostics.LoadSnapshot", ex); return null; }
    }

    /// <summary>
    /// Diff two snapshots and describe what moved. Matching is by ContainerId first
    /// (stable per physical device), then exact InstanceId, then VID/PID + serial
    /// token — so a device whose identity *changed* still gets paired to its old
    /// self and the change is reported rather than showing as remove+add. Devices
    /// left unmatched are paired by VID/PID + name as a last resort (catches a fully
    /// regenerated identity, e.g. ContainerId itself changed). Composite interface
    /// children are excluded — their identity just follows the parent.
    /// </summary>
    public static string BuildComparisonReport(UsbSnapshot before, UsbSnapshot after)
    {
        var beforeTop = before.Devices.Where(d => !d.IsCompositeChild).ToList();
        var afterTop  = after.Devices.Where(d => !d.IsCompositeChild).ToList();

        var afterRemaining = afterTop.ToList();
        var moved      = new List<(UsbSnapshotEntry B, UsbSnapshotEntry A, List<string> Changes)>();
        var regenerated = new List<(UsbSnapshotEntry B, UsbSnapshotEntry A)>();
        var unchanged  = 0;
        var removed    = new List<UsbSnapshotEntry>();

        // Pass 1: strong matches (container / instance / serial token).
        foreach (var b in beforeTop)
        {
            int idx = FindMatch(b, afterRemaining, strong: true);
            if (idx < 0) { removed.Add(b); continue; }

            var a = afterRemaining[idx];
            afterRemaining.RemoveAt(idx);

            var changes = DiffEntry(b, a);
            if (changes.Count == 0) unchanged++;
            else moved.Add((b, a, changes));
        }

        // Pass 2: weak match (VID/PID + name) for anything left over — a fully
        // regenerated identity where even the ContainerId changed.
        foreach (var b in removed.ToList())
        {
            int idx = FindMatch(b, afterRemaining, strong: false);
            if (idx < 0) continue;
            var a = afterRemaining[idx];
            afterRemaining.RemoveAt(idx);
            removed.Remove(b);
            regenerated.Add((b, a));
        }

        var added = afterRemaining;

        var sb = new StringBuilder();
        sb.AppendLine("USB Snapshot Comparison");
        sb.AppendLine($"Before: {before.CapturedAt:yyyy-MM-dd HH:mm:ss}  ({beforeTop.Count} devices)");
        sb.AppendLine($"After:  {after.CapturedAt:yyyy-MM-dd HH:mm:ss}  ({afterTop.Count} devices)");
        sb.AppendLine();

        sb.AppendLine("── Devices whose identity MOVED ────────────────────────");
        if (moved.Count == 0) sb.AppendLine("    (none — every matched device kept the same identity)");
        foreach (var (b, a, changes) in moved)
        {
            sb.AppendLine($"  {NameOf(b)}  [{b.VendorId}:{b.ProductId}]");
            foreach (var c in changes) sb.AppendLine($"      {c}");
            sb.AppendLine();
        }
        sb.AppendLine();

        if (regenerated.Count > 0)
        {
            sb.AppendLine("── Identity fully regenerated (ContainerId changed) ────");
            foreach (var (b, a) in regenerated)
            {
                sb.AppendLine($"  {NameOf(b)}  [{b.VendorId}:{b.ProductId}]");
                sb.AppendLine($"      instance : {b.InstanceId}");
                sb.AppendLine($"           now : {a.InstanceId}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (added.Count > 0)
        {
            sb.AppendLine("── Added (present only in the new snapshot) ────────────");
            foreach (var a in added) sb.AppendLine($"    + {NameOf(a)}  [{a.VendorId}:{a.ProductId}]");
            sb.AppendLine();
        }
        if (removed.Count > 0)
        {
            sb.AppendLine("── Removed (present only in the old snapshot) ──────────");
            foreach (var r in removed) sb.AppendLine($"    - {NameOf(r)}  [{r.VendorId}:{r.ProductId}]");
            sb.AppendLine();
        }

        sb.AppendLine($"Unchanged: {unchanged} device(s).");
        return sb.ToString();
    }

    private static int FindMatch(UsbSnapshotEntry b, List<UsbSnapshotEntry> candidates, bool strong)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (strong)
            {
                if (!string.IsNullOrEmpty(b.ContainerId) && b.ContainerId == c.ContainerId) return i;
                if (b.InstanceId == c.InstanceId) return i;
                if (b.IdentityKind == nameof(UsbIdentityKind.SerialBased)
                    && b.VendorId == c.VendorId && b.ProductId == c.ProductId
                    && b.UniqueToken == c.UniqueToken) return i;
            }
            else if (b.VendorId == c.VendorId && b.ProductId == c.ProductId
                     && b.Description == c.Description)
            {
                return i;
            }
        }
        return -1;
    }

    private static List<string> DiffEntry(UsbSnapshotEntry b, UsbSnapshotEntry a)
    {
        var changes = new List<string>();
        if (b.InstanceId != a.InstanceId)
            changes.Add($"instance : {b.InstanceId}  →  {a.InstanceId}");
        if (b.UniqueToken != a.UniqueToken)
            changes.Add($"token    : {b.UniqueToken}  →  {a.UniqueToken}");
        if (b.IdentityKind != a.IdentityKind)
            changes.Add($"identity : {b.IdentityKind}  →  {a.IdentityKind}");
        if (b.PortNumber != a.PortNumber)
            changes.Add($"port     : {b.PortNumber?.ToString() ?? "?"}  →  {a.PortNumber?.ToString() ?? "?"}");
        if (b.ParentHubName != a.ParentHubName)
            changes.Add($"hub      : {b.ParentHubName}  →  {a.ParentHubName}");
        return changes;
    }

    private static string NameOf(UsbSnapshotEntry e) =>
        string.IsNullOrWhiteSpace(e.Description) ? "(unnamed device)" : e.Description;

    private static string Name(UsbDeviceNode n) =>
        string.IsNullOrWhiteSpace(n.Description) ? "(unnamed device)" : n.Description;

    private static string Hub(UsbDeviceNode n) =>
        string.IsNullOrWhiteSpace(n.ParentHubName) ? "(unknown hub)" : n.ParentHubName;

    private static string IdentityText(UsbDeviceNode n) => n.IdentityKind switch
    {
        UsbIdentityKind.SerialBased => $"serial-based (stable) — serial '{n.UniqueToken}'",
        UsbIdentityKind.PortDerived => $"PORT-DERIVED (may move) — no serial; location token '{n.UniqueToken}'",
        UsbIdentityKind.Inherited   => "interface of a composite device (inherits parent identity)",
        _                           => "unknown",
    };

    // ── Node construction ─────────────────────────────────────────────────────────

    private static UsbDeviceNode BuildNode(string instanceId)
    {
        var (kind, isComposite, token) = Classify(instanceId);

        uint?   port = null;
        string  location = "", hubName = "", hubInstance = "", description = "";
        string? containerId = null;
        uint    problem = 0;

        if (CM_Locate_DevNodeW(out uint node, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
        {
            description = BestDescription(node) ?? "";
            containerId = ReadGuidProperty(node, DEVPKEY_Device_ContainerId);
            location    = ReadStringProperty(node, DEVPKEY_Device_LocationInfo) ?? "";
            port        = ReadUint32Property(node, DEVPKEY_Device_Address);

            if (CM_Get_DevNode_Status(out _, out uint prob, node, 0) == CR_SUCCESS)
                problem = prob;

            if (CM_Get_Parent(out uint parent, node, 0) == CR_SUCCESS)
            {
                hubName     = ReadStringProperty(parent, DEVPKEY_Device_FriendlyName)
                              ?? ReadStringProperty(parent, DEVPKEY_Device_DeviceDesc) ?? "";
                hubInstance = GetDeviceId(parent) ?? "";
            }
        }

        var (vid, pid) = ParseVidPid(instanceId);

        return new UsbDeviceNode
        {
            InstanceId          = instanceId,
            VendorId            = vid,
            ProductId           = pid,
            Description         = description,
            IsCompositeChild    = isComposite,
            IdentityKind        = kind,
            UniqueToken         = token,
            ParentHubName       = hubName,
            ParentHubInstanceId = hubInstance,
            PortNumber          = port,
            LocationInfo        = location,
            ContainerId         = containerId,
            ProblemCode         = problem,
        };
    }

    /// <summary>
    /// Classify a USB instance ID. Format is
    /// <c>USB\VID_xxxx&amp;PID_xxxx[&amp;MI_zz]\&lt;unique&gt;</c>. A composite
    /// interface child (<c>&amp;MI_</c>) inherits its parent's identity. Otherwise
    /// the trailing segment is a firmware serial (no '&amp;') or a Windows-generated
    /// location token (contains '&amp;') — the long-documented USBView heuristic.
    /// </summary>
    private static (UsbIdentityKind Kind, bool IsComposite, string Token) Classify(string instanceId)
    {
        var parts = instanceId.Split('\\');
        bool composite = parts.Length > 1 &&
                         parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase);
        string token = parts.Length > 2 ? parts[^1] : "";

        if (composite)                       return (UsbIdentityKind.Inherited, true, token);
        if (string.IsNullOrEmpty(token))     return (UsbIdentityKind.Unknown, false, token);
        return token.Contains('&')
            ? (UsbIdentityKind.PortDerived, false, token)
            : (UsbIdentityKind.SerialBased, false, token);
    }

    private static (string Vid, string Pid) ParseVidPid(string instanceId)
    {
        string vid = "", pid = "";
        int vi = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vi >= 0 && vi + 8 <= instanceId.Length) vid = instanceId.Substring(vi + 4, 4).ToUpperInvariant();
        int pi = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pi >= 0 && pi + 8 <= instanceId.Length) pid = instanceId.Substring(pi + 4, 4).ToUpperInvariant();
        return (vid, pid);
    }

    /// <summary>FriendlyName → BusReportedDeviceDesc (own node) → DeviceDesc.</summary>
    private static string? BestDescription(uint node)
    {
        var friendly = ReadStringProperty(node, DEVPKEY_Device_FriendlyName);
        if (!string.IsNullOrWhiteSpace(friendly)) return friendly;

        var busName = ReadStringProperty(node, DEVPKEY_BusReportedDeviceDesc);
        if (!string.IsNullOrWhiteSpace(busName)) return busName;

        return ReadStringProperty(node, DEVPKEY_Device_DeviceDesc);
    }

    // ── CM_PROB_* problem-code text ───────────────────────────────────────────────

    /// <summary>Plain-language text for the common CM_PROB_* problem codes. The
    /// findings engine (later phase) maps these to recommendations.</summary>
    public static string ProblemText(uint code) => code switch
    {
        0  => "OK",
        1  => "Not configured correctly (CM_PROB_NOT_CONFIGURED)",
        9  => "Windows can't identify this hardware (CM_PROB_INVALID_DATA)",
        10 => "Device cannot start (CM_PROB_FAILED_START)",
        14 => "Needs a restart to work (CM_PROB_NEED_RESTART)",
        18 => "Drivers need reinstalling (CM_PROB_REINSTALL)",
        19 => "Registry/config corrupt (CM_PROB_REGISTRY)",
        21 => "Being removed (CM_PROB_DISABLED)",
        22 => "Disabled (CM_PROB_DISABLED)",
        24 => "Not present / disconnected (CM_PROB_DEVICE_NOT_THERE)",
        28 => "Drivers not installed (CM_PROB_FAILED_INSTALL)",
        31 => "Driver failed to load (CM_PROB_FAILED_ADD)",
        43 => "Driver reported a failure (CM_PROB_FAILED_POST_START)",
        45 => "Currently disconnected (CM_PROB_PHANTOM)",
        48 => "Driver blocked from starting (CM_PROB_DRIVER_BLOCKED)",
        49 => "Registry too large (CM_PROB_REGISTRY_TOO_LARGE)",
        52 => "Driver signature can't be verified (CM_PROB_UNSIGNED_DRIVER)",
        54 => "Failed and is being reset",
        56 => "Not enough USB controller resources (endpoint exhaustion)",
        _  => $"Problem code {code}",
    };

    // ── Enumeration: USB-enumerator instance IDs ──────────────────────────────────

    private static List<string> GetUsbInstanceIds()
    {
        const string enumerator = "USB";
        uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;

        if (CM_Get_Device_ID_List_SizeW(out uint needed, enumerator, flags) != CR_SUCCESS || needed == 0)
            return [];

        var buf = new char[needed];
        if (CM_Get_Device_ID_ListW(enumerator, buf, needed, flags) != CR_SUCCESS)
            return [];

        return [.. new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    // ── CfgMgr property readers ────────────────────────────────────────────────────

    private static string? ReadStringProperty(uint node, DEVPROPKEY key)
    {
        uint type = 0, size = 0;
        CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref size, 0);
        if (size == 0 || type != DEVPROP_TYPE_STRING) return null;
        var raw = new byte[size];
        if (CM_Get_DevNode_PropertyW(node, ref key, out _, raw, ref size, 0) != CR_SUCCESS) return null;
        return Encoding.Unicode.GetString(raw).TrimEnd('\0').Trim();
    }

    private static uint? ReadUint32Property(uint node, DEVPROPKEY key)
    {
        uint type = 0, size = sizeof(uint);
        var raw = new byte[sizeof(uint)];
        if (CM_Get_DevNode_PropertyW(node, ref key, out type, raw, ref size, 0) != CR_SUCCESS) return null;
        if (type != DEVPROP_TYPE_UINT32) return null;
        return BitConverter.ToUInt32(raw, 0);
    }

    private static string? ReadGuidProperty(uint node, DEVPROPKEY key)
    {
        uint type = 0, size = 16;
        var raw = new byte[16];
        if (CM_Get_DevNode_PropertyW(node, ref key, out type, raw, ref size, 0) != CR_SUCCESS) return null;
        if (type != DEVPROP_TYPE_GUID || size != 16) return null;
        var guid = new Guid(raw);
        return guid == Guid.Empty ? null : guid.ToString("B").ToUpperInvariant();
    }

    private static string? GetDeviceId(uint node)
    {
        var buf = new char[MAX_DEVICE_ID_LEN];
        if (CM_Get_Device_IDW(node, buf, (uint)buf.Length, 0) != CR_SUCCESS) return null;
        int end = Array.IndexOf(buf, '\0');
        return new string(buf, 0, end > 0 ? end : buf.Length);
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // DEVPKEY_Device_* live under fmtid {a45c254e-df1c-4efd-8020-67d146a850e0}.
    private static readonly Guid DevpkeyDeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static DEVPROPKEY DEVPKEY_Device_DeviceDesc    => new() { fmtid = DevpkeyDeviceFmtid, pid = 2  };
    private static DEVPROPKEY DEVPKEY_Device_FriendlyName  => new() { fmtid = DevpkeyDeviceFmtid, pid = 14 };
    private static DEVPROPKEY DEVPKEY_Device_LocationInfo  => new() { fmtid = DevpkeyDeviceFmtid, pid = 15 };
    private static DEVPROPKEY DEVPKEY_Device_Address       => new() { fmtid = DevpkeyDeviceFmtid, pid = 30 };
    private static DEVPROPKEY DEVPKEY_BusReportedDeviceDesc => new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    private static DEVPROPKEY DEVPKEY_Device_ContainerId   => new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };

    private const int  CR_SUCCESS                    = 0;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    private const uint CM_GETIDLIST_FILTER_PRESENT    = 0x100;
    private const uint CM_LOCATE_DEVNODE_NORMAL       = 0x0;
    private const uint DEVPROP_TYPE_STRING            = 0x12;
    private const uint DEVPROP_TYPE_UINT32            = 0x07;
    private const uint DEVPROP_TYPE_GUID              = 0x0D;
    private const int  MAX_DEVICE_ID_LEN             = 200;

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber,
                                                    uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        [Out] byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);
}
