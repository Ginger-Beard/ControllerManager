using System.ComponentModel;

namespace ControllerManager.Models;

public sealed class HidDevice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string InstanceId          { get; init; }
    public required string VendorId            { get; init; }
    public required string ProductId           { get; init; }
    public          string FriendlyName        { get; set; } = "";
    public required string VendorLabel         { get; init; }
    public          string DeviceInterfacePath   { get; set; } = "";
    public          string AlternativeInstanceId { get; set; } = "";
    public          int    DuplicateIndex        { get; set; }
    public          int    AxisCount           { get; set; }
    public          int    ButtonCount         { get; set; }

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

    public string VidPid       => $"VID_{VendorId}&PID_{ProductId}";
    public string VidPidShort  => $"{VendorId}:{ProductId}";
    public string InputSummary => AxisCount > 0 || ButtonCount > 0
        ? $"{AxisCount}ax {ButtonCount}btn"
        : "—";

    public override string ToString() => FriendlyName;
}
