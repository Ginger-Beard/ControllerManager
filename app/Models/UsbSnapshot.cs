namespace ControllerManager.Models;

/// <summary>
/// A point-in-time fingerprint of every present USB device's identity, persisted
/// to JSON. The "Compare" pass diffs a saved snapshot against a fresh one to find
/// devices whose Windows identity *moved* between two hub populations — the proof
/// step for the "channels scramble when I add a device" bug. See
/// [[project_diagnostics_tab]].
/// </summary>
public sealed class UsbSnapshot
{
    public DateTime CapturedAt { get; set; }
    public List<UsbSnapshotEntry> Devices { get; set; } = [];
}

/// <summary>
/// Serialization DTO for one device in a <see cref="UsbSnapshot"/>. A plain
/// get/set class (not the <see cref="UsbDeviceNode"/> record) so System.Text.Json
/// round-trips it without constructor-binding friction.
/// </summary>
public sealed class UsbSnapshotEntry
{
    public string  InstanceId       { get; set; } = "";
    public string  VendorId         { get; set; } = "";
    public string  ProductId        { get; set; } = "";
    public string  Description      { get; set; } = "";
    public string  IdentityKind     { get; set; } = "";
    public string  UniqueToken      { get; set; } = "";
    public string  ParentHubName    { get; set; } = "";
    public uint?   PortNumber       { get; set; }
    public string? ContainerId      { get; set; }
    public bool    IsCompositeChild { get; set; }
}
