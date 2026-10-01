namespace ControllerManager.Models;

/// <summary>
/// How stable a USB device's Windows identity is across enumeration changes.
/// This is the crux of the "channels move when I add a device to the hub" class
/// of bug: a device whose instance ID is derived from its port location (rather
/// than a firmware serial) can have that ID — and therefore any audio-endpoint
/// or input mapping keyed on it — shift when the hub's device population changes.
/// </summary>
public enum UsbIdentityKind
{
    /// <summary>Couldn't classify (malformed or non-standard instance ID).</summary>
    Unknown,

    /// <summary>Instance ID's unique segment is a firmware serial number — stable
    /// regardless of port or what else is plugged in.</summary>
    SerialBased,

    /// <summary>Instance ID's unique segment was synthesized by Windows from the
    /// device's bus location (contains '&'). NOT guaranteed stable when hub
    /// topology changes — Microsoft documents these as potentially breaking when
    /// moved to a different port.</summary>
    PortDerived,

    /// <summary>A composite-device interface child (instance ID contains
    /// <c>&amp;MI_</c>). Its stability is inherited from the parent function node,
    /// so it isn't classified on its own.</summary>
    Inherited,
}

/// <summary>
/// A single USB-enumerator devnode as seen by the diagnostics pass. Built from
/// CfgMgr/SetupAPI property reads only — no device file open required — so it
/// works for HidHide-blocked and exclusively-owned devices alike.
/// </summary>
public sealed record UsbDeviceNode
{
    public required string InstanceId { get; init; }
    public string VendorId  { get; init; } = "";
    public string ProductId { get; init; } = "";

    /// <summary>Best human-readable name (FriendlyName → BusReportedDesc → DeviceDesc).</summary>
    public string Description { get; init; } = "";

    /// <summary>True when this is a composite interface child (has <c>&amp;MI_</c>).</summary>
    public bool IsCompositeChild { get; init; }

    public UsbIdentityKind IdentityKind { get; init; }

    /// <summary>The trailing instance-ID segment — the firmware serial when
    /// <see cref="IdentityKind"/> is <see cref="UsbIdentityKind.SerialBased"/>,
    /// otherwise the Windows-generated location token.</summary>
    public string UniqueToken { get; init; } = "";

    public string  ParentHubName       { get; init; } = "";
    public string  ParentHubInstanceId { get; init; } = "";
    /// <summary>USB port number on the parent hub (DEVPKEY_Device_Address), if known.</summary>
    public uint?   PortNumber          { get; init; }
    public string  LocationInfo        { get; init; } = "";
    public string? ContainerId         { get; init; }

    /// <summary>CM_Get_DevNode_Status problem code (CM_PROB_*), 0 when healthy.</summary>
    public uint ProblemCode { get; init; }

    /// <summary>True when the device has a driver/PnP problem flagged by the kernel.</summary>
    public bool HasProblem => ProblemCode != 0;
}
