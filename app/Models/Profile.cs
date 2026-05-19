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
