using System.Text.Json.Nodes;

namespace HIDReorder.Services;

public sealed class VidResolver
{
    private readonly Dictionary<string, string> _curated =
        new(StringComparer.OrdinalIgnoreCase);

    public VidResolver(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return;
        try
        {
            var root    = JsonNode.Parse(File.ReadAllText(jsonPath));
            var vendors = root?["vendors"]?.AsObject();
            if (vendors is null) return;

            foreach (var kv in vendors)
            {
                var parts = kv.Key.Split('_');
                string key = parts.Length == 2 && parts[0].Length <= 4 && parts[1].Length <= 4
                    ? $"{parts[0].ToUpperInvariant().PadLeft(4, '0')}_{parts[1].ToUpperInvariant().PadLeft(4, '0')}"
                    : parts[0].ToUpperInvariant().PadLeft(4, '0');
                _curated.TryAdd(key, kv.Value?.GetValue<string>() ?? kv.Key);
            }
        }
        catch { }
    }

    public bool IsKnownSimVid(string vid) =>
        _curated.ContainsKey(vid.ToUpperInvariant().PadLeft(4, '0'));

    public string Resolve(string vid, string? pid = null)
    {
        var vidKey = vid.ToUpperInvariant().PadLeft(4, '0');
        if (pid is not null)
        {
            var vidPidKey = $"{vidKey}_{pid.ToUpperInvariant().PadLeft(4, '0')}";
            if (_curated.TryGetValue(vidPidKey, out var specific)) return specific;
        }
        return _curated.TryGetValue(vidKey, out var name) ? name : $"Unknown (VID_{vidKey})";
    }
}
