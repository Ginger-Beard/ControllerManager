using System.Text.Json.Serialization;
using ControllerManager.Models;

namespace ControllerManager.Models;

public enum LogLevel { Off, Normal, Verbose }

/// <summary>
/// Which device-hiding backend to use.
/// Auto = HidHide when installed, pnputil otherwise.
/// </summary>
public enum DeviceHidingBackend { Auto, HidHide, Pnputil }

public sealed class AppSettings
{
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("processWatcherEnabled")]
    public bool ProcessWatcherEnabled { get; set; } = true;

    [JsonPropertyName("logLevel")]
    public LogLevel LogLevel { get; set; } = LogLevel.Normal;

    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; }

    [JsonPropertyName("deviceHidingBackend")]
    public DeviceHidingBackend DeviceHidingBackend { get; set; } = DeviceHidingBackend.Auto;
}
