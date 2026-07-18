using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class AdsBDemodulatorTests
{
    private const string GoldenHex = "8D4840D6202CC371C32CE0576098";
    private static readonly AdsBDemodulator Demod = new();
    private static FeatureVector Features() => new(1_090_000_000, 0, 0, 0, 0, 0, 0, null);

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    [Fact]
    public void Demodulate_CleanGoldenFrame_RecoversExactBytes()
    {
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.0);

        var frames = Demod.Demodulate(block, Features()).ToList();

        var got = Assert.Single(frames);
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }

    [Fact]
    public void Demodulate_ThroughRealDecoder_YieldsIcaoAndCallsign()
    {
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.05);

        var frames = Demod.Demodulate(block, Features()).ToList();
        var outcome = new AdsBDecoder().Decode(Assert.Single(frames));

        Assert.True(outcome.Success);
        Assert.Equal("4840D6", outcome.Frame!.Identifiers["icao"]);
        Assert.Equal("KLM1023", outcome.Frame.Identifiers["callsign"]);
    }

    [Fact]
    public void Demodulate_UnderModerateNoise_StillRecoversExactBytes()
    {
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.1, seed: 3);

        var got = Assert.Single(Demod.Demodulate(block, Features()).ToList());
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }

    [Fact]
    public void Demodulate_PureNoise_YieldsNothing()
    {
        // amp:0 → no pulses, only seeded noise.
        var block = AdsBModulator.Modulate(new byte[14], amp: 0.0, noiseSigma: 1.0, seed: 99);

        Assert.Empty(Demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_ManyPureNoiseBlocks_ProduceNoDecodedAircraft()
    {
        // The preamble gate is a deliberately-loose pre-filter; the CRC-24 in AdsBDecoder is the
        // real validator. The guarantee that matters end-to-end: pure noise yields no DECODED frame.
        // Assert it across many seeds (a statistical bound, not a single-seed point check).
        var decoder = new AdsBDecoder();
        int survivors = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var block = AdsBModulator.Modulate(new byte[14], amp: 0.0, noiseSigma: 1.0, seed: seed);
            foreach (var frame in Demod.Demodulate(block, Features()))
                if (decoder.Decode(frame).Success)
                    survivors++;
        }
        Assert.Equal(0, survivors);
    }

    [Fact]
    public void Demodulate_OffFrequencyBlock_YieldsNothing_SelfGate()
    {
        // Frame present, but tuned to 915 MHz @ 2 MS/s → 1090 MHz not in band.
        var block = AdsBModulator.Modulate(Hex(GoldenHex), centerFreqHz: 915_000_000);

        Assert.Empty(Demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_NonEvenMhzSampleRate_YieldsNothing()
    {
        // 3 MS/s is not an even number of MHz → slot math can't align → clean no-op, not garbage.
        var block = AdsBModulator.Modulate(Hex(GoldenHex), sampleRateHz: 3_000_000);
        Assert.Empty(Demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_TwoFramesInOneBlock_RecoversBoth()
    {
        // Concatenate two modulated frames into one block's I/Q.
        var a = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 2, trailSlots: 2);
        var b = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 2, trailSlots: 2);
        var i = a.I.Concat(b.I).ToArray();
        var q = a.Q.Concat(b.Q).ToArray();
        var block = new IqBlock(1_090_000_000, 2_000_000, i, q);

        var frames = Demod.Demodulate(block, Features()).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(Hex(GoldenHex), f.ToArray()));
    }
}
