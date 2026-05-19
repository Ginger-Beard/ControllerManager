using System.Text.Json.Serialization;

namespace ControllerManager.Models;

public class Profile
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Profile";

    [JsonPropertyName("gameExecutablePath")]
    public string GameExecutablePath { get; set; } = "";

    [JsonPropertyName("gameExecutableName")]
    public string GameExecutableName { get; set; } = "";

    // Kept for reading old profiles only — no longer written by the editor.
    [JsonPropertyName("triggerMode")]
    public TriggerMode TriggerMode { get; set; } = TriggerMode.HandleWatcher;
    [JsonPropertyName("timerSeconds")]
    public int TimerSeconds { get; set; } = 0;

    /// <summary>
    /// Extra seconds to wait after HandleWatcher fires (or its 120s timeout elapses)
    /// before beginning the sequential device reveal. 0 = start immediately.
    /// On load, migrated from old TimerSeconds when TriggerMode was Timer.
    /// </summary>
    [JsonPropertyName("initialDelaySeconds")]
    public int InitialDelaySeconds { get; set; } = 0;

    [JsonPropertyName("hotkeyBinding")]
    public string HotkeyBinding { get; set; } = "F9";

    [JsonPropertyName("handleWatcherStepTimeoutMs")]
    public int HandleWatcherStepTimeoutMs { get; set; } = 1500;

    [JsonPropertyName("processWatcherEnabled")]
    public bool ProcessWatcherEnabled { get; set; } = true;

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

    [JsonPropertyName("delaySeconds")]
    public int DelaySeconds { get; set; } = 0;
}

public enum TriggerMode { HandleWatcher, Timer }
