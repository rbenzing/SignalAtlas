using SignalAtlas.Decode;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — OUI→vendor lookup + LAA detection (SPEC §7.4, §8.4). A locally-administered /
/// randomized MAC (first-octet bit 0x02) has no meaningful OUI and must not be vendor-mapped
/// (test list: "OUI map (unknown→null)", "random/static MAC flag").
/// </summary>
public class DecodeOuiTests
{
    private static readonly OuiLookup Oui = new();

    // 1. LAA bit (0x02 of the first octet) detected as locally administered.
    [Theory]
    [InlineData("02:00:00:00:00:01")]  // bit 0x02 set
    [InlineData("B8:27:EB:12:34:56")]  // globally unique → not LAA
    public void IsLocallyAdministered_ReadsBit02OfFirstOctet(string mac)
    {
        var expected = mac.StartsWith("02", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, Oui.IsLocallyAdministered(mac));
    }

    // 2. Known OUI → vendor (accepts colon / hyphen / bare-hex, case-insensitive).
    [Theory]
    [InlineData("00:00:0C:AA:BB:CC", "Cisco")]
    [InlineData("3C-5A-B4-11-22-33", "Google")]
    [InlineData("b827eb123456", "Raspberry Pi Foundation")]
    public void Vendor_KnownOui_ReturnsVendor(string mac, string vendor)
    {
        Assert.Equal(vendor, Oui.Vendor(mac));
    }

    // 3. Randomized / locally-administered MAC → null vendor (no meaningful OUI).
    [Fact]
    public void Vendor_LocallyAdministeredMac_ReturnsNull()
    {
        // 0x02 first octet is LAA even though "02:00:0C" resembles a real OUI.
        Assert.Null(Oui.Vendor("02:00:0C:00:00:01"));
    }

    // 4. Unknown OUI (globally-unique, bit 0x02 clear) → null.
    [Fact]
    public void Vendor_UnknownOui_ReturnsNull()
    {
        Assert.False(Oui.IsLocallyAdministered("10:AA:BB:33:44:55"));
        Assert.Null(Oui.Vendor("10:AA:BB:33:44:55"));
    }
}
