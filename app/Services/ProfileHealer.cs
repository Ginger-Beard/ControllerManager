using ControllerManager.Models;

namespace ControllerManager.Services;

public static class ProfileHealer
{
    /// <summary>
    /// Reconciles every DeviceRef in every profile against the live devices via
    /// <see cref="DeviceMatcher.Resolve"/>. Only Exact (backfill) and Serial (new instance
    /// ID) matches are written to the refs; Model/Name/Ambiguous/Unresolved are never
    /// persisted and are reported in <c>attention</c> where useful. <c>healed</c> lists
    /// refs whose instance ID was rewritten.
    /// </summary>
    public static (bool changed, List<string> healed, List<string> attention) HealAll(
        IList<Profile> profiles, IReadOnlyList<HidDevice> live)
    {
        bool changed = false;
        var healed    = new List<string>();
        var attention = new List<string>();

        foreach (var profile in profiles)
        {
            foreach (var m in DeviceMatcher.Resolve(profile, live))
            {
                var r = m.Ref;

                if (m.Ambiguous)
                {
                    attention.Add($"'{r.FriendlyName}' ({profile.Name}): {m.Devices.Count} identical devices");
                    continue;
                }

                switch (m.Tier)
                {
                    case MatchTier.Exact:
                        if (DeviceMatcher.Backfill(r, m.Devices[0])) changed = true;
                        break;

                    case MatchTier.Serial:
                        var d = m.Devices[0];
                        // Another ref in this profile already points at that device.
                        bool taken = AllRefs(profile).Any(o => !ReferenceEquals(o, r) &&
                            (o.InstanceId.Equals(d.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                             d.ChildInstanceIds.Contains(o.InstanceId, StringComparer.OrdinalIgnoreCase)));
                        if (taken) break;

                        // Placeholder serials can pair different products: only persist when the
                        // product is unchanged or the name still agrees.
                        if (!DeviceMatcher.PidEq(r, d) && !DeviceMatcher.NameEq(r.FriendlyName, d.FriendlyName))
                        {
                            attention.Add($"'{r.FriendlyName}' ({profile.Name}): matched by serial under a different product ID (not saved)");
                            break;
                        }

                        Logger.Write($"[ProfileHealer] '{profile.Name}' serial: {r.InstanceId} -> {d.InstanceId}");
                        r.InstanceId          = d.InstanceId;
                        r.DeviceInterfacePath = d.DeviceInterfacePath;
                        DeviceMatcher.Backfill(r, d);
                        healed.Add(r.FriendlyName);
                        changed = true;
                        break;

                    case MatchTier.Model:
                    case MatchTier.Name:
                        attention.Add($"'{r.FriendlyName}' ({profile.Name}): matched by {m.Tier.ToString().ToLowerInvariant()} this session, not saved");
                        break;
                }
            }
        }

        return (changed, healed, attention);
    }

    /// <summary>Single-profile wrapper; returns the names of refs whose instance ID was rewritten.</summary>
    public static List<string> Heal(Profile profile, IReadOnlyList<HidDevice> liveDevices) =>
        HealAll([profile], liveDevices).healed;

    private static IEnumerable<DeviceRef> AllRefs(Profile p) =>
        p.KeepEnabled.Concat(p.DisableThenRestore).Concat(p.KeepDisabled);
}
