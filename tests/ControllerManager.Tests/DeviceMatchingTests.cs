using System.Text.Json;
using ControllerManager.Models;
using ControllerManager.Services;
using Xunit;

namespace ControllerManager.Tests;

public class DeviceMatchingTests
{
    private const string RawA = "22003C000C51303335333433";
    private const string RawB = "003C00223330510C33343335";
    private const string Canon = "003C00223330510C33343335";

    private const string MozaName = "MOZA R12 Base";

    private static HidDevice Dev(string instanceId, string vid, string pid, string name,
        string serial = "", string mi = "", params string[] children) => new()
    {
        InstanceId = instanceId,
        VendorId = vid,
        ProductId = pid,
        VendorLabel = "",
        FriendlyName = name,
        UsbSerial = serial,
        InterfaceNumber = mi,
        ChildInstanceIds = children.Length > 0 ? children : [instanceId],
        DeviceInterfacePath = @"\\?\" + instanceId,
    };

    private static DeviceRef Ref(string instanceId, string name, string? vid = null, string? pid = null,
        string? serial = null, string? mi = null) => new()
    {
        InstanceId = instanceId,
        FriendlyName = name,
        VendorId = vid,
        ProductId = pid,
        Serial = serial,
        InterfaceNumber = mi,
    };

    private static DeviceRef MozaBackfilled(string instanceId) =>
        Ref(instanceId, MozaName, "346E", "0006", Canon, "02");

    private static Profile P(IEnumerable<DeviceRef>? keep = null, IEnumerable<DeviceRef>? dtr = null,
        IEnumerable<DeviceRef>? disabled = null) => new()
    {
        Name = "Test",
        KeepEnabled = keep?.ToList() ?? [],
        DisableThenRestore = dtr?.ToList() ?? [],
        KeepDisabled = disabled?.ToList() ?? [],
    };

    private const string Moza0006Mi02 = @"HID\VID_346E&PID_0006&MI_02\F&2C3E1905&0&0000";
    private const string Moza0016Mi02 = @"HID\VID_346E&PID_0016&MI_02\F&25C404D4&1&0000";

    // 1
    [Fact]
    public void NormalizeSerial_BothMozaByteOrders_CollapseToSameCanonicalForm_AndPlaceholdersAndPortTokensAreEmpty()
    {
        Assert.Equal(Canon, DeviceMatcher.NormalizeSerial(RawA));
        Assert.Equal(Canon, DeviceMatcher.NormalizeSerial(RawB));
        Assert.Equal("", DeviceMatcher.NormalizeSerial("000000000000"));
        Assert.Equal("A00SA4152LXOTP", DeviceMatcher.NormalizeSerial("A00SA4152LXOTP"));
        Assert.NotEqual(DeviceMatcher.NormalizeSerial("A00SA4152LXOTP"), DeviceMatcher.NormalizeSerial("A00SA4152NC0N0"));
        Assert.Equal("A00SA4152NC0N0", DeviceMatcher.NormalizeSerial("A00SA4152NC0N0"));
        Assert.Equal("", DeviceMatcher.NormalizeSerial("7&1A2B3C4D&0&2"));
    }

    // 2
    [Fact]
    public void LegacyRef_WithChildIdMatchingLiveDevice_ResolvesExact_AndHealAllBackfillsIdentityFields()
    {
        var r = Ref(@"HID\VID_346E&PID_0006&MI_02\f&2c3e1905&0&0000", MozaName);
        var profile = P(keep: [r]);
        var live = new[] { Dev(Moza0006Mi02, "346E", "0006", MozaName, DeviceMatcher.NormalizeSerial(RawA), "02") };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.Equal(MatchTier.Exact, m.Tier);

        var (changed, healed, _) = ProfileHealer.HealAll([profile], live);
        Assert.True(changed);
        Assert.Empty(healed);
        Assert.Equal(Canon, r.Serial);
        Assert.Equal("346E", r.VendorId);
        Assert.Equal("0006", r.ProductId);
        Assert.Equal("02", r.InterfaceNumber);
    }

    // 3
    [Fact]
    public void BackfilledMozaRef_LiveDeviceIsPid0016WithSameSerial_ResolvesSerial_AndHealAllRewritesInstanceId()
    {
        var r = MozaBackfilled(Moza0006Mi02);
        var profile = P(keep: [r]);
        var live = new[] { Dev(Moza0016Mi02, "346E", "0016", MozaName, Canon, "02") };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.Equal(MatchTier.Serial, m.Tier);
        Assert.False(m.Ambiguous);

        var (changed, healed, _) = ProfileHealer.HealAll([profile], live);
        Assert.True(changed);
        Assert.Equal(Moza0016Mi02, r.InstanceId);
        Assert.Equal("0016", r.ProductId);
        Assert.Contains(MozaName, healed);
    }

    // 4
    [Fact]
    public void BackfilledMozaRef_LiveDeviceHasByteSwappedRawSerial_ResolvesSerial()
    {
        var r = Ref(Moza0016Mi02, MozaName, "346E", "0016", DeviceMatcher.NormalizeSerial(RawA), "02");
        var profile = P(keep: [r]);
        var live = new[] { Dev(Moza0006Mi02, "346E", "0006", MozaName, DeviceMatcher.NormalizeSerial(RawB), "02") };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.Equal(MatchTier.Serial, m.Tier);
    }

    // 5
    [Fact]
    public void LegacyMozaRefWithoutSerial_AbsentButSameNamePid0016Present_ResolvesName_SessionOnly_NotPersisted_ListedInAttention()
    {
        var r = Ref(Moza0006Mi02, MozaName);
        var profile = P(keep: [r]);
        var live = new[] { Dev(Moza0016Mi02, "346E", "0016", MozaName, Canon, "02") };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.Equal(MatchTier.Name, m.Tier);

        var warnings = new List<string>();
        var session = DeviceMatcher.ResolveForSession(profile, live, warnings);
        Assert.Equal(Moza0016Mi02, Assert.Single(session.KeepEnabled).InstanceId);
        Assert.Single(warnings);
        Assert.Contains("new ID", warnings[0]);
        Assert.Equal(Moza0006Mi02, r.InstanceId); // original not mutated

        var (changed, healed, attention) = ProfileHealer.HealAll([profile], live);
        Assert.False(changed);
        Assert.Empty(healed);
        Assert.Equal(Moza0006Mi02, r.InstanceId);
        Assert.Null(r.Serial);
        Assert.Single(attention);
    }

    // 6
    [Fact]
    public void KeepRefMozaBase_OnlyMozaPedalsPresent_IsUnresolved()
    {
        var r = MozaBackfilled(Moza0006Mi02);
        var live = new[] { Dev(@"HID\VID_346E&PID_0003&MI_00\F&AAAA&0&0000", "346E", "0003", "MOZA SR-P Pedals",
            DeviceMatcher.NormalizeSerial("PEDALSERIAL1234"), "00") };

        var m = Assert.Single(DeviceMatcher.Resolve(P(keep: [r]), live));
        Assert.Equal(MatchTier.Unresolved, m.Tier);
    }

    // 7
    [Fact]
    public void KeepRef0483_0531_LiveDevice0483_5001WithDifferentName_IsUnresolved()
    {
        var r = Ref(@"HID\VID_0483&PID_0531\7&1&0&0000", "Some Shifter", "0483", "0531");
        var live = new[] { Dev(@"HID\VID_0483&PID_5001\8&2&0&0000", "0483", "5001", "Other Gadget") };

        var m = Assert.Single(DeviceMatcher.Resolve(P(keep: [r]), live));
        Assert.Equal(MatchTier.Unresolved, m.Tier);
    }

    // 8
    [Fact]
    public void SameModelDifferentSerial_KeepRefHasS1_OnlyS2Present_IsUnresolvedDueToConflict()
    {
        var r = Ref(@"HID\VID_1234&PID_0001&MI_00\A&1&0&0000", "Pedals", "1234", "0001", "SERIAL-S1", "00");
        var live = new[] { Dev(@"HID\VID_1234&PID_0001&MI_00\B&2&0&0000", "1234", "0001", "Pedals", "SERIAL-S2", "00") };

        var m = Assert.Single(DeviceMatcher.Resolve(P(keep: [r]), live));
        Assert.Equal(MatchTier.Unresolved, m.Tier);
    }

    // 9
    [Fact]
    public void SerialLessDeviceWithChangedPathToken_SameVidPidMi_ResolvesModel_VisibleForSessionOnly_NotPersisted()
    {
        var r = Ref(@"HID\VID_1234&PID_0001&MI_00\A&111&0&0000", "Shifter");
        var profile = P(keep: [r]);
        var newId = @"HID\VID_1234&PID_0001&MI_00\B&222&0&0000";
        var live = new[] { Dev(newId, "1234", "0001", "Shifter", "", "00") };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.Equal(MatchTier.Model, m.Tier);

        var session = DeviceMatcher.ResolveForSession(profile, live, []);
        Assert.Equal(newId, Assert.Single(session.KeepEnabled).InstanceId);

        var (changed, healed, attention) = ProfileHealer.HealAll([profile], live);
        Assert.False(changed);
        Assert.Empty(healed);
        Assert.Single(attention);
        Assert.Equal(@"HID\VID_1234&PID_0001&MI_00\A&111&0&0000", r.InstanceId);
    }

    private static (HidDevice a, HidDevice b) Twins() => (
        Dev(@"HID\VID_1234&PID_0001&MI_00\B&111&0&0000", "1234", "0001", "Twin Pedals", "", "00"),
        Dev(@"HID\VID_1234&PID_0001&MI_00\B&222&0&0000", "1234", "0001", "Twin Pedals", "", "00"));

    // 10
    [Fact]
    public void TwoIdenticalSerialLessDevices_OneStaleKeepRef_IsAmbiguous_SessionYieldsTwoRefsWithWarning_NotPersisted()
    {
        var r = Ref(@"HID\VID_1234&PID_0001&MI_00\A&999&0&0000", "Twin Pedals");
        var profile = P(keep: [r]);
        var (a, b) = Twins();
        var live = new[] { a, b };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.True(m.Ambiguous);
        Assert.Equal(2, m.Devices.Count);

        var warnings = new List<string>();
        var session = DeviceMatcher.ResolveForSession(profile, live, warnings);
        Assert.Equal(2, session.KeepEnabled.Count);
        Assert.Contains(session.KeepEnabled, x => x.InstanceId == a.InstanceId);
        Assert.Contains(session.KeepEnabled, x => x.InstanceId == b.InstanceId);
        Assert.Contains(warnings, w => w.Contains("matches 2 devices"));

        var (changed, healed, attention) = ProfileHealer.HealAll([profile], live);
        Assert.False(changed);
        Assert.Empty(healed);
        Assert.Single(attention);
        Assert.Equal(@"HID\VID_1234&PID_0001&MI_00\A&999&0&0000", r.InstanceId);
    }

    // 11
    [Fact]
    public void TwoIdenticalDevices_TwinClaimedExactByDtrRef_StaleKeepRefResolvesUniquelyAtModel()
    {
        var (a, b) = Twins();
        var keep = Ref(@"HID\VID_1234&PID_0001&MI_00\A&999&0&0000", "Twin Pedals");
        var dtr = Ref(a.InstanceId, "Twin Pedals");
        var profile = P(keep: [keep], dtr: [dtr]);

        var results = DeviceMatcher.Resolve(profile, [a, b]);
        var km = results.Single(x => ReferenceEquals(x.Ref, keep));
        Assert.Equal(MatchTier.Model, km.Tier);
        Assert.False(km.Ambiguous);
        Assert.Same(b, Assert.Single(km.Devices));
        Assert.Equal(MatchTier.Exact, results.Single(x => ReferenceEquals(x.Ref, dtr)).Tier);
    }

    // 12
    [Fact]
    public void VJoyStyleRef_PresentIsExact_AbsentIsUnresolvedAndUnchanged()
    {
        const string id = @"HID\HIDCLASS\1&2A3B4C&0&0000";
        var r = Ref(id, "vJoy Device");
        var profile = P(keep: [r]);

        var present = new[] { Dev(id, "1234", "BEAD", "vJoy Device") };
        Assert.Equal(MatchTier.Exact, Assert.Single(DeviceMatcher.Resolve(profile, present)).Tier);

        var absent = new[] { Dev(Moza0006Mi02, "346E", "0006", MozaName, Canon, "02") };
        Assert.Equal(MatchTier.Unresolved, Assert.Single(DeviceMatcher.Resolve(profile, absent)).Tier);

        var (changed, healed, _) = ProfileHealer.HealAll([profile], absent);
        Assert.False(changed);
        Assert.Empty(healed);
        Assert.Equal(id, r.InstanceId);
        Assert.Null(r.Serial);
    }

    // 13
    [Fact]
    public void KeepDisabledRef_WithOnlyModelTierCandidate_IsUnresolved_AndNothingHealed()
    {
        var r = Ref(@"HID\VID_1234&PID_0001&MI_00\A&111&0&0000", "Shifter");
        var profile = P(disabled: [r]);
        var live = new[] { Dev(@"HID\VID_1234&PID_0001&MI_00\B&222&0&0000", "1234", "0001", "Shifter", "", "00") };

        var m = Assert.Single(DeviceMatcher.Resolve(profile, live));
        Assert.Equal(MatchTier.Unresolved, m.Tier);

        var (changed, healed, attention) = ProfileHealer.HealAll([profile], live);
        Assert.False(changed);
        Assert.Empty(healed);
        Assert.Empty(attention);
        Assert.Equal(@"HID\VID_1234&PID_0001&MI_00\A&111&0&0000", r.InstanceId);
    }

    // 14
    [Fact]
    public void CompositeDevice_TwoInterfacesShareSerial_RefWithMi02AndStalePath_PicksTheMi02Device()
    {
        var r = MozaBackfilled(@"HID\VID_346E&PID_0006&MI_02\STALE&0&0000");
        var mi00 = Dev(@"HID\VID_346E&PID_0006&MI_00\F&1&0&0000", "346E", "0006", MozaName, Canon, "00");
        var mi02 = Dev(@"HID\VID_346E&PID_0006&MI_02\F&2&0&0000", "346E", "0006", MozaName, Canon, "02");

        var m = Assert.Single(DeviceMatcher.Resolve(P(keep: [r]), [mi00, mi02]));
        Assert.Equal(MatchTier.Serial, m.Tier);
        Assert.False(m.Ambiguous);
        Assert.Same(mi02, Assert.Single(m.Devices));
    }

    // 15
    [Fact]
    public void IsIdentityMatch_AbsentMozaKeepRef_TrueForArrivingPid0016SameSerial_FalseForMozaPedals()
    {
        var r = MozaBackfilled(Moza0006Mi02);
        var arriving = Dev(Moza0016Mi02, "346E", "0016", MozaName, Canon, "02");
        var pedals = Dev(@"HID\VID_346E&PID_0003&MI_00\F&AAAA&0&0000", "346E", "0003", "MOZA SR-P Pedals",
            DeviceMatcher.NormalizeSerial("PEDALSERIAL1234"), "00");

        Assert.True(DeviceMatcher.IsIdentityMatch(r, arriving));
        Assert.False(DeviceMatcher.IsIdentityMatch(r, pedals));
    }

    // 16
    [Fact]
    public void DeviceRef_NullIdentityFieldsAreOmittedFromJson_AndPopulatedFieldsRoundTrip()
    {
        var bare = new DeviceRef { InstanceId = "X", FriendlyName = "Y" };
        var json = JsonSerializer.Serialize(bare);
        foreach (var key in new[] { "vendorId", "productId", "serial", "interfaceNumber" })
            Assert.DoesNotContain($"\"{key}\"", json);

        var full = MozaBackfilled(Moza0006Mi02);
        var back = JsonSerializer.Deserialize<DeviceRef>(JsonSerializer.Serialize(full))!;
        Assert.Equal("346E", back.VendorId);
        Assert.Equal("0006", back.ProductId);
        Assert.Equal(Canon, back.Serial);
        Assert.Equal("02", back.InterfaceNumber);

        var legacy = JsonSerializer.Deserialize<DeviceRef>("{\"instanceId\":\"X\",\"friendlyName\":\"Y\"}")!;
        Assert.Null(legacy.VendorId);
        Assert.Null(legacy.Serial);
    }

    // 17
    [Fact]
    public void PlaceholderSerial_SameVidDifferentProductAndName_NoInterfaceNumbers_IsUnresolved_AndNotIdentityMatch()
    {
        var r = Ref(@"HID\VID_0416&PID_5020\A&1&0&0000", "Button Box", "0416", "5020", "22201234");
        var other = Dev(@"HID\VID_0416&PID_C300\B&2&0&0000", "0416", "C300", "Desk Lamp Controller", "22201234");

        var m = Assert.Single(DeviceMatcher.Resolve(P(keep: [r]), [other]));
        Assert.Equal(MatchTier.Unresolved, m.Tier);
        Assert.False(DeviceMatcher.IsIdentityMatch(r, other));
    }

    // 18
    [Fact]
    public void Serial_DifferentPidSameInterfaceNumber_ResolvesSerial_ButHealAllDoesNotPersist()
    {
        var oldId = @"HID\VID_0416&PID_5020&MI_00\A&1&0&0000";
        var r = Ref(oldId, "Button Box", "0416", "5020", "22201234", "00");
        var profile = P(keep: [r]);
        var other = Dev(@"HID\VID_0416&PID_C300&MI_00\B&2&0&0000", "0416", "C300", "Desk Lamp Controller", "22201234", "00");

        var m = Assert.Single(DeviceMatcher.Resolve(profile, [other]));
        Assert.Equal(MatchTier.Serial, m.Tier);

        var (_, healed, attention) = ProfileHealer.HealAll([profile], [other]);
        Assert.Equal(oldId, r.InstanceId);
        Assert.Empty(healed);
        Assert.Single(attention);
    }

    // 19
    [Fact]
    public void Backfill_PlaceholderVidPidFromLiveDevice_LeavesRefVendorAndProductUntouched()
    {
        var r = Ref(Moza0006Mi02, MozaName, "346E", "0006", Canon, "02");
        var live = Dev(Moza0006Mi02, "0000", "0000", MozaName, Canon, "02");

        DeviceMatcher.Backfill(r, live);

        Assert.Equal("346E", r.VendorId);
        Assert.Equal("0006", r.ProductId);
    }
}
