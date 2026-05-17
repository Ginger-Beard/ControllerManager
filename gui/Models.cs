namespace HidReorder;

public sealed class SimDevice
{
    public required string InstanceId  { get; init; }
    public required string VendorId    { get; init; }  // "346E"
    public required string ProductId   { get; init; }  // "0016"
    public required string DisplayName { get; init; }  // "MOZA Racing [VID_346E&PID_0016]"
    public required bool   IsEnabled   { get; init; }

    // Pattern used for profile matching and PowerShell targeting
    public string VidPattern  => $"VID_{VendorId}";
    public string VidPidLabel => $"VID_{VendorId}&PID_{ProductId}";

    public override string ToString() => DisplayName;
}

public sealed class Profile
{
    public required string       Name           { get; set; }
    // VID_XXXX patterns in slot order; index 0 = slot #1
    public required List<string> DevicePatterns { get; set; }
}

// ── Drift monitor data ─────────────────────────────────────────────────────────

public sealed record AxisReading(string Name, int Percent, bool IsDrifting);

public sealed record DeviceReading(string Label, AxisReading[] Axes);
