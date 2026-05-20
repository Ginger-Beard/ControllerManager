using System.Text.Json;
using ControllerManager.Models;

namespace ControllerManager.Services;

public sealed class ProfileStore(string path)
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>
    /// Fires after Save() writes successfully. Subscribers should re-Load their
    /// in-memory copy. Used to keep Dashboard's profile dropdown in sync with
    /// Games-tab add/delete operations without forcing the two VMs to share a list.
    /// </summary>
    public event Action? Changed;

    public List<Profile> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(path), Opts) ?? [];
        }
        catch (Exception ex) { Logger.Write($"[ProfileStore] Load failed: {ex.Message}"); return []; }
    }

    public void Save(List<Profile> profiles)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(profiles, Opts));
            try { Changed?.Invoke(); } catch (Exception ex) { Logger.WriteException("ProfileStore.Changed", ex); }
        }
        catch (Exception ex) { Logger.Write($"[ProfileStore] Save failed: {ex.Message}"); }
    }
}
