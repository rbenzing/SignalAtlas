using SignalAtlas.Anomaly;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M7 — rule-based Anomaly Engine (SPEC §8.8). Covers the §8.8 test list:
/// new emitter → one alert / repeat → none (dedup); new device → new_device;
/// protocol flip → protocol_change + old→new evidence; relative power jump → power_change
/// (within threshold → none); occupancy spike vs baseline → occupancy_spike (normal → none);
/// location move beyond threshold → location_change; and P4 (every alert has non-empty
/// evidence) / P5 (deterministic Ids). Power is relative dBFS only (§4.5 / G20).
/// </summary>
public class AnomalyTests
{
    private static readonly AnomalyEngine Engine = new();
    private static readonly DateTimeOffset T0 = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    // A quiescent event that matches its baseline exactly → no detector should fire.
    private static AnomalyEvent Steady() => new(
        Time: T0, EmitterId: "em-1", DeviceId: "dev-1", Protocol: "wifi",
        PowerDbfs: -40.0, Latitude: 51.5, Longitude: -0.12, Occupancy: 0.20);

    private static EmitterBaseline KnownBaseline() => new(
        KnownEmitter: true, KnownDevice: true, LastProtocol: "wifi",
        LastLatitude: 51.5, LastLongitude: -0.12,
        BaselinePowerDbfs: -40.0, BaselineOccupancy: 0.20);

    private static Alert? Single(IReadOnlyList<Alert> alerts, string kind)
        => alerts.SingleOrDefault(a => a.Kind == kind);

    // 1a. Unknown emitter → exactly one new_emitter alert.
    [Fact]
    public void UnknownEmitter_YieldsOneNewEmitterAlert()
    {
        var baseline = KnownBaseline() with { KnownEmitter = false };
        var alerts = Engine.Evaluate(Steady(), baseline);

        var alert = Single(alerts, Alert.NewEmitter);
        Assert.NotNull(alert);
        Assert.Equal(1, alerts.Count(a => a.Kind == Alert.NewEmitter));
        Assert.Equal("em-1", alert!.EmitterId);
        Assert.NotEmpty(alert.Evidence);
    }

    // 1b. Already-known emitter → NO new_emitter alert (dedup: repeat → none).
    [Fact]
    public void KnownEmitter_YieldsNoNewEmitterAlert()
    {
        var alerts = Engine.Evaluate(Steady(), KnownBaseline());
        Assert.Null(Single(alerts, Alert.NewEmitter));
    }

    // 2a. Unknown device → new_device alert.
    [Fact]
    public void UnknownDevice_YieldsNewDevice()
    {
        var baseline = KnownBaseline() with { KnownDevice = false };
        var alerts = Engine.Evaluate(Steady(), baseline);

        var alert = Single(alerts, Alert.NewDevice);
        Assert.NotNull(alert);
        Assert.Equal("dev-1", alert!.DeviceId);
        Assert.NotEmpty(alert.Evidence);
    }

    // 2b. No device id on the event → no new_device even if baseline says unknown.
    [Fact]
    public void NullDevice_YieldsNoNewDevice()
    {
        var evt = Steady() with { DeviceId = null };
        var baseline = KnownBaseline() with { KnownDevice = false };
        Assert.Null(Single(Engine.Evaluate(evt, baseline), Alert.NewDevice));
    }

    // 3a. Protocol flip → protocol_change citing old→new.
    [Fact]
    public void ProtocolFlip_YieldsProtocolChange_WithOldNewEvidence()
    {
        var evt = Steady() with { Protocol = "ble" };
        var alerts = Engine.Evaluate(evt, KnownBaseline()); // baseline LastProtocol = "wifi"

        var alert = Single(alerts, Alert.ProtocolChange);
        Assert.NotNull(alert);
        Assert.NotEmpty(alert!.Evidence);
        // Evidence must cite both the old and the new protocol somewhere.
        var blob = string.Join("|", alert.Evidence.Select(e => $"{e.Feature}={e.Value}"));
        Assert.Contains("wifi", blob);
        Assert.Contains("ble", blob);
    }

    // 3b. Same protocol → no protocol_change.
    [Fact]
    public void SameProtocol_YieldsNoProtocolChange()
        => Assert.Null(Single(Engine.Evaluate(Steady(), KnownBaseline()), Alert.ProtocolChange));

    // 4a. Relative power jump beyond threshold → power_change.
    [Fact]
    public void PowerJumpBeyondThreshold_YieldsPowerChange()
    {
        var evt = Steady() with { PowerDbfs = -30.0 }; // +10 dB vs -40 baseline (> 6 dB)
        var alert = Single(Engine.Evaluate(evt, KnownBaseline()), Alert.PowerChange);

        Assert.NotNull(alert);
        Assert.NotEmpty(alert!.Evidence);
    }

    // 4b. Power within threshold → no power_change.
    [Fact]
    public void PowerWithinThreshold_YieldsNoPowerChange()
    {
        var evt = Steady() with { PowerDbfs = -43.0 }; // 3 dB delta (< 6 dB)
        Assert.Null(Single(Engine.Evaluate(evt, KnownBaseline()), Alert.PowerChange));
    }

    // 5a. Occupancy spike vs baseline → occupancy_spike.
    [Fact]
    public void OccupancySpike_YieldsAlert()
    {
        var evt = Steady() with { Occupancy = 0.80 }; // 4× the 0.20 baseline (> 2×)
        var alert = Single(Engine.Evaluate(evt, KnownBaseline()), Alert.OccupancySpike);

        Assert.NotNull(alert);
        Assert.NotEmpty(alert!.Evidence);
    }

    // 5b. Occupancy within factor → no spike.
    [Fact]
    public void NormalOccupancy_YieldsNoSpike()
    {
        var evt = Steady() with { Occupancy = 0.30 }; // 1.5× baseline (< 2×)
        Assert.Null(Single(Engine.Evaluate(evt, KnownBaseline()), Alert.OccupancySpike));
    }

    // 6a. Location move beyond threshold → location_change citing the distance.
    [Fact]
    public void LocationMoveBeyondThreshold_YieldsLocationChange()
    {
        // ~0.01° latitude ≈ 1.1 km, well beyond the 100 m default.
        var evt = Steady() with { Latitude = 51.51, Longitude = -0.12 };
        var alert = Single(Engine.Evaluate(evt, KnownBaseline()), Alert.LocationChange);

        Assert.NotNull(alert);
        Assert.NotEmpty(alert!.Evidence);
        Assert.Contains(alert.Evidence, e => e.Feature.Contains("distance") || e.Feature.Contains('m'));
    }

    // 6b. Small move within threshold → no location_change.
    [Fact]
    public void LocationWithinThreshold_YieldsNoLocationChange()
    {
        // ~0.0005° ≈ 55 m, under the 100 m default.
        var evt = Steady() with { Latitude = 51.5005 };
        Assert.Null(Single(Engine.Evaluate(evt, KnownBaseline()), Alert.LocationChange));
    }

    // 7. Steady state matching baseline → no alerts at all.
    [Fact]
    public void SteadyState_YieldsNoAlerts()
        => Assert.Empty(Engine.Evaluate(Steady(), KnownBaseline()));

    // Build an event/baseline that trips every detector at once.
    private static (AnomalyEvent, EmitterBaseline) AllTripping()
    {
        var evt = new AnomalyEvent(
            Time: T0, EmitterId: "em-9", DeviceId: "dev-9", Protocol: "ble",
            PowerDbfs: -20.0, Latitude: 51.60, Longitude: -0.12, Occupancy: 0.90);
        var baseline = new EmitterBaseline(
            KnownEmitter: false, KnownDevice: false, LastProtocol: "wifi",
            LastLatitude: 51.50, LastLongitude: -0.12,
            BaselinePowerDbfs: -40.0, BaselineOccupancy: 0.10);
        return (evt, baseline);
    }

    // P4 — every emitted alert carries non-empty evidence, and all six detectors can fire.
    [Fact]
    public void EveryEmittedAlert_HasNonEmptyEvidence()
    {
        var (evt, baseline) = AllTripping();
        var alerts = Engine.Evaluate(evt, baseline);

        Assert.Equal(6, alerts.Count);
        Assert.All(alerts, a =>
        {
            Assert.NotEmpty(a.Evidence);
            Assert.All(a.Evidence, e => Assert.False(string.IsNullOrWhiteSpace(e.Feature)));
            Assert.False(string.IsNullOrWhiteSpace(a.Summary));
            Assert.False(string.IsNullOrWhiteSpace(a.Severity));
        });
        // No two alerts of the same kind for one emitter (dedup).
        Assert.Equal(alerts.Select(a => a.Kind).Distinct().Count(), alerts.Count);
    }

    // P5 — deterministic: same inputs → identical alert Ids.
    [Fact]
    public void Deterministic_SameInputs_SameIds()
    {
        var (evt, baseline) = AllTripping();
        var a = Engine.Evaluate(evt, baseline);
        var b = Engine.Evaluate(evt, baseline);

        Assert.Equal(
            a.OrderBy(x => x.Kind).Select(x => x.Id),
            b.OrderBy(x => x.Kind).Select(x => x.Id));
    }

    // Ids follow the documented seed: DeterministicGuid.From("{kind}:{emitterId}:{time:o}").
    [Fact]
    public void AlertId_MatchesDeterministicSeed()
    {
        var baseline = KnownBaseline() with { KnownEmitter = false };
        var evt = Steady();
        var alert = Single(Engine.Evaluate(evt, baseline), Alert.NewEmitter)!;

        var expected = DeterministicGuid.From($"{Alert.NewEmitter}:{evt.EmitterId}:{evt.Time:o}");
        Assert.Equal(expected, alert.Id);
    }
}
