using System.Text.Json.Serialization;

namespace ControllerManager.Models;

public class Profile
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// JSON schema version, used to migrate per-device DelaySeconds semantics:
    ///   0 — legacy: InitialDelaySeconds + DelaySeconds meant "wait AFTER reveal"
    ///   1 — DelaySeconds meant "wait BEFORE reveal" (relative to previous reveal)
    ///   2 — current: DelaySeconds is the ABSOLUTE time from start of reveal phase
    ///       when this device should be revealed. List order is reveal order;
    ///       a device with a smaller time than the previous one reveals immediately
    ///       after the previous (clamped). Matches users' intuition that the
    ///       number is "the moment this device appears."
    /// Migration runs in ProfileEditorViewModel.LoadProfile; ToProfile always
    /// writes the current schema.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 0;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Profile";

    [JsonPropertyName("gameExecutablePath")]
    public string GameExecutablePath { get; set; } = "";

    [JsonPropertyName("gameExecutableName")]
    public string GameExecutableName { get; set; } = "";

    // Legacy: kept for reading old profiles. Not exposed in UI.
    // TimerSeconds migrates into InitialDelaySeconds when TriggerMode was Timer.
    [JsonPropertyName("triggerMode")]
    public TriggerMode TriggerMode { get; set; } = TriggerMode.HandleWatcher;
    [JsonPropertyName("timerSeconds")]
    public int TimerSeconds { get; set; } = 0;

    /// <summary>
    /// Seconds to wait after the game process starts before beginning the sequential
    /// device reveal. Default 5s gives FFB-sensitive games (Forza Horizon, etc.) time
    /// to commit slot #1 to the always-visible wheel before pedals/shifter arrive.
    /// Set to 0 for hot-plug-aware games that don't care about slot assignment timing.
    /// </summary>
    [JsonPropertyName("initialDelaySeconds")]
    public int InitialDelaySeconds { get; set; } = 5;

    [JsonPropertyName("processWatcherEnabled")]
    public bool ProcessWatcherEnabled { get; set; } = true;

    /// <summary>
    /// How to time the start of the reveal phase.
    /// • <see cref="AcquisitionTrigger.Timer"/> (default): wait until the per-device
    ///   T+Xs configured on each Reveal-After-Start row.
    /// • <see cref="AcquisitionTrigger.FirstDeviceOpened"/>: subscribe to kernel
    ///   ETW and start the reveal sequence the moment the game opens its first
    ///   HID device file. Eliminates per-game timing tuning for games with a hard
    ///   device-detection window. Falls back to the timer behavior if ETW fails
    ///   or the signal doesn't arrive within 30 seconds.
    /// </summary>
    [JsonPropertyName("acquisitionTrigger")]
    public AcquisitionTrigger AcquisitionTrigger { get; set; } = AcquisitionTrigger.Timer;

    [JsonPropertyName("keepEnabled")]
    public List<DeviceRef> KeepEnabled { get; set; } = [];

    // Ordered — re-enable happens in this order after game acquires devices
    [JsonPropertyName("disableThenRestore")]
    public List<DeviceRef> DisableThenRestore { get; set; } = [];

    // Disabled for the entire session — re-enabled only on game exit
    [JsonPropertyName("keepDisabled")]
    public List<DeviceRef> KeepDisabled { get; set; } = [];
}

public class DeviceRef
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("deviceInterfacePath")]
    public string DeviceInterfacePath { get; set; } = "";

    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    /// <summary>
    /// Absolute reveal time in seconds from start of the reveal phase, with
    /// sub-second precision. Old integer values still parse (JSON numbers
    /// are double-typed).
    /// </summary>
    [JsonPropertyName("delaySeconds")]
    public double DelaySeconds { get; set; } = 0;
}

public enum TriggerMode { HandleWatcher, Timer }

public enum AcquisitionTrigger
{
    /// <summary>Wait the per-device T+Xs times configured in the profile.</summary>
    Timer,
    /// <summary>Wait until the game opens its first HID device, then reveal all at once.</summary>
    FirstDeviceOpened,
}
