using System.Text.Json.Serialization;
using ControllerManager.Models;

namespace ControllerManager.Models;

// Order is meaningful — Logger uses `_level >= LogLevel.X` comparisons to gate
// writes. Higher = chattier.
//   Off      — nothing
//   Normal   — orchestrator phase changes, errors
//   Verbose  — per-device events, broker/PID details
//   Debug    — firehose: HIDCLASS Rundown payloads, etc.
public enum LogLevel { Off, Normal, Verbose, Debug }

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
