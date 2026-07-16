using SignalAtlas.Domain;

namespace SignalAtlas.Decode;

/// <summary>
/// OUI→vendor lookup over a small built-in table (SPEC §7.4). A locally-administered /
/// randomized MAC (first-octet bit 0x02 set) has no meaningful OUI, so <see cref="Vendor"/>
/// returns null for it (SPEC §8.4 LAA check). Accepts colon / hyphen / bare-hex MACs.
/// </summary>
public sealed class OuiLookup : IOuiLookup
{
    // A handful of real IEEE-registered prefixes (SPEC §7.4 example set). OUI key = first
    // three octets as uppercase bare hex.
    private static readonly IReadOnlyDictionary<string, string> Vendors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["00000C"] = "Cisco",
            ["3C5AB4"] = "Google",
            ["B827EB"] = "Raspberry Pi Foundation",
            ["FCFBFB"] = "Cisco",
            ["DCA632"] = "Raspberry Pi Foundation",
            ["F4F5E8"] = "Google",
        };

    public bool IsLocallyAdministered(string mac)
    {
        var octets = Normalize(mac);
        // LAA/randomized MAC: bit 0x02 of the first octet is set (SPEC §8.4 LAA check).
        return octets.Length >= 1 && (octets[0] & 0x02) != 0;
    }

    public string? Vendor(string mac)
    {
        // A locally-administered / randomized MAC has no meaningful OUI — never vendor-map it.
        if (IsLocallyAdministered(mac))
            return null;

        var octets = Normalize(mac);
        if (octets.Length < 3)
            return null;

        var oui = $"{octets[0]:X2}{octets[1]:X2}{octets[2]:X2}";
        return Vendors.TryGetValue(oui, out var vendor) ? vendor : null;
    }

    /// <summary>Parses a colon / hyphen / bare-hex MAC (case-insensitive) into its octets.</summary>
    private static byte[] Normalize(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return Array.Empty<byte>();

        var hex = mac.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
        if (hex.Length % 2 != 0)
            return Array.Empty<byte>();

        var octets = new byte[hex.Length / 2];
        for (var i = 0; i < octets.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out octets[i]))
                return Array.Empty<byte>();
        }

        return octets;
    }
}
