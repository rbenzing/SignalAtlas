using SignalAtlas.Domain;

namespace SignalAtlas.Decode;

/// <summary>
/// Builds a determined <see cref="Device"/> from decoded identity frames (SPEC §8.4).
/// Infers device type + protocol, picks the strongest primary identifier, merges all
/// frame identifiers, and vendor-maps only a real (non-LAA) MAC. Evidence is never empty (AC-D5).
/// </summary>
public sealed class DeviceResolver(IOuiLookup oui) : IDeviceResolver
{
    private readonly IOuiLookup _oui = oui;

    public Device? Resolve(IReadOnlyList<DecodedFrame> frames)
    {
        if (frames is null || frames.Count == 0)
            return null;

        // Protocol: the modal protocol across frames (frames of one device share it).
        var protocol = frames
            .GroupBy(f => f.Protocol, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First().Key;

        // Merge all frame identifiers; last frame wins on a key collision.
        var identifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in frames)
            foreach (var (key, value) in frame.Identifiers)
                identifiers[key] = value;

        var (deviceType, primaryKeys) = Classify(protocol);
        var primary = FirstPresent(identifiers, primaryKeys)
                      ?? identifiers.Values.FirstOrDefault();

        // Evidence: carry every frame's evidence forward (AC-D5 — never empty), then add
        // device-level determination evidence.
        var evidence = new List<EvidenceItem>();
        foreach (var frame in frames)
            evidence.AddRange(frame.Evidence);
        evidence.Add(new EvidenceItem("device_type", deviceType, 0.5));
        evidence.Add(new EvidenceItem("protocol", protocol, 0.5));
        if (primary is not null)
            evidence.Add(new EvidenceItem("primary_identifier", primary, 1.0));

        // Vendor only from a MAC-bearing identifier whose LAA bit is clear (SPEC §8.4).
        // A randomized/locally-administered MAC is flagged, not vendor-mapped (AC-D3).
        string? vendor = null;
        var mac = FirstPresent(identifiers, new[] { "bssid", "mac", "adva" });
        if (mac is not null)
        {
            if (_oui.IsLocallyAdministered(mac))
                evidence.Add(new EvidenceItem("laa_mac", $"randomized/locally-administered MAC {mac} — vendor not mapped", 1.0));
            else
                vendor = _oui.Vendor(mac);
        }

        var confidence = frames.Average(f => f.DecodeQuality);
        var id = DeterministicGuid.From(primary ?? $"{protocol}:{string.Join(",", identifiers.Values)}").ToString();

        return new Device(
            id, deviceType, primary, identifiers, vendor, protocol, confidence, evidence);
    }

    /// <summary>Maps a protocol to its device type and the ordered keys of its strongest identifier.</summary>
    private static (string DeviceType, string[] PrimaryKeys) Classify(string protocol)
    {
        var p = protocol.ToUpperInvariant();
        if (p.Contains("ADS") || p.Contains("ADSB"))
            return ("Aircraft", new[] { "icao", "callsign" });
        if (p.Contains("WI-FI") || p.Contains("WIFI") || p.Contains("WLAN") || p.Contains("802.11"))
            return ("Wi-Fi AP", new[] { "bssid", "mac" });
        if (p.Contains("BLE") || p.Contains("BLUETOOTH"))
            return ("BLE device", new[] { "adva", "mac" });
        if (p.Contains("FM"))
            return ("FM broadcast", new[] { "pi", "ps", "callsign" });
        if (p.Contains("ZIGBEE") || p.Contains("802.15.4"))
            return ("Zigbee node", new[] { "src_addr", "ext_addr", "short_addr", "pan_id" });
        if (p.Contains("LORA"))
            return ("LoRa node", new[] { "devaddr", "deveui" });
        return ("Unknown device", Array.Empty<string>());
    }

    private static string? FirstPresent(IReadOnlyDictionary<string, string> ids, IEnumerable<string> keys)
    {
        foreach (var key in keys)
            if (ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }
}
