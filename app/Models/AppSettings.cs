using System.Text.Json.Serialization;
using HIDReorder.Models;

namespace HIDReorder.Models;

public enum LogLevel { Off, Normal, Verbose }

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
}
