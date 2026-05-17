using System.Text.Json;

namespace HidReorder.Core;

public sealed class ProfileManager
{
    private readonly string _path;
    private List<Profile> _profiles;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ProfileManager(string path)
    {
        _path    = path;
        _profiles = Load();
    }

    public IReadOnlyList<Profile> All => _profiles.AsReadOnly();

    public Profile? Get(string name) =>
        _profiles.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Save(string name, IEnumerable<SimDevice> orderedDevices)
    {
        var patterns = orderedDevices.Select(d => d.VidPattern).ToList();
        var existing = Get(name);
        if (existing is not null)
            existing.DevicePatterns = patterns;
        else
            _profiles.Add(new Profile { Name = name, DevicePatterns = patterns });

        Persist();
    }

    public void Delete(string name)
    {
        _profiles.RemoveAll(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Persist();
    }

    private List<Profile> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(_path)) ?? []; }
        catch { return []; }
    }

    private void Persist() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(_profiles, JsonOpts));
}
