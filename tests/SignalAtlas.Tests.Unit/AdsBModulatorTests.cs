using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class AdsBModulatorTests
{
    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    // At 2 MS/s hus=1, leadSlots=0: preamble pulses land on I samples {0,2,7,9}, gaps are 0.
    [Fact]
    public void Modulate_PlacesPreamblePulsesAtKnownHalfMicrosecondSlots()
    {
        var block = AdsBModulator.Modulate(new byte[14], leadSlots: 0, trailSlots: 0, noiseSigma: 0.0);

        foreach (var slot in new[] { 0, 2, 7, 9 })
            Assert.True(block.I[slot] > 0.5f, $"preamble slot {slot} should be a pulse");
        foreach (var slot in new[] { 1, 3, 4, 5, 6, 8 })
            Assert.Equal(0f, block.I[slot]);
    }

    // A data '1' bit puts the pulse in the FIRST half-chip; a '0' in the SECOND.
    [Fact]
    public void Modulate_EncodesBitsAsPpmFirstOrSecondHalfChip()
    {
        // bit0 = MSB of byte0. 0x80 → bit0 = 1; 0x00 → bit0 = 0.
        var one = AdsBModulator.Modulate(Hex("8000000000000000000000000000"), leadSlots: 0, trailSlots: 0);
        var zero = AdsBModulator.Modulate(Hex("0000000000000000000000000000"), leadSlots: 0, trailSlots: 0);

        // Data starts at slot 16 (after the 16-slot preamble). bit0 occupies slots 16,17.
        Assert.True(one.I[16] > 0.5f && one.I[17] == 0f);   // '1' → first half-chip
        Assert.True(zero.I[16] == 0f && zero.I[17] > 0.5f); // '0' → second half-chip
    }

    [Fact]
    public void Modulate_IsInBandAt1090AndDeterministicUnderSeededNoise()
    {
        var a = AdsBModulator.Modulate(Hex("8D4840D6202CC371C32CE0576098"), noiseSigma: 0.1, seed: 7);
        var b = AdsBModulator.Modulate(Hex("8D4840D6202CC371C32CE0576098"), noiseSigma: 0.1, seed: 7);

        Assert.Equal(1_090_000_000, a.CenterFreqHz);
        Assert.Equal(a.I, b.I);   // same seed → identical noise → deterministic fixtures
    }
}
