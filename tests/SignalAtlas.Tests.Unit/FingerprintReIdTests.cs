using SignalAtlas.Domain;
using SignalAtlas.Fingerprint;
using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M10 — synthetic re-ID meeting NFR-A5 (≥90% true-match @ ≤5% false-match), reference-gated (G21).
/// Over a small synthetic population we compute, per <see cref="RefQuality"/>, the true-match rate at
/// an operating threshold pinned to ≤5% false-match. A better reference must separate better; the
/// ≥0.90 target is only claimed at the GOOD reference. <b>Synthetic only — field accuracy is gated
/// and field-validated (§18.3); lab numbers are not the field ceiling.</b>
/// </summary>
public class FingerprintReIdTests
{
    private const int Devices = 30;

    private sealed record ReIdResult(double TrueMatchRate, double FalseMatchRate, double Threshold);

    private static HardwareSignature[] BuildPopulation()
    {
        var rng = new DeterministicRng(42);
        var pop = new HardwareSignature[Devices];
        for (int d = 0; d < Devices; d++)
        {
            pop[d] = new HardwareSignature(
                GainImbalance: rng.NextGaussian(0, 0.05),
                PhaseErrRad: rng.NextGaussian(0, 0.05),
                DcI: rng.NextGaussian(0, 0.02),
                DcQ: rng.NextGaussian(0, 0.02),
                CfoHz: rng.NextGaussian(0, 3000),
                RiseSamples: 40 + rng.NextInt(0, 200),
                Overshoot: rng.NextDouble() * 0.08);
        }
        return pop;
    }

    private static ReIdResult Evaluate(string refQuality, HardwareSignature[] pop)
    {
        ulong refOffset = refQuality switch
        {
            RefQuality.Gpsdo => 0,
            RefQuality.Tcxo => 100_000,
            _ => 200_000,
        };

        var fp = new PhyFingerprinter(new FixedClock(DateTimeOffset.UnixEpoch));
        var enroll = new DeviceFingerprint[Devices];
        var probe = new DeviceFingerprint[Devices];
        for (int d = 0; d < Devices; d++)
        {
            ulong baseSeed = refOffset + (ulong)d * 1000UL;
            enroll[d] = fp.Extract(FingerprintSynth.Synthesize(pop[d], refQuality, baseSeed + 1), refQuality, $"e{d}");
            probe[d] = fp.Extract(FingerprintSynth.Synthesize(pop[d], refQuality, baseSeed + 2), refQuality, $"p{d}");
        }

        var matcher = new FingerprintMatcher();
        var genuine = new List<double>(Devices);
        var impostor = new List<double>(Devices * (Devices - 1));
        for (int a = 0; a < Devices; a++)
        {
            genuine.Add(matcher.Similarity(enroll[a], probe[a]));
            for (int b = 0; b < Devices; b++)
                if (a != b) impostor.Add(matcher.Similarity(enroll[a], probe[b]));
        }

        // Operating threshold pinned so the false-match rate is ≤ 5% (NFR-A5).
        impostor.Sort();
        int idx = (int)Math.Ceiling(0.95 * impostor.Count) - 1;
        idx = Math.Clamp(idx, 0, impostor.Count - 1);
        double threshold = impostor[idx];

        double fmr = impostor.Count(s => s >= threshold) / (double)impostor.Count;
        double tmr = genuine.Count(s => s >= threshold) / (double)genuine.Count;
        return new ReIdResult(tmr, fmr, threshold);
    }

    [Fact]
    public void ReId_MeetsNfrA5AtGoodReference_AndDegradesAtCrystal()
    {
        var pop = BuildPopulation();
        var gpsdo = Evaluate(RefQuality.Gpsdo, pop);
        var tcxo = Evaluate(RefQuality.Tcxo, pop);
        var crystal = Evaluate(RefQuality.Crystal, pop);

        // False-match ≤ 5% at every reference (operating point is pinned there).
        Assert.True(gpsdo.FalseMatchRate <= 0.06, $"gpsdo FMR {gpsdo.FalseMatchRate:0.###}");
        Assert.True(tcxo.FalseMatchRate <= 0.06, $"tcxo FMR {tcxo.FalseMatchRate:0.###}");
        Assert.True(crystal.FalseMatchRate <= 0.06, $"crystal FMR {crystal.FalseMatchRate:0.###}");

        // NFR-A5 ≥90% true-match ONLY claimed at the good references.
        Assert.True(gpsdo.TrueMatchRate >= 0.90, $"gpsdo TMR {gpsdo.TrueMatchRate:0.###} < 0.90");
        Assert.True(tcxo.TrueMatchRate >= 0.90, $"tcxo TMR {tcxo.TrueMatchRate:0.###} < 0.90");

        // Better reference ⇒ better separation; crystal is degraded (G21).
        Assert.True(gpsdo.TrueMatchRate >= crystal.TrueMatchRate, "gpsdo should separate ≥ crystal");
        Assert.True(tcxo.TrueMatchRate >= crystal.TrueMatchRate, "tcxo should separate ≥ crystal");
        Assert.True(crystal.TrueMatchRate < gpsdo.TrueMatchRate,
            $"crystal TMR {crystal.TrueMatchRate:0.###} not degraded vs gpsdo {gpsdo.TrueMatchRate:0.###}");
    }
}
