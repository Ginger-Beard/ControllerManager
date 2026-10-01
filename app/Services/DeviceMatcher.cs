using System.Text.Json;
using System.Text.RegularExpressions;
using ControllerManager.Models;

namespace ControllerManager.Services;

public enum DeviceListKind { KeepEnabled, DisableThenRestore, KeepDisabled }

public enum MatchTier { Exact, Serial, Model, Name, Unresolved }

public sealed record RefMatch(
    DeviceRef Ref, DeviceListKind ListKind, MatchTier Tier,
    IReadOnlyList<HidDevice> Devices, bool Ambiguous);

/// <summary>
/// Identity-tolerant matching of profile DeviceRefs against live devices. Pure: no
/// logging, no I/O, no WPF, so it can be unit-tested in isolation.
///
/// Asymmetry rule: hiding a wanted device means no input, while leaving an extra device
/// visible costs at worst double input. So loose or ambiguous matches for KeepEnabled and
/// DisableThenRestore resolve toward VISIBLE and only for the session; KeepDisabled
/// accepts only Exact/Serial matches. Nothing looser than Serial is ever persisted.
/// </summary>
public static class DeviceMatcher
{
    private static readonly Regex VidRx = new(@"VID_([0-9A-F]{4})",  RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidRx = new(@"PID_([0-9A-F]{4})",  RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MiRx  = new(@"&MI_([0-9A-F]{2})",  RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DupSuffixRx = new(@"\s+#\d+$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions CloneOpts = new() { WriteIndented = true };

    // ── Serial normalisation ─────────────────────────────────────────────────────

    /// <summary>
    /// Canonical form of a USB serial. Some firmware reports the same serial with the
    /// byte order swapped inside each 32-bit word; the ordinal-smaller of the two forms
    /// is used so both spellings compare equal. Placeholder and port-derived values → "".
    /// </summary>
    public static string NormalizeSerial(string? s)
    {
        if (s is null) return "";
        s = s.Trim().ToUpperInvariant();

        if (s.Length < 4 || s.Contains('&')) return "";
        if (s.All(c => c == s[0])) return "";
        if (s is "0123456789" or "123456789" or "NONE") return "";

        if (s.Length >= 16 && s.Length % 8 == 0 && s.All(Uri.IsHexDigit))
        {
            var w = new char[s.Length];
            for (int word = 0; word < s.Length; word += 8)
                for (int b = 0; b < 4; b++)
                {
                    w[word + b * 2]     = s[word + (3 - b) * 2];
                    w[word + b * 2 + 1] = s[word + (3 - b) * 2 + 1];
                }
            var swapped = new string(w);
            return string.CompareOrdinal(s, swapped) <= 0 ? s : swapped;
        }

        return s;
    }

    // ── Identity accessors (fall back to parsing InstanceId for legacy refs) ─────

    private static string RefVid(DeviceRef r) =>
        r.VendorId ?? VidRx.Match(r.InstanceId).Groups[1].Value.ToUpperInvariant();
    private static string RefPid(DeviceRef r) =>
        r.ProductId ?? PidRx.Match(r.InstanceId).Groups[1].Value.ToUpperInvariant();
    private static string RefMi(DeviceRef r) =>
        r.InterfaceNumber ?? MiRx.Match(r.InstanceId).Groups[1].Value.ToUpperInvariant();
    private static string RefSerial(DeviceRef r) => r.Serial ?? "";

    private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string StripName(string? n) => DupSuffixRx.Replace((n ?? "").Trim(), "");

    public static bool NameEq(string? a, string? b)
    {
        var x = StripName(a);
        var y = StripName(b);
        return x.Length > 0 && y.Length > 0 && Eq(x, y);
    }

    /// <summary>True if the ref's product ID (parsed from the instance ID for legacy refs) equals the device's.</summary>
    public static bool PidEq(DeviceRef r, HidDevice d) => Eq(RefPid(r), d.ProductId);

    private static bool Conflict(DeviceRef r, HidDevice d)
    {
        var rs = RefSerial(r);
        return rs.Length > 0 && d.UsbSerial.Length > 0 && !Eq(rs, d.UsbSerial);
    }

    private static bool MiEq(DeviceRef r, HidDevice d)
    {
        var mi = RefMi(r);
        return mi.Length == 0 || Eq(mi, d.InterfaceNumber);
    }

    // ── Tier predicates ──────────────────────────────────────────────────────────

    /// <summary>True if the ref's instance ID is the device's primary or one of its child IDs.</summary>
    public static bool IsExact(DeviceRef r, HidDevice d) =>
        Eq(r.InstanceId, d.InstanceId) ||
        d.ChildInstanceIds.Any(c => Eq(c, r.InstanceId));

    private static bool IsSerial(DeviceRef r, HidDevice d)
    {
        var rs = RefSerial(r);
        var vid = RefVid(r);
        if (!(rs.Length > 0 && vid.Length > 0 && Eq(vid, d.VendorId) && Eq(rs, d.UsbSerial))) return false;

        // Some devices ship placeholder serials ("Nuvoton", "22201234"), so VID + serial alone
        // is not enough: require the same PID, the same name, or the same interface number.
        var mi = RefMi(r);
        return PidEq(r, d)
            || NameEq(r.FriendlyName, d.FriendlyName)
            || (mi.Length > 0 && d.InterfaceNumber.Length > 0 && Eq(mi, d.InterfaceNumber));
    }

    private static bool IsModel(DeviceRef r, HidDevice d)
    {
        var vid = RefVid(r);
        var pid = RefPid(r);
        return vid.Length > 0 && pid.Length > 0
            && Eq(vid, d.VendorId) && Eq(pid, d.ProductId)
            && MiEq(r, d) && !Conflict(r, d);
    }

    private static bool IsName(DeviceRef r, HidDevice d)
    {
        if (!NameEq(r.FriendlyName, d.FriendlyName)) return false;
        var vid = RefVid(r);
        if (vid.Length > 0 && !Eq(vid, d.VendorId)) return false;
        var mi = RefMi(r);
        if (mi.Length > 0 && d.InterfaceNumber.Length > 0 && !Eq(mi, d.InterfaceNumber)) return false;
        return !Conflict(r, d);
    }

    /// <summary>True if d would satisfy the Serial, Model or Name tier for r on its own.</summary>
    public static bool IsIdentityMatch(DeviceRef r, HidDevice d) =>
        IsSerial(r, d) || IsModel(r, d) || IsName(r, d);

    // ── Resolve ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<RefMatch> Resolve(Profile profile, IReadOnlyList<HidDevice> live)
    {
        var lists = new (DeviceListKind Kind, List<DeviceRef> Refs)[]
        {
            (DeviceListKind.KeepEnabled,        profile.KeepEnabled),
            (DeviceListKind.DisableThenRestore, profile.DisableThenRestore),
            (DeviceListKind.KeepDisabled,       profile.KeepDisabled),
        };

        var results = new Dictionary<DeviceRef, RefMatch>(ReferenceEqualityComparer.Instance);
        var claimed = new HashSet<HidDevice>(ReferenceEqualityComparer.Instance);

        // Pass A: exact instance-ID matches claim their device.
        foreach (var (kind, refs) in lists)
            foreach (var r in refs)
            {
                var d = live.FirstOrDefault(x => IsExact(r, x));
                if (d is null) continue;
                claimed.Add(d);
                results[r] = new RefMatch(r, kind, MatchTier.Exact, [d], false);
            }

        // Pass B: remaining refs against unclaimed devices; first tier with a candidate decides.
        foreach (var (kind, refs) in lists)
            foreach (var r in refs)
            {
                if (results.ContainsKey(r)) continue;

                var pool = live.Where(d => !claimed.Contains(d)).ToList();
                bool allowLoose = kind != DeviceListKind.KeepDisabled;

                var cands = pool.Where(d => IsSerial(r, d)).ToList();
                var tier = MatchTier.Serial;
                if (cands.Count > 1)
                {
                    var byMi = cands.Where(d => MiEq(r, d)).ToList();
                    if (byMi.Count > 0) cands = byMi;
                }
                if (cands.Count > 1)
                {
                    var byPid = cands.Where(d => Eq(RefPid(r), d.ProductId)).ToList();
                    if (byPid.Count > 0) cands = byPid;
                }

                if (cands.Count == 0 && allowLoose)
                {
                    tier  = MatchTier.Model;
                    cands = pool.Where(d => IsModel(r, d)).ToList();
                    if (cands.Count > 1)
                    {
                        var byName = cands.Where(d => NameEq(r.FriendlyName, d.FriendlyName)).ToList();
                        if (byName.Count > 0) cands = byName;
                    }
                }

                if (cands.Count == 0 && allowLoose)
                {
                    tier  = MatchTier.Name;
                    cands = pool.Where(d => IsName(r, d)).ToList();
                }

                if (cands.Count == 1)
                {
                    claimed.Add(cands[0]);
                    results[r] = new RefMatch(r, kind, tier, cands, false);
                }
                else if (cands.Count > 1 && allowLoose)
                {
                    results[r] = new RefMatch(r, kind, tier, cands, true);
                }
                else
                {
                    // None at any tier, or ambiguous for KeepDisabled (treated as unresolved).
                    results[r] = new RefMatch(r, kind, MatchTier.Unresolved, [], false);
                }
            }

        var ordered = new List<RefMatch>();
        foreach (var (_, refs) in lists)
            foreach (var r in refs)
                ordered.Add(results[r]);
        return ordered;
    }

    // ── Ref construction / backfill ──────────────────────────────────────────────

    public static DeviceRef CreateRef(HidDevice d)
    {
        var r = new DeviceRef
        {
            InstanceId          = d.InstanceId,
            DeviceInterfacePath = d.DeviceInterfacePath,
            FriendlyName        = d.FriendlyName,
        };
        Backfill(r, d);
        return r;
    }

    /// <summary>Sets the identity fields from d. Never touches FriendlyName and never
    /// replaces a non-empty serial with a different one. Returns true if anything changed.</summary>
    public static bool Backfill(DeviceRef r, HidDevice d)
    {
        bool changed = false;

        // An empty / 0000 VID or PID is the enumerator's fallback when a device can't be
        // opened. Never persist that over real identity.
        var vid = d.VendorId.ToUpperInvariant();
        var pid = d.ProductId.ToUpperInvariant();
        bool placeholder = vid.Length == 0 || pid.Length == 0 || vid == "0000" || pid == "0000";
        if (!placeholder)
        {
            if (r.VendorId != vid) { r.VendorId = vid; changed = true; }
            if (r.ProductId != pid) { r.ProductId = pid; changed = true; }
        }

        if (r.InterfaceNumber != d.InterfaceNumber) { r.InterfaceNumber = d.InterfaceNumber; changed = true; }

        if (string.IsNullOrEmpty(r.Serial) && r.Serial != d.UsbSerial)
        {
            r.Serial = d.UsbSerial;
            changed = true;
        }

        return changed;
    }

    // ── Session resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a deep copy of the profile whose refs are pointed at the live devices they
    /// matched, for this session only. The original profile is not mutated.
    /// </summary>
    public static Profile ResolveForSession(Profile profile, IReadOnlyList<HidDevice> live, List<string> warnings)
    {
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, CloneOpts), CloneOpts)!;
        var byRef = new Dictionary<DeviceRef, RefMatch>(ReferenceEqualityComparer.Instance);
        foreach (var m in Resolve(copy, live)) byRef[m.Ref] = m;

        List<DeviceRef> Apply(List<DeviceRef> refs)
        {
            var output = new List<DeviceRef>();
            foreach (var r in refs)
            {
                var m = byRef[r];

                if (m.Ambiguous)
                {
                    warnings.Add($"WARNING: '{r.FriendlyName}' matches {m.Devices.Count} devices - leaving all visible");
                    foreach (var d in m.Devices)
                    {
                        var c = new DeviceRef
                        {
                            InstanceId          = d.InstanceId,
                            DeviceInterfacePath = d.DeviceInterfacePath,
                            FriendlyName        = r.FriendlyName,
                            DelaySeconds        = r.DelaySeconds,
                            VendorId            = r.VendorId,
                            ProductId           = r.ProductId,
                            Serial              = r.Serial,
                            InterfaceNumber     = r.InterfaceNumber,
                        };
                        Backfill(c, d);
                        output.Add(c);
                    }
                    continue;
                }

                switch (m.Tier)
                {
                    case MatchTier.Exact:
                        Backfill(r, m.Devices[0]);
                        break;

                    case MatchTier.Serial:
                    case MatchTier.Model:
                    case MatchTier.Name:
                        var live0 = m.Devices[0];
                        warnings.Add($"'{r.FriendlyName}' found under a new ID ({m.Tier.ToString().ToLowerInvariant()}) - using {live0.InstanceId}");
                        r.InstanceId = live0.InstanceId;
                        if (r.DeviceInterfacePath.Length > 0) r.DeviceInterfacePath = live0.DeviceInterfacePath;
                        Backfill(r, live0);
                        break;

                    default:
                        if (m.ListKind == DeviceListKind.KeepEnabled)
                            warnings.Add($"WARNING: Always-visible '{r.FriendlyName}' is not connected - it will be left visible if it appears");
                        break;
                }
                output.Add(r);
            }
            return output;
        }

        copy.KeepEnabled        = Apply(copy.KeepEnabled);
        copy.DisableThenRestore = Apply(copy.DisableThenRestore);
        copy.KeepDisabled       = Apply(copy.KeepDisabled);
        return copy;
    }
}
