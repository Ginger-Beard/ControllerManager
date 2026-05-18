using System.Text.Json;
using HIDReorder.Models;

namespace HIDReorder.Services;

public sealed class ProfileStore(string path)
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

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
        try { File.WriteAllText(path, JsonSerializer.Serialize(profiles, Opts)); }
        catch (Exception ex) { Logger.Write($"[ProfileStore] Save failed: {ex.Message}"); }
    }
}
