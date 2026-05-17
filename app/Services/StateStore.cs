using System.Text.Json;
using System.Text.Json.Serialization;
using HIDReorder.Models;

namespace HIDReorder.Services;

/// <summary>
/// Persists which devices we disabled. Written before each disable, cleared after each
/// enable — so a crash leaves a record and we can recover on next launch.
/// </summary>
public sealed class StateStore(string path)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();

    private List<DisabledEntry> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<DisabledEntry>>(json, JsonOpts) ?? [];
        }
        catch { return []; }
    }

    private void Save(List<DisabledEntry> entries)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOpts)); }
        catch { }
    }

    public void RecordDisabled(HidDevice device)
        => RecordDisabledCore(device.InstanceId, device.FriendlyName);

    public void RecordDisabledRef(Models.DeviceRef device)
        => RecordDisabledCore(device.InstanceId, device.FriendlyName);

    private void RecordDisabledCore(string instanceId, string friendlyName)
    {
        lock (_lock)
        {
            var entries = Load();
            if (!entries.Any(e => e.InstanceId == instanceId))
            {
                entries.Add(new DisabledEntry
                {
                    InstanceId    = instanceId,
                    FriendlyName  = friendlyName,
                    DisabledAtUtc = DateTime.UtcNow,
                });
                Save(entries);
            }
        }
    }

    public void ClearEnabled(HidDevice device) => ClearEnabledById(device.InstanceId);

    public void ClearEnabledById(string instanceId)
    {
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => e.InstanceId == instanceId);
            Save(entries);
        }
    }

    public IReadOnlyList<DisabledEntry> GetDisabledByUs()
    {
        lock (_lock) { return Load(); }
    }

    public bool HasAny()
    {
        lock (_lock) { return Load().Count > 0; }
    }

    public void RecoverOnStartup()
    {
        var entries = GetDisabledByUs();
        if (entries.Count == 0) return;

        foreach (var entry in entries)
        {
            try { DeviceController.SetEnabledById(entry.InstanceId, true); }
            catch { /* best-effort recovery */ }
        }

        lock (_lock) { Save([]); }
    }

    public void RestoreAll()
    {
        var entries = GetDisabledByUs();
        foreach (var entry in entries)
        {
            try { DeviceController.SetEnabledById(entry.InstanceId, true); }
            catch { }
        }
        lock (_lock) { Save([]); }
    }
}

public sealed class DisabledEntry
{
    [JsonPropertyName("instanceId")]   public required string   InstanceId    { get; set; }
    [JsonPropertyName("friendlyName")] public required string   FriendlyName  { get; set; }
    [JsonPropertyName("disabledAt")]   public required DateTime DisabledAtUtc { get; set; }
}
