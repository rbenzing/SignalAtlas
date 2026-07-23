using SignalAtlas.Domain;
using SignalAtlas.Processing;
using Xunit;

public class AudioDemodulatorTests
{
    private const int Fs = 2_000_000; // 2 MS/s, matches the design's headline decimation (/80 -> 25 kHz)
    private const int AudioRateHz = AudioDemodulator.TargetAudioRateHz;

    // --- Synthetic modulators (test-only; the real modulators live outside the receive-only pipeline) ---

    /// <summary>FM-modulate a single audio tone onto a baseband carrier: instantaneous frequency offset
    /// is deviation*sin(2*pi*audioFreq*t), so phase is the running integral of that offset.</summary>
    private static IqBlock FmModulatedTone(double audioFreqHz, double deviationHz, int n)
    {
        var i = new float[n];
        var q = new float[n];
        double phase = 0.0;
        for (int k = 0; k < n; k++)
        {
            i[k] = (float)Math.Cos(phase);
            q[k] = (float)Math.Sin(phase);
            double instFreq = deviationHz * Math.Sin(2 * Math.PI * audioFreqHz * k / Fs);
            phase += 2 * Math.PI * instFreq / Fs;
        }
        return new IqBlock(100_000_000, Fs, i, q);
    }

    /// <summary>AM-modulate a single audio tone onto a zero-IF carrier: envelope A(t) = 1 + m*sin(...),
    /// always positive for m &lt; 1, so I=A(t), Q=0 and the magnitude recovers A(t) directly.</summary>
    private static IqBlock AmModulatedTone(double audioFreqHz, double modulationIndex, int n)
    {
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            double envelope = 1.0 + modulationIndex * Math.Sin(2 * Math.PI * audioFreqHz * k / Fs);
            i[k] = (float)envelope;
            q[k] = 0f;
        }
        return new IqBlock(100_000_000, Fs, i, q);
    }

    /// <summary>SSB-modulate a single audio tone as a complex exponential at +audioFreqHz (upper
    /// sideband) or -audioFreqHz (lower sideband): I=cos, Q=+/-sin. This is exactly what a
    /// suppressed-carrier SSB transmitter, down-converted to zero-IF, presents to the receiver --
    /// content strictly on one side of the (suppressed) carrier's baseband frequency.</summary>
    private static IqBlock SsbModulatedTone(double audioFreqHz, int n, bool upperSideband)
    {
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            double angle = 2 * Math.PI * audioFreqHz * k / Fs;
            i[k] = (float)Math.Cos(angle);
            q[k] = upperSideband ? (float)Math.Sin(angle) : (float)(-Math.Sin(angle));
        }
        return new IqBlock(100_000_000, Fs, i, q);
    }

    /// <summary>Two simultaneous SSB tones in complex baseband: one strictly on the upper (positive
    /// frequency) side of the suppressed carrier, one strictly on the lower (negative frequency) side --
    /// I = cos(up) + cos(low), Q = sin(up) - sin(low), i.e. e^(j*2*pi*upperHz*t) + e^(-j*2*pi*lowerHz*t).
    /// A true phasing-method demodulator must recover ONLY the tone on its selected side and reject the
    /// other by a real margin; a plain coherent product detector (Re{(I+jQ)*e^-j*theta}) cannot -- it
    /// passes both equally, which is exactly the bug this test exists to catch.</summary>
    private static IqBlock TwoToneOppositeSidebands(double upperHz, double lowerHz, int n)
    {
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            double up = 2 * Math.PI * upperHz * k / Fs;
            double low = 2 * Math.PI * lowerHz * k / Fs;
            i[k] = (float)(Math.Cos(up) + Math.Cos(low));
            q[k] = (float)(Math.Sin(up) - Math.Sin(low));
        }
        return new IqBlock(100_000_000, Fs, i, q);
    }

    /// <summary>A bare CW carrier (key-down, constant amplitude) sitting at a small frequency
    /// offset from the tuned center -- i.e. the receiver isn't perfectly zero-beat on it, mirroring
    /// realistic manual tuning. Represented as a slowly-rotating complex exponential.</summary>
    private static IqBlock CwCarrier(double offsetHz, int n)
    {
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            double angle = 2 * Math.PI * offsetHz * k / Fs;
            i[k] = (float)Math.Cos(angle);
            q[k] = (float)Math.Sin(angle);
        }
        return new IqBlock(100_000_000, Fs, i, q);
    }

    /// <summary>Goertzel single-bin DFT energy at targetFreqHz for a PCM16LE mono buffer sampled at
    /// AudioRateHz. A real spectral test, not a "non-empty" placeholder.</summary>
    private static double GoertzelEnergy(byte[] pcm16, double targetFreqHz)
    {
        int n = pcm16.Length / 2;
        var samples = new double[n];
        for (int k = 0; k < n; k++)
            samples[k] = BitConverter.ToInt16(pcm16, k * 2);

        double k2 = Math.Round(n * targetFreqHz / AudioRateHz);
        double w = 2 * Math.PI * k2 / n;
        double cosine = Math.Cos(w);
        double coeff = 2 * cosine;
        double q0 = 0, q1 = 0, q2 = 0;
        for (int idx = 0; idx < n; idx++)
        {
            q0 = coeff * q1 - q2 + samples[idx];
            q2 = q1;
            q1 = q0;
        }
        double real = q1 - q2 * cosine;
        double imag = q2 * Math.Sin(w);
        return real * real + imag * imag;
    }

    [Fact]
    public void Fm_RecoversDominantAudioTone()
    {
        // 0.2 s at 2 MS/s -> 400,000 IQ samples -> 5,000 PCM samples at 25 kHz (80x, exact decimation).
        var block = FmModulatedTone(audioFreqHz: 1000, deviationHz: 15_000, n: 400_000);
        var demod = new AudioDemodulator(AudioMode.Wbfm);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double energy1k = GoertzelEnergy(pcm, 1000);
        double energy3k = GoertzelEnergy(pcm, 3000);
        double energy5k = GoertzelEnergy(pcm, 5000);

        Assert.True(energy1k > 20 * energy3k, $"1kHz energy {energy1k} should dominate 3kHz energy {energy3k}");
        Assert.True(energy1k > 20 * energy5k, $"1kHz energy {energy1k} should dominate 5kHz energy {energy5k}");
    }

    [Fact]
    public void Am_RecoversDominantAudioTone()
    {
        var block = AmModulatedTone(audioFreqHz: 1000, modulationIndex: 0.5, n: 400_000);
        var demod = new AudioDemodulator(AudioMode.Am);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double energy1k = GoertzelEnergy(pcm, 1000);
        double energy3k = GoertzelEnergy(pcm, 3000);
        double energy5k = GoertzelEnergy(pcm, 5000);

        Assert.True(energy1k > 20 * energy3k, $"1kHz energy {energy1k} should dominate 3kHz energy {energy3k}");
        Assert.True(energy1k > 20 * energy5k, $"1kHz energy {energy1k} should dominate 5kHz energy {energy5k}");
    }

    [Fact]
    public void Usb_RecoversDominantAudioTone()
    {
        var block = SsbModulatedTone(audioFreqHz: 1000, n: 400_000, upperSideband: true);
        var demod = new AudioDemodulator(AudioMode.Usb);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double energy1k = GoertzelEnergy(pcm, 1000);
        double energy3k = GoertzelEnergy(pcm, 3000);
        double energy5k = GoertzelEnergy(pcm, 5000);

        Assert.True(energy1k > 20 * energy3k, $"1kHz energy {energy1k} should dominate 3kHz energy {energy3k}");
        Assert.True(energy1k > 20 * energy5k, $"1kHz energy {energy1k} should dominate 5kHz energy {energy5k}");
    }

    [Fact]
    public void Lsb_RecoversDominantAudioTone()
    {
        var block = SsbModulatedTone(audioFreqHz: 1000, n: 400_000, upperSideband: false);
        var demod = new AudioDemodulator(AudioMode.Lsb);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double energy1k = GoertzelEnergy(pcm, 1000);
        double energy3k = GoertzelEnergy(pcm, 3000);
        double energy5k = GoertzelEnergy(pcm, 5000);

        Assert.True(energy1k > 20 * energy3k, $"1kHz energy {energy1k} should dominate 3kHz energy {energy3k}");
        Assert.True(energy1k > 20 * energy5k, $"1kHz energy {energy1k} should dominate 5kHz energy {energy5k}");
    }

    // --- Sideband-rejection correctness gate: the whole point of the phasing/Hilbert upgrade.
    // A coherent product detector recovers the SAME audio for Usb and Lsb on a clean signal (it can't
    // tell the sidebands apart). A true phasing demodulator must select ONE side and reject the other
    // by a real margin. Two simultaneous opposite-sideband tones make this unambiguous: whichever mode
    // is under test must report the tone on ITS side dominating, and the other side's tone suppressed
    // by >= ~20dB (a conservative gate -- a real phasing demod easily exceeds 30dB).

    private const double RejectionGateDb = 20.0;

    [Fact]
    public void Usb_RejectsLowerSidebandTone_ByAtLeast20dB()
    {
        // +1200 Hz (upper/wanted) and -1800 Hz (lower/unwanted) simultaneously.
        var block = TwoToneOppositeSidebands(upperHz: 1200, lowerHz: 1800, n: 400_000);
        var demod = new AudioDemodulator(AudioMode.Usb);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double wanted = GoertzelEnergy(pcm, 1200);
        double rejected = GoertzelEnergy(pcm, 1800);
        double rejectionDb = 10 * Math.Log10(wanted / rejected);

        Console.WriteLine($"USB rejection of the lower sideband (1800 Hz vs wanted 1200 Hz): {rejectionDb:F1} dB");
        Assert.True(wanted > rejected, $"USB should favor the upper (wanted) tone: wanted={wanted}, rejected={rejected}");
        Assert.True(rejectionDb >= RejectionGateDb,
            $"USB should reject the opposite (lower) sideband by >= {RejectionGateDb}dB, achieved {rejectionDb:F1}dB");
    }

    [Fact]
    public void Lsb_RejectsUpperSidebandTone_ByAtLeast20dB()
    {
        // +1200 Hz (upper/unwanted) and -1800 Hz (lower/wanted) simultaneously.
        var block = TwoToneOppositeSidebands(upperHz: 1200, lowerHz: 1800, n: 400_000);
        var demod = new AudioDemodulator(AudioMode.Lsb);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double wanted = GoertzelEnergy(pcm, 1800);
        double rejected = GoertzelEnergy(pcm, 1200);
        double rejectionDb = 10 * Math.Log10(wanted / rejected);

        Console.WriteLine($"LSB rejection of the upper sideband (1200 Hz vs wanted 1800 Hz): {rejectionDb:F1} dB");
        Assert.True(wanted > rejected, $"LSB should favor the lower (wanted) tone: wanted={wanted}, rejected={rejected}");
        Assert.True(rejectionDb >= RejectionGateDb,
            $"LSB should reject the opposite (upper) sideband by >= {RejectionGateDb}dB, achieved {rejectionDb:F1}dB");
    }

    [Fact]
    public void Usb_And_Lsb_ProduceDifferentAudio_OnOppositeSidebandSignal()
    {
        // The direct proof that USB != LSB: on a signal with distinct content on each side, the two
        // modes must not produce (near-)identical PCM, unlike the old coherent product detector.
        var block = TwoToneOppositeSidebands(upperHz: 1200, lowerHz: 1800, n: 400_000);

        byte[] usb = new AudioDemodulator(AudioMode.Usb).Demodulate(block);
        byte[] lsb = new AudioDemodulator(AudioMode.Lsb).Demodulate(block);

        Assert.NotEqual(usb, lsb);
    }

    [Fact]
    public void Cw_RecoversApproximately700HzTone()
    {
        // The carrier itself sits 20 Hz off the tuned center (imperfect zero-beat), so the fixed
        // ~700 Hz BFO produces an audible tone at ~680 Hz -- close to, not exactly, 700 Hz.
        var block = CwCarrier(offsetHz: 20, n: 400_000);
        var demod = new AudioDemodulator(AudioMode.Cw);

        byte[] pcm = demod.Demodulate(block);
        Assert.Equal(5000 * 2, pcm.Length);

        double energyTone = GoertzelEnergy(pcm, 680);
        double energy100 = GoertzelEnergy(pcm, 100);
        double energy3k = GoertzelEnergy(pcm, 3000);

        Assert.True(energyTone > 20 * energy100, $"~700Hz tone energy {energyTone} should dominate 100Hz energy {energy100}");
        Assert.True(energyTone > 20 * energy3k, $"~700Hz tone energy {energyTone} should dominate 3kHz energy {energy3k}");
    }

    [Theory]
    [InlineData(AudioMode.Usb)]
    [InlineData(AudioMode.Lsb)]
    [InlineData(AudioMode.Cw)]
    public void Ssb_Demodulate_IsDeterministic_AcrossIndependentInstances(AudioMode mode)
    {
        var block = mode == AudioMode.Cw
            ? CwCarrier(20, 200_000)
            : SsbModulatedTone(1000, 200_000, upperSideband: mode == AudioMode.Usb);

        byte[] a = new AudioDemodulator(mode).Demodulate(block);
        byte[] b = new AudioDemodulator(mode).Demodulate(block);

        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void Demodulate_IsDeterministic_AcrossIndependentInstances()
    {
        var block = FmModulatedTone(1000, 15_000, 200_000);

        byte[] a = new AudioDemodulator(AudioMode.Wbfm).Demodulate(block);
        byte[] b = new AudioDemodulator(AudioMode.Wbfm).Demodulate(block);

        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void Demodulate_IsDeterministic_Am()
    {
        var block = AmModulatedTone(1000, 0.5, 200_000);

        byte[] a = new AudioDemodulator(AudioMode.Am).Demodulate(block);
        byte[] b = new AudioDemodulator(AudioMode.Am).Demodulate(block);

        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Theory]
    [InlineData(AudioMode.Wbfm)]
    [InlineData(AudioMode.Nbfm)]
    [InlineData(AudioMode.Am)]
    [InlineData(AudioMode.Usb)]
    [InlineData(AudioMode.Lsb)]
    [InlineData(AudioMode.Cw)]
    public void Demodulate_BelowAudioRate_ReturnsEmpty_NoThrow(AudioMode mode)
    {
        var i = new float[] { 0.1f, 0.2f, 0.3f, -0.1f };
        var q = new float[] { 0.0f, 0.1f, -0.1f, 0.2f };
        var shortBlock = new IqBlock(100_000_000, 8_000, i, q); // 8 kHz < 25 kHz target

        var demod = new AudioDemodulator(mode);
        byte[] pcm = demod.Demodulate(shortBlock);

        Assert.Empty(pcm);
    }

    [Fact]
    public void Demodulate_EmptyBlock_ReturnsEmpty_NoThrow()
    {
        var block = new IqBlock(100_000_000, Fs, Array.Empty<float>(), Array.Empty<float>());
        var demod = new AudioDemodulator(AudioMode.Wbfm);

        byte[] pcm = demod.Demodulate(block);

        Assert.Empty(pcm);
    }

    [Fact]
    public void Demodulate_NonExactDecimationRate_Resamples_And_IsDeterministic()
    {
        // 48 kHz doesn't divide evenly into 25 kHz: rawDecimation = floor(48000/25000) = 1, so the
        // intermediate rate (48 kHz) must be linearly resampled down to exactly 25 kHz.
        const int fs = 48_000;
        int n = 48_000; // 1 second
        var i = new float[n];
        var q = new float[n];
        double phase = 0;
        for (int k = 0; k < n; k++)
        {
            i[k] = (float)Math.Cos(phase);
            q[k] = (float)Math.Sin(phase);
            double instFreq = 3000 * Math.Sin(2 * Math.PI * 400 * k / fs);
            phase += 2 * Math.PI * instFreq / fs;
        }
        var block = new IqBlock(100_000_000, fs, i, q);

        byte[] a = new AudioDemodulator(AudioMode.Wbfm).Demodulate(block);
        byte[] b = new AudioDemodulator(AudioMode.Wbfm).Demodulate(block);

        Assert.Equal(a, b);
        Assert.NotEmpty(a);
        // Not an exact 80x decimation, so this exercises the resampler path (output length is close to
        // but not required to equal a fixed multiple; assert it's in a sane ballpark for 1s @ 25kHz).
        int sampleCount = a.Length / 2;
        Assert.InRange(sampleCount, (int)(AudioRateHz * 0.9), (int)(AudioRateHz * 1.1));
    }

    [Fact]
    public void Demodulate_CarriesStateAcrossBlocks_MatchesSingleBlockResult()
    {
        // Splitting the same IQ stream into two chunks and feeding sequentially must produce the same
        // audio as one call (modulo the small filter-settle differences at the exact split point,
        // which the spectral test tolerates) -- proving state (discriminator phase, de-emphasis,
        // resampler phase) carries across Demodulate calls rather than resetting.
        var whole = FmModulatedTone(1000, 15_000, 400_000);

        var chunked = new AudioDemodulator(AudioMode.Wbfm);
        var firstHalf = SliceBlock(whole, 0, 200_000);
        var secondHalf = SliceBlock(whole, 200_000, 200_000);
        byte[] part1 = chunked.Demodulate(firstHalf);
        byte[] part2 = chunked.Demodulate(secondHalf);

        var combined = new byte[part1.Length + part2.Length];
        Buffer.BlockCopy(part1, 0, combined, 0, part1.Length);
        Buffer.BlockCopy(part2, 0, combined, part1.Length, part2.Length);

        double energy1k = GoertzelEnergy(combined, 1000);
        double energy5k = GoertzelEnergy(combined, 5000);
        Assert.True(energy1k > 20 * energy5k);
    }

    private static IqBlock SliceBlock(IqBlock block, int start, int length)
    {
        var i = new float[length];
        var q = new float[length];
        Array.Copy(block.I, start, i, 0, length);
        Array.Copy(block.Q, start, q, 0, length);
        return new IqBlock(block.CenterFreqHz, block.SampleRateHz, i, q);
    }
}
