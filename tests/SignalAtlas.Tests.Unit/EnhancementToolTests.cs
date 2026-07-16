using System.Text.Json;
using SignalAtlas.Domain;
using SignalAtlas.Enhancement;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M13 (SPEC §8.13) deterministic tool layer — NO Claude. Enhancement-candidate count from edge data
/// alone (AC-DA7); session-intelligence assembly; egress guard excludes raw IQ + cleartext (AC-DA3).
/// </summary>
public class EnhancementToolTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private static Signal Sig(long id, string protocol, double confidence,
        IReadOnlyDictionary<string, double>? features = null)
        => new(id, T, id, null, null, protocol, confidence, "rules",
            [new EvidenceItem("f", "v", 1.0)], 915_000_000, 125_000, 100,
            features ?? new Dictionary<string, double> { ["snr_db"] = 12.5 });

    // ── AC-DA7: enhancement-candidate count computed from edge data alone, no Claude ───────────────

    [Fact]
    public void Count_CountsUnknownAndBelowFloorSignals_PlusUnresolvedEmitters()
    {
        var signals = new[]
        {
            Sig(1, "Wi-Fi", 0.95),                 // confident → not a candidate
            Sig(2, "Unknown", 0.99),               // Unknown → candidate regardless of confidence
            Sig(3, "LoRa", 0.55),                  // below the 0.6 floor → candidate
            Sig(4, "LoRa", EnhancementCandidates.ConfidenceFloor), // exactly at floor → NOT a candidate
        };
        var emitters = new[]
        {
            AnalystData.Emitter("e-resolved", "Wi-Fi", 2_437_000_000) with { DeviceId = "dev-1" },
            AnalystData.Emitter("e-unresolved", "LoRa", 915_000_000), // DeviceId null → unresolved device
        };

        var report = EnhancementCandidates.Count(signals, emitters);

        Assert.Equal(2, report.LowConfidenceSignals);
        Assert.Equal(1, report.UnresolvedDevices);
        Assert.Equal(3, report.Total);
    }

    [Fact]
    public void ConfidenceFloor_IsDocumentedAt_0_6()
        => Assert.Equal(0.6, EnhancementCandidates.ConfidenceFloor);

    // ── tool layer assembles a session's intelligence correctly (no Claude) ────────────────────────

    [Fact]
    public void Assemble_GathersSignalsEmittersAndFrames_AsStructuredMetadata()
    {
        var signals = new[] { Sig(1, "LoRa", 0.8) };
        var emitters = new[] { AnalystData.Emitter("e-1", "LoRa", 915_000_000, 42.36, -71.06) };
        var frames = new[]
        {
            new DecodedFrame("Wi-Fi", "beacon",
                new Dictionary<string, string> { ["bssid"] = "A4:2B:B0:11:22:33" }, 0.9,
                [new EvidenceItem("crc", "pass", 1.0)]),
        };

        var payload = SessionIntelligence.Assemble("sess-1", signals, emitters, frames, []);

        Assert.Equal("sess-1", payload.SessionId);
        Assert.Equal(1, payload.Signals.Count);
        Assert.Equal("LoRa", payload.Signals[0].Protocol);
        Assert.Equal(12.5, payload.Signals[0].Features["snr_db"]);
        Assert.Equal("e-1", Assert.Single(payload.Emitters).Id);
        Assert.Equal("beacon", Assert.Single(payload.DecodedFrames).FrameType);
    }

    // ── AC-DA3: egress payload excludes raw IQ + cleartext (inject marker, assert absent) ──────────

    [Fact]
    public void Assemble_ExcludesRawIqReference_AndForbiddenFeatureKeys()
    {
        var features = new Dictionary<string, double> { ["snr_db"] = 12.5, ["raw_iq_blob"] = 1.0 };
        var signals = new[] { Sig(1, "Unknown", 0.4, features) };
        // The observation carries a pointer to retained raw IQ — it must NEVER reach egress (§4.2 L7).
        var obs = new Observation(
            T, TimeSource.Gps, "col-1", 1, 915_000_000, 125_000, -37.5, PowerRef.Relative, 11.0,
            42.36, -71.06, PositionQuality.Good, IqRef: "raw_iq://__SENTINEL_MUST_NOT_LEAK__",
            CorrelationId: Guid.NewGuid());

        var payload = SessionIntelligence.Assemble("sess-1", signals, [], [], [obs]);
        var json = JsonSerializer.Serialize(payload);

        Assert.DoesNotContain("__SENTINEL_MUST_NOT_LEAK__", json);       // raw-IQ pointer dropped
        Assert.DoesNotContain("raw_iq_blob", json);                     // forbidden feature key stripped
        Assert.Contains("snr_db", json);                                // structured RF metadata retained
        EgressGuard.AssertNoRawIqOrContent(payload);                    // guard passes on a clean payload
    }

    [Fact]
    public void EgressGuard_Throws_WhenPayloadContainsForbiddenMarker()
    {
        // A contaminated payload (a raw-IQ marker smuggled into an identifier value) must be caught.
        var contaminated = new EgressPayload("sess-1",
            [],
            [new EgressEmitter("e-1", null, "LoRa", 915_000_000, null, null, null,
                new Dictionary<string, string> { ["leak"] = "raw_iq_dump" }, [])],
            []);

        Assert.Throws<InvalidOperationException>(() => EgressGuard.AssertNoRawIqOrContent(contaminated));
    }
}
