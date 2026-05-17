namespace HIDReorder.Models;

public sealed class HidHandleEvent
{
    public required string   DevicePath { get; init; }
    public required int      ProcessId  { get; init; }
    public required DateTime Timestamp  { get; init; }

    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff}  {DevicePath}";
}
