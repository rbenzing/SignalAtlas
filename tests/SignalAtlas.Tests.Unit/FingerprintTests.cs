using SignalAtlas.Domain;
using SignalAtlas.Fingerprint;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M10 — RF/PHY fingerprint extraction + matching (SPEC §8.10, G18/G21). Features are drift-robust
/// (relative/ratio) so an emitter re-ID's across rotating IDs and different carriers. Numbers here
/// are SYNTHETIC; field accuracy is reference-gated and field-validated (§18.3, NFR-A5).
/// </summary>
public class FingerprintTests
{
    private static readonly HardwareSignature RadioA =
        new(GainImbalance: 0.06, PhaseErrRad: 0.05, DcI: 0.02, DcQ: -0.015, CfoHz: 1200, RiseSamples: 90, Overshoot: 0.04);

    private static readonly HardwareSignature RadioB =
        new(GainImbalance: -0.07, PhaseErrRad: -0.06, DcI: -0.02, DcQ: 0.02, CfoHz: -1500, RiseSamples: 200, Overshoot: 0.005);

    private static DeviceFingerprint Extract(HardwareSignature hw, string refQuality, ulong seed,
        string emitterId = "emitter-x", long centerFreqHz = 100_000_000)
    {
        var block = FingerprintSynth.Synthesize(hw, refQuality, seed, centerFreqHz);
        return new PhyFingerprinter(new FixedClock(DateTimeOffset.UnixEpoch)).Extract(block, refQuality, emitterId);
    }

    [Fact]
    public void Extract_SameInputs_ProducesIdenticalEmbedding()
    {
        var block = FingerprintSynth.Synthesize(RadioA, RefQuality.Gpsdo, seed: 7);
        var fp = new PhyFingerprinter(new FixedClock(DateTimeOffset.UnixEpoch));

        var a = fp.Extract(block, RefQuality.Gpsdo, "e1");
        var b = fp.Extract(block, RefQuality.Gpsdo, "e1");

        Assert.Equal(PhyFingerprinter.EmbeddingLength, a.FeatureVector.Length);
        Assert.Equal(a.FeatureVector, b.FeatureVector); // deterministic (§6.1 P5)
    }

    [Fact]
    public void Extract_SameIqAndClock_ProducesIdenticalUpdatedAt()
    {
        // P5: identical inputs + identical injected clock → identical output, including UpdatedAt.
        var block = FingerprintSynth.Synthesize(RadioA, RefQuality.Gpsdo, seed: 7);
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);

        var a = new PhyFingerprinter(clock).Extract(block, RefQuality.Gpsdo, "e1");
        var b = new PhyFingerprinter(clock).Extract(block, RefQuality.Gpsdo, "e1");

        Assert.Equal(a.UpdatedAt, b.UpdatedAt);
        Assert.Equal(a.FeatureVector, b.FeatureVector);
    }

    [Fact]
    public void SameHardware_DifferentSeedAndCarrier_HighSimilarity()
    {
        // Same hardware, different noise seed AND different carrier → still the same hardware.
        var a = Extract(RadioA, RefQuality.Gpsdo, seed: 11, centerFreqHz: 100_000_000);
        var b = Extract(RadioA, RefQuality.Gpsdo, seed: 22, centerFreqHz: 433_920_000);

        var matcher = new FingerprintMatcher();
        double sim = matcher.Similarity(a, b);

        Assert.True(sim > 0.95, $"same-hardware similarity {sim:0.###} was not > 0.95");
        Assert.True(matcher.IsSameHardware(a, b));
    }

    [Fact]
    public void DistinctHardware_LowSimilarity()
    {
        var a = Extract(RadioA, RefQuality.Gpsdo, seed: 11);
        var b = Extract(RadioB, RefQuality.Gpsdo, seed: 12);

        var matcher = new FingerprintMatcher();
        double sim = matcher.Similarity(a, b);

        Assert.True(sim < 0.6, $"distinct-hardware similarity {sim:0.###} was not < 0.6");
        Assert.False(matcher.IsSameHardware(a, b));
    }

    [Fact]
    public void RotatedMac_SameHardware_ResolvesToOneFingerprint()
    {
        // Two captures of ONE radio decoded under two rotating MACs → one hardware.
        var cap1 = Extract(RadioA, RefQuality.Tcxo, seed: 101, emitterId: "aa:bb:cc:00:00:01");
        var cap2 = Extract(RadioA, RefQuality.Tcxo, seed: 202, emitterId: "de:ad:be: ef:00:99".Replace(" ", ""));

        var matcher = new FingerprintMatcher();

        Assert.NotEqual(cap1.EmitterId, cap2.EmitterId);         // different decoded IDs...
        Assert.True(matcher.IsSameHardware(cap1, cap2),          // ...resolve to one hardware.
            $"rotated-MAC captures did not match (sim {matcher.Similarity(cap1, cap2):0.###})");
    }

    [Theory]
    [InlineData(RefQuality.Gpsdo)]
    [InlineData(RefQuality.Tcxo)]
    [InlineData(RefQuality.Crystal)]
    public void Extract_StoresRefQualityAndStability(string refQuality)
    {
        var fp = Extract(RadioA, refQuality, seed: 5);
        Assert.Equal(refQuality, fp.RefQuality);
        Assert.InRange(fp.Stability, 0.0, 1.0);
    }
}
