using System.Text.RegularExpressions;
using HIDReorder.Models;

namespace HIDReorder.Services;

public static class ProfileHealer
{
    private static readonly Regex VidRx =
        new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidRx =
        new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// For each DeviceRef whose InstanceId is no longer present in liveDevices, searches
    /// for a replacement by VID+PID with FriendlyName as the tiebreaker. Mutates the
    /// DeviceRef in place and returns a description of each healed entry so the caller
    /// can surface it to the user.
    /// </summary>
    public static List<string> Heal(Profile profile, IReadOnlyList<HidDevice> liveDevices)
    {
        var healed  = new List<string>();
        var liveIds = liveDevices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);

        // Only heal lists that drive device toggling; KeepEnabled is informational only.
        var allRefs = profile.DisableThenRestore
            .Concat(profile.KeepDisabled);

        foreach (var r in allRefs)
        {
            if (liveIds.ContainsKey(r.InstanceId)) continue;

            var vid = VidRx.Match(r.InstanceId).Groups[1].Value.ToUpperInvariant();
            var pid = PidRx.Match(r.InstanceId).Groups[1].Value.ToUpperInvariant();
            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid)) continue;

            var candidates = liveDevices
                .Where(d => d.VendorId.Equals(vid, StringComparison.OrdinalIgnoreCase)
                         && d.ProductId.Equals(pid, StringComparison.OrdinalIgnoreCase))
                .ToList();

            HidDevice? match = candidates.Count switch
            {
                0 => null,
                1 => candidates[0],
                _ => candidates.FirstOrDefault(d =>
                         d.FriendlyName.Equals(r.FriendlyName, StringComparison.OrdinalIgnoreCase))
                     ?? candidates.FirstOrDefault(d =>
                         d.FriendlyName.Contains(r.FriendlyName, StringComparison.OrdinalIgnoreCase))
            };

            if (match is null) continue;

            Logger.Write($"[ProfileHealer] Healed '{r.FriendlyName}': {r.InstanceId} → {match.InstanceId}");
            r.InstanceId          = match.InstanceId;
            r.DeviceInterfacePath = match.DeviceInterfacePath;
            healed.Add(r.FriendlyName);
        }

        return healed;
    }
}
