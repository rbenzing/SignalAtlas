using SignalAtlas.Correlation;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M4 — Emitter Correlation Engine (SPEC §8.5, G8). Decoded identifier is the PRIMARY key
/// (AC-CR1/CR2); with no decoded ID, a weighted RF score decides match-vs-new (AC-CR3/CR4).
/// Every result carries non-empty evidence (AC-CR5, P4) and the core is deterministic (P5).
/// </summary>
public class CorrelationTests
{
    private static readonly WeightedCorrelationEngine Engine = new();

    private static readonly IReadOnlyList<EvidenceItem> Seed =
        new[] { new EvidenceItem("seed", "existing", 1.0) };

    private static Emitter Emit(
        string id, string protocol, long freq, int stability = 1000,
        double? lat = null, double? lon = null, double? uncM = null,
        long count = 1, double confidence = 0.9,
        IReadOnlyDictionary<string, string>? ids = null) =>
        new(id, null, protocol, freq, stability, lat, lon, uncM, count, confidence,
            ids ?? new Dictionary<string, string>(), Seed);

    private static CorrelationInput In(
        string protocol, long freq, int bw = 125_000,
        double? lat = null, double? lon = null,
        IReadOnlyDictionary<string, string>? ids = null) =>
        new(protocol, freq, bw, lat, lon, DateTimeOffset.UnixEpoch,
            ids ?? new Dictionary<string, string>());

    private static Dictionary<string, string> Id(string key, string value) => new() { [key] = value };

    // 1. AC-CR1 — matching BSSID reuses the emitter even though the RF centre drifted far
    // beyond the stability window (ID beats RF).
    [Fact]
    public void MatchingBssid_ReusesEmitter_DespiteFrequencyDrift()
    {
        var existing = new[] { Emit("e1", "Wi-Fi", 2_412_000_000, ids: Id("bssid", "AA:BB:CC:00:11:22")) };
        var input = In("Wi-Fi", 2_412_500_000, ids: Id("bssid", "AA:BB:CC:00:11:22"));

        var r = Engine.Correlate(input, existing);

        Assert.False(r.IsNew);
        Assert.Equal("e1", r.Emitter.Id);
        Assert.True(r.Score >= 0.99);
        Assert.NotEmpty(r.Evidence);
    }

    // 2. AC-CR2 — a different decoded identifier of the same protocol is a distinct emitter,
    // never merged into the ID-mismatched one (even at an identical centre frequency).
    [Fact]
    public void DifferentIcao_ProducesDistinctEmitter()
    {
        var existing = new[] { Emit("e1", "ADS-B", 1_090_000_000, ids: Id("icao", "ABC123")) };
        var input = In("ADS-B", 1_090_000_000, ids: Id("icao", "DEF456"));

        var r = Engine.Correlate(input, existing);

        Assert.True(r.IsNew);
        Assert.NotEqual("e1", r.Emitter.Id);
        Assert.NotEmpty(r.Evidence);
    }

    // 3. AC-CR3 — no decoded ID, same protocol/freq/place → matches the existing emitter by score.
    [Fact]
    public void NoIdRepeat_MatchesExistingViaScoring()
    {
        var existing = new[] { Emit("e1", "LoRa", 915_000_000, stability: 5000, lat: 40.0, lon: -73.0, uncM: 50) };
        var input = In("LoRa", 915_000_000, lat: 40.0, lon: -73.0);

        var r = Engine.Correlate(input, existing);

        Assert.False(r.IsNew);
        Assert.Equal("e1", r.Emitter.Id);
        Assert.True(r.Score >= 0.60);
        Assert.NotEmpty(r.Evidence);
    }

    // 4. AC-CR4 boundary — a same-protocol candidate whose RF score is below threshold → NEW.
    [Fact]
    public void BelowThresholdCandidate_ProducesNewEmitter()
    {
        var existing = new[] { Emit("e1", "LoRa", 915_000_000, stability: 1000) };
        var input = In("LoRa", 915_050_000); // 50 kHz off, tolerance 3 kHz, no position → score 0

        var r = Engine.Correlate(input, existing);

        Assert.True(r.IsNew);
        Assert.True(r.Score < 0.60);
        Assert.NotEmpty(r.Evidence);
    }

    // 5. AC-CR5 — a match increments signal count and carries evidence; a new emitter also carries evidence.
    [Fact]
    public void Match_IncrementsCount_AndBothOutcomesCarryEvidence()
    {
        var existing = new[] { Emit("e1", "Wi-Fi", 2_412_000_000, count: 7, ids: Id("bssid", "AA:BB:CC:00:11:22")) };
        var matched = Engine.Correlate(In("Wi-Fi", 2_412_000_000, ids: Id("bssid", "AA:BB:CC:00:11:22")), existing);

        Assert.False(matched.IsNew);
        Assert.Equal(8, matched.Emitter.SignalCount);
        Assert.NotEmpty(matched.Emitter.Evidence);

        var created = Engine.Correlate(In("LoRa", 915_000_000), Array.Empty<Emitter>());

        Assert.True(created.IsNew);
        Assert.Equal(1, created.Emitter.SignalCount);
        Assert.NotEmpty(created.Emitter.Evidence);
    }

    // 6. Tie-break — two equal-scoring candidates resolve deterministically to the lowest Id,
    // and the tie-break basis is documented in evidence.
    [Fact]
    public void EqualScoringCandidates_TieBreakToLowestId_Documented()
    {
        var existing = new[]
        {
            Emit("e2", "LoRa", 915_001_000, stability: 1000),
            Emit("e1", "LoRa", 914_999_000, stability: 1000),
        };
        var input = In("LoRa", 915_000_000); // |Δ| = 1 kHz for both → identical freq score

        var r = Engine.Correlate(input, existing);

        Assert.False(r.IsNew);
        Assert.Equal("e1", r.Emitter.Id);
        Assert.Contains(r.Evidence, e => e.Feature.Contains("tie", StringComparison.OrdinalIgnoreCase));
    }

    // 7. Hard gate — a different protocol never matches, even with identical freq + position.
    [Fact]
    public void DifferentProtocol_NeverMatches()
    {
        var existing = new[] { Emit("e1", "BLE", 2_450_000_000, lat: 40.0, lon: -73.0, uncM: 50) };
        var input = In("Wi-Fi", 2_450_000_000, lat: 40.0, lon: -73.0);

        var r = Engine.Correlate(input, existing);

        Assert.True(r.IsNew);
        Assert.NotEmpty(r.Evidence);
    }
}
