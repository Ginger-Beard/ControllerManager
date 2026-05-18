using System.ComponentModel;

namespace HIDReorder.Models;

public sealed class HidDevice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string InstanceId          { get; init; }
    public required string VendorId            { get; init; }
    public required string ProductId           { get; init; }
    public required string FriendlyName        { get; init; }
    public required string VendorLabel         { get; init; }
    public          string DeviceInterfacePath { get; init; } = "";

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public string VidPid      => $"VID_{VendorId}&PID_{ProductId}";
    public string VidPidShort => $"{VendorId}:{ProductId}";

    public override string ToString() => FriendlyName;
}
