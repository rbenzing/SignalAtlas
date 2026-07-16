using System.Globalization;
using System.Text.RegularExpressions;
using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// Deterministic keyword/pattern intent classifier + slot extraction (SPEC §8.12) — NO LLM, no
/// randomness. Maps free text to one of the enumerated supported <see cref="AnalystQueryType"/>s and
/// extracts slots (protocol, time window, coords/radius, band). Precedence is fixed so overlapping
/// keywords resolve deterministically (Occupancy → NearLocation → UnknownInBand → CountByProtocol →
/// WhatChanged → ListByProtocol → Unsupported). Unrecognized phrasing → Unsupported.
/// </summary>
public sealed partial class IntentClassifier : IIntentClassifier
{
    public AnalystIntent Classify(string text)
    {
        var q = (text ?? string.Empty).ToLowerInvariant().Trim();
        if (q.Length == 0) return new AnalystIntent(AnalystQueryType.Unsupported);

        // 1. Occupancy / spectrum usage.
        if (q.Contains("busiest") || q.Contains("occupancy") || q.Contains("spectrum usage"))
            return new AnalystIntent(AnalystQueryType.Occupancy);

        // 2. Near a location.
        if (q.Contains("near") || q.Contains("around") || q.Contains("close to"))
        {
            var (lat, lon) = ExtractCoords(q);
            return new AnalystIntent(
                AnalystQueryType.NearLocation,
                Latitude: lat, Longitude: lon, RadiusMeters: ExtractRadiusMeters(q));
        }

        // 3. Unknown / unidentified emitters (optionally in a band).
        if (q.Contains("unknown") || q.Contains("unidentified"))
        {
            var band = ExtractBand(q);
            return new AnalystIntent(
                AnalystQueryType.UnknownInBand, BandLowHz: band?.Low, BandHighHz: band?.High);
        }

        // 4. Counts.
        if (q.Contains("how many") || q.Contains("count"))
            return new AnalystIntent(AnalystQueryType.CountByProtocol, Protocol: ExtractProtocol(q));

        // 5. What changed / new activity in a window.
        if (q.Contains("what changed") || q.Contains("what's new") || q.Contains("whats new")
            || q.Contains("new activity") || q.Contains("today") || q.Contains("last hour"))
            return new AnalystIntent(AnalystQueryType.WhatChanged, Window: ExtractWindow(q));

        // 6. Show / list, or a bare protocol name.
        var proto = ExtractProtocol(q);
        if (q.Contains("show") || q.Contains("list") || proto is not null)
            return new AnalystIntent(AnalystQueryType.ListByProtocol, Protocol: proto);

        return new AnalystIntent(AnalystQueryType.Unsupported);
    }

    /// <summary>Recognized protocol tokens → canonical store protocol strings.</summary>
    private static string? ExtractProtocol(string q)
    {
        if (Regex.IsMatch(q, @"\bwi[-\s]?fi\b")) return "Wi-Fi";
        if (Regex.IsMatch(q, @"\bble\b") || q.Contains("bluetooth")) return "BLE";
        if (Regex.IsMatch(q, @"\blora\b")) return "LoRa";
        if (Regex.IsMatch(q, @"\bzigbee\b")) return "Zigbee";
        if (Regex.IsMatch(q, @"\bads[-\s]?b\b") || Regex.IsMatch(q, @"\badsb\b")) return "ADS-B";
        if (Regex.IsMatch(q, @"\bfm\b")) return "FM";
        if (q.Contains("unknown") || q.Contains("unidentified")) return "Unknown";
        return null;
    }

    private static TimeSpan ExtractWindow(string q)
    {
        var m = Regex.Match(q, @"last\s+(\d+)\s*(minute|min|hour|hr)");
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value.StartsWith("h", StringComparison.Ordinal)
                ? TimeSpan.FromHours(n)
                : TimeSpan.FromMinutes(n);
        }

        if (q.Contains("last hour")) return TimeSpan.FromHours(1);
        return TimeSpan.FromHours(24); // "today" / "what changed" default window.
    }

    private static (double? Lat, double? Lon) ExtractCoords(string q)
    {
        var m = Regex.Match(q, @"(-?\d+\.\d+)\s*,\s*(-?\d+\.\d+)");
        if (!m.Success) return (null, null);
        return (
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static double ExtractRadiusMeters(string q)
    {
        var m = Regex.Match(q, @"(?:within|radius(?:\s+of)?)\s+(\d+(?:\.\d+)?)\s*(km|kilometer|kilometre|m|meter|metre)?");
        if (!m.Success) return 1000.0; // default 1 km
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value;
        return unit.StartsWith("k", StringComparison.Ordinal) ? v * 1000.0 : v;
    }

    private static (long Low, long High)? ExtractBand(string q)
    {
        if (Regex.IsMatch(q, @"2\.4\s*ghz") || q.Contains("2400")) return (2_400_000_000, 2_500_000_000);
        if (q.Contains("915") || q.Contains("902")) return (902_000_000, 928_000_000);
        if (q.Contains("433")) return (433_000_000, 434_800_000);
        if (q.Contains("1090")) return (1_089_000_000, 1_091_000_000);
        if (Regex.IsMatch(q, @"\bfm\b") || q.Contains("88-108")) return (88_000_000, 108_000_000);
        return null;
    }
}
