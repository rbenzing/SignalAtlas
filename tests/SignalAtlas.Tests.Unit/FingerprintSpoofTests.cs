using SignalAtlas.Domain;
using SignalAtlas.Fingerprint;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M10 — spoof/clone detection (SPEC §8.10). Same decoded identifier + mismatched fingerprint ⇒ a
/// <c>fingerprint_spoof</c> alert with non-empty evidence (P4). Reference-gated (§18.3, G21).
/// </summary>
public class FingerprintSpoofTests
{
    private static readonly HardwareSignature GenuineRadio =
        new(0.06, 0.05, 0.02, -0.015, 1200, 90, 0.04);

    private static readonly HardwareSignature ClonerRadio =
        new(-0.07, -0.06, -0.02, 0.02, -1500, 200, 0.005);

    private static DeviceFingerprint Fp(HardwareSignature hw, ulong seed, string emitterId) =>
        new PhyFingerprinter().Extract(
            FingerprintSynth.Synthesize(hw, RefQuality.Gpsdo, seed), RefQuality.Gpsdo, emitterId);

    [Fact]
    public void ClonedIdentifier_MismatchedFingerprint_EmitsSpoofAlert()
    {
        const string bssid = "AA:BB:CC:DD:EE:FF";
        var known = Fp(GenuineRadio, seed: 1, emitterId: "emitter-genuine");
        var cloned = Fp(ClonerRadio, seed: 2, emitterId: "emitter-cloned"); // same BSSID, other hardware

        var alert = new SpoofDetector().Check(bssid, known, cloned);

        Assert.NotNull(alert);
        Assert.Equal(SpoofDetector.SpoofAlertKind, alert!.Kind);
        Assert.Equal("fingerprint_spoof", alert.Kind);
        Assert.NotEmpty(alert.Evidence);
        Assert.Contains(alert.Evidence, e => e.Feature == "decoded_identifier" && e.Value == bssid);
    }

    [Fact]
    public void SameHardware_SameIdentifier_NoSpoof()
    {
        const string bssid = "AA:BB:CC:DD:EE:FF";
        var known = Fp(GenuineRadio, seed: 1, emitterId: "emitter-a");
        var again = Fp(GenuineRadio, seed: 2, emitterId: "emitter-a"); // same radio, same ID → fine

        Assert.Null(new SpoofDetector().Check(bssid, known, again));
    }
}
