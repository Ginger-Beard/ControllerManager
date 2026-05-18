using System.Text.Json.Serialization;
using HIDReorder.Models;

namespace HIDReorder.Models;

public sealed class AppSettings
{
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("processWatcherEnabled")]
    public bool ProcessWatcherEnabled { get; set; } = true;

    [JsonPropertyName("defaultTriggerMode")]
    public TriggerMode DefaultTriggerMode { get; set; } = TriggerMode.HandleWatcher;
}
