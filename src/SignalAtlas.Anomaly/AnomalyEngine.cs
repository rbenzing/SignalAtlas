using System.Globalization;
using SignalAtlas.Domain;

namespace SignalAtlas.Anomaly;

/// <summary>
/// Rule-based anomaly detection (SPEC §8.8). Given an <see cref="AnomalyEvent"/> and the emitter's
/// prior <see cref="EmitterBaseline"/>, runs six independent detectors and returns zero or more
/// <see cref="Alert"/>s. All detection is <b>relative</b> (§4.5 / G20): power is compared as a dBFS
/// delta, never absolute dBm. Guarantees:
/// <list type="bullet">
///   <item><b>Evidence (P4):</b> every alert carries a non-empty <c>Evidence</c> list.</item>
///   <item><b>Deterministic (P5):</b> each alert's <c>Id</c> is
///   <c>DeterministicGuid.From("{kind}:{emitterId}:{time:o}")</c>; identical inputs → identical Ids.</item>
///   <item><b>Dedup:</b> at most one alert per Kind per emitter per call. A new_emitter fires only
///   the first time (baseline.KnownEmitter == false); a repeat visit (KnownEmitter == true) → none.</item>
/// </list>
/// <b>Severity mapping</b> (info | warning | critical):
/// new_emitter → info, power_change → info; new_device / protocol_change / location_change /
/// occupancy_spike → warning. Critical is reserved for future correlated detectors.
/// </summary>
public sealed class AnomalyEngine : IAnomalyEngine
{
    /// <summary>Purely informational: something new observed, no threat implied.</summary>
    public const string SeverityInfo = "info";

    /// <summary>Noteworthy change an analyst should review.</summary>
    public const string SeverityWarning = "warning";

    private readonly AnomalyOptions _options;

    public AnomalyEngine() : this(AnomalyOptions.Default) { }

    public AnomalyEngine(AnomalyOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public IReadOnlyList<Alert> Evaluate(AnomalyEvent evt, EmitterBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(baseline);

        var alerts = new List<Alert>(6);

        // new_emitter — first sighting only; a known emitter re-appearing is not an anomaly (dedup).
        if (!baseline.KnownEmitter)
        {
            alerts.Add(Make(evt, Alert.NewEmitter, SeverityInfo,
                $"First sighting of emitter '{evt.EmitterId}'.",
                new EvidenceItem("known_emitter", "false", 1.0),
                new EvidenceItem("emitter_id", evt.EmitterId, 1.0)));
        }

        // new_device — the event names a device the store has not seen before.
        if (evt.DeviceId is { } deviceId && !baseline.KnownDevice)
        {
            alerts.Add(Make(evt, Alert.NewDevice, SeverityWarning,
                $"First sighting of device '{deviceId}' on emitter '{evt.EmitterId}'.",
                new EvidenceItem("known_device", "false", 1.0),
                new EvidenceItem("device_id", deviceId, 1.0)));
        }

        // protocol_change — the emitter is speaking a different protocol than last recorded.
        if (baseline.LastProtocol is { } last &&
            !string.Equals(last, evt.Protocol, StringComparison.Ordinal))
        {
            alerts.Add(Make(evt, Alert.ProtocolChange, SeverityWarning,
                $"Protocol changed for '{evt.EmitterId}': {last} → {evt.Protocol}.",
                new EvidenceItem("old_protocol", last, 1.0),
                new EvidenceItem("new_protocol", evt.Protocol, 1.0)));
        }

        // location_change — moved further than the documented great-circle threshold.
        if (baseline is { LastLatitude: { } bLat, LastLongitude: { } bLon } &&
            evt is { Latitude: { } eLat, Longitude: { } eLon })
        {
            var distanceM = HaversineMeters(bLat, bLon, eLat, eLon);
            if (distanceM > _options.LocationChangeThresholdMeters)
            {
                alerts.Add(Make(evt, Alert.LocationChange, SeverityWarning,
                    $"Emitter '{evt.EmitterId}' moved {distanceM:0} m " +
                    $"(> {_options.LocationChangeThresholdMeters:0} m threshold).",
                    new EvidenceItem("distance_m", Num(distanceM), distanceM),
                    new EvidenceItem("threshold_m", Num(_options.LocationChangeThresholdMeters),
                        _options.LocationChangeThresholdMeters),
                    new EvidenceItem("from", $"{Num(bLat)},{Num(bLon)}", 1.0),
                    new EvidenceItem("to", $"{Num(eLat)},{Num(eLon)}", 1.0)));
            }
        }

        // power_change — relative dBFS delta from baseline exceeds the documented threshold (§4.5).
        if (baseline.BaselinePowerDbfs is { } basePower)
        {
            var deltaDb = evt.PowerDbfs - basePower;
            if (Math.Abs(deltaDb) > _options.PowerChangeThresholdDb)
            {
                alerts.Add(Make(evt, Alert.PowerChange, SeverityInfo,
                    $"Relative power for '{evt.EmitterId}' shifted {deltaDb:+0.0;-0.0} dB " +
                    $"(baseline {Num(basePower)} dBFS → {Num(evt.PowerDbfs)} dBFS).",
                    new EvidenceItem("delta_db", Num(deltaDb), Math.Abs(deltaDb)),
                    new EvidenceItem("threshold_db", Num(_options.PowerChangeThresholdDb),
                        _options.PowerChangeThresholdDb),
                    new EvidenceItem("baseline_dbfs", Num(basePower), basePower),
                    new EvidenceItem("event_dbfs", Num(evt.PowerDbfs), evt.PowerDbfs)));
            }
        }

        // occupancy_spike — occupancy exceeds baseline by more than the documented factor.
        if (baseline.BaselineOccupancy is { } baseOcc &&
            evt.Occupancy > baseOcc * _options.OccupancySpikeFactor)
        {
            var factor = baseOcc > 0 ? evt.Occupancy / baseOcc : double.PositiveInfinity;
            alerts.Add(Make(evt, Alert.OccupancySpike, SeverityWarning,
                $"Occupancy for '{evt.EmitterId}' spiked to {Num(evt.Occupancy)} " +
                $"({factor:0.0}× the {Num(baseOcc)} baseline).",
                new EvidenceItem("event_occupancy", Num(evt.Occupancy), evt.Occupancy),
                new EvidenceItem("baseline_occupancy", Num(baseOcc), baseOcc),
                new EvidenceItem("factor", Num(factor), factor),
                new EvidenceItem("factor_threshold", Num(_options.OccupancySpikeFactor),
                    _options.OccupancySpikeFactor)));
        }

        return alerts;
    }

    /// <summary>Builds an alert with a deterministic Id seeded by kind + emitter + event time (P5).</summary>
    private static Alert Make(
        AnomalyEvent evt, string kind, string severity, string summary,
        params EvidenceItem[] evidence)
        => new(
            Id: DeterministicGuid.From($"{kind}:{evt.EmitterId}:{evt.Time:o}"),
            Time: evt.Time,
            EmitterId: evt.EmitterId,
            DeviceId: evt.DeviceId,
            Kind: kind,
            Severity: severity,
            Summary: summary,
            Evidence: evidence);

    private static string Num(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Great-circle distance in metres between two WGS-84 points (§8.6/§8.7 convention).</summary>
    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                  * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
