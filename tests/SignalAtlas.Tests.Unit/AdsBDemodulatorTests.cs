using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class AdsBDemodulatorTests
{
    private const string GoldenHex = "8D4840D6202CC371C32CE0576098";
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
        var demod = new AdsBDemodulator();
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.0);

        var frames = demod.Demodulate(block, Features()).ToList();

        var got = Assert.Single(frames);
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }

    [Fact]
    public void Demodulate_ThroughRealDecoder_YieldsIcaoAndCallsign()
    {
        var demod = new AdsBDemodulator();
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.05);

        var frames = demod.Demodulate(block, Features()).ToList();
        var outcome = new AdsBDecoder().Decode(Assert.Single(frames));

        Assert.True(outcome.Success);
        Assert.Equal("4840D6", outcome.Frame!.Identifiers["icao"]);
        Assert.Equal("KLM1023", outcome.Frame.Identifiers["callsign"]);
    }

    [Fact]
    public void Demodulate_UnderModerateNoise_StillRecoversExactBytes()
    {
        var demod = new AdsBDemodulator();
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.1, seed: 3);

        var got = Assert.Single(demod.Demodulate(block, Features()).ToList());
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }

    [Fact]
    public void Demodulate_PureNoise_YieldsNothing()
    {
        // amp:0 → no pulses, only seeded noise.
        var demod = new AdsBDemodulator();
        var block = AdsBModulator.Modulate(new byte[14], amp: 0.0, noiseSigma: 1.0, seed: 99);

        Assert.Empty(demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_ManyPureNoiseBlocks_ProduceNoDecodedAircraft()
    {
        // The preamble gate is a deliberately-loose pre-filter; the CRC-24 in AdsBDecoder is the
        // real validator. The guarantee that matters end-to-end: pure noise yields no DECODED frame.
        // Assert it across many seeds (a statistical bound, not a single-seed point check).
        var demod = new AdsBDemodulator();
        var decoder = new AdsBDecoder();
        int survivors = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var block = AdsBModulator.Modulate(new byte[14], amp: 0.0, noiseSigma: 1.0, seed: seed);
            foreach (var frame in demod.Demodulate(block, Features()))
                if (decoder.Decode(frame).Success)
                    survivors++;
        }
        Assert.Equal(0, survivors);
    }

    [Fact]
    public void Demodulate_OffFrequencyBlock_YieldsNothing_SelfGate()
    {
        // Frame present, but tuned to 915 MHz @ 2 MS/s → 1090 MHz not in band.
        var demod = new AdsBDemodulator();
        var block = AdsBModulator.Modulate(Hex(GoldenHex), centerFreqHz: 915_000_000);

        Assert.Empty(demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_NonEvenMhzSampleRate_YieldsNothing()
    {
        // 3 MS/s is not an even number of MHz → slot math can't align → clean no-op, not garbage.
        var demod = new AdsBDemodulator();
        var block = AdsBModulator.Modulate(Hex(GoldenHex), sampleRateHz: 3_000_000);
        Assert.Empty(demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_TwoFramesInOneBlock_RecoversBoth()
    {
        // Concatenate two modulated frames into one block's I/Q.
        var demod = new AdsBDemodulator();
        var a = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 2, trailSlots: 2);
        var b = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 2, trailSlots: 2);
        var i = a.I.Concat(b.I).ToArray();
        var q = a.Q.Concat(b.Q).ToArray();
        var block = new IqBlock(1_090_000_000, 2_000_000, i, q);

        var frames = demod.Demodulate(block, Features()).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(Hex(GoldenHex), f.ToArray()));
    }

    [Fact]
    public void Demodulate_PreambleSplitNearBlockEnd_EmittedExactlyOnceOnSecondBlock()
    {
        // Synthesize one frame's full contiguous IQ with a long lead-in (well over one frame's worth
        // of samples) so the preamble starts near the END of a long block 1, and split there — 5
        // slots (samples, at 2 MS/s where hus=1) into the 16-slot preamble. Block 1 alone therefore
        // contains only a fragment of the preamble (no full 240-slot frame window can start inside
        // it), and block 2 alone starts mid-preamble (missing the first 5 slots), so a per-block
        // scanner with no carry drops the frame entirely on BOTH sides of the split. The streaming
        // demod must retain block 1's unconsumed tail (bounded: < one frame's samples) and, once
        // combined with block 2, emit the frame exactly once.
        const int sampleRateHz = 2_000_000; // hus = 1 → slots and samples coincide.
        const int leadSlots = 300;          // > FrameSlots (240): forces the retained carry to be
                                             // large but still strictly < one frame's samples.
        const int trailSlots = 20;
        var full = AdsBModulator.Modulate(Hex(GoldenHex), sampleRateHz: sampleRateHz, leadSlots: leadSlots, trailSlots: trailSlots);

        int splitAt = leadSlots + 5; // 5 samples into the 16-slot preamble.
        var block1 = new IqBlock(1_090_000_000, sampleRateHz, full.I[..splitAt], full.Q[..splitAt]);
        var block2 = new IqBlock(1_090_000_000, sampleRateHz, full.I[splitAt..], full.Q[splitAt..]);

        var demod = new AdsBDemodulator();
        var fromBlock1 = demod.Demodulate(block1, Features()).ToList();
        var fromBlock2 = demod.Demodulate(block2, Features()).ToList();

        Assert.Empty(fromBlock1);
        var got = Assert.Single(fromBlock2);
        Assert.Equal(Hex(GoldenHex), got.ToArray());

        // Regression check: a per-block scanner with no carry (fresh instance per block, matching
        // the pre-fix behavior) finds nothing in EITHER half alone — the frame is silently dropped.
        var naive1 = new AdsBDemodulator().Demodulate(block1, Features()).ToList();
        var naive2 = new AdsBDemodulator().Demodulate(block2, Features()).ToList();
        Assert.Empty(naive1);
        Assert.Empty(naive2);
    }

    [Fact]
    public void Demodulate_FrameFullyInsideOneBlock_StillEmitsExactlyOnce_WithCarryStatePresent()
    {
        // Guard against double-emission at a block boundary: feed an empty-ish leading block (no
        // frame) then a block with a complete frame, on the SAME stateful instance. The frame must
        // still be emitted exactly once — the carry mechanism must not duplicate a frame that was
        // never split.
        var demod = new AdsBDemodulator();
        var empty = new IqBlock(1_090_000_000, 2_000_000, new float[400], new float[400]);
        Assert.Empty(demod.Demodulate(empty, Features()).ToList());

        var block = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 4, trailSlots: 4);
        var frames = demod.Demodulate(block, Features()).ToList();

        var got = Assert.Single(frames);
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }
}
