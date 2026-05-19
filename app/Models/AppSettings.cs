using System.Text.Json.Serialization;
using ControllerManager.Models;

namespace ControllerManager.Models;

public enum LogLevel { Off, Normal, Verbose }

public sealed class AppSettings
{
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("logLevel")]
    public LogLevel LogLevel { get; set; } = LogLevel.Normal;

    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; }
}
