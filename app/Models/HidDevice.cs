namespace HIDReorder.Models;

public sealed class HidDevice
{
    public required string InstanceId          { get; init; }
    public required string VendorId            { get; init; }
    public required string ProductId           { get; init; }
    public required string FriendlyName        { get; init; }
    public required string VendorLabel         { get; init; }
    public          string DeviceInterfacePath { get; init; } = "";
    public required bool   IsEnabled           { get; init; }

    public string VidPid      => $"VID_{VendorId}&PID_{ProductId}";
    public string VidPidShort => $"{VendorId}:{ProductId}";

    public override string ToString() => FriendlyName;
}
