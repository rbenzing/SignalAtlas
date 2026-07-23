using SignalAtlas.Domain;

namespace SignalAtlas.Processing;

/// <summary>Audio demodulation mode selectable per audio session (RF Audio Player design §2/§3).
/// Usb/Lsb = single-sideband voice; Cw = Morse tone (SSB/USB with a fixed BFO offset).</summary>
public enum AudioMode { Wbfm, Nbfm, Am, Usb, Lsb, Cw }

/// <summary>Stateful per-session IQ->audio demodulator. Deterministic pure DSP. Output is PCM16
/// mono little-endian at AudioRateHz (25 kHz). Receive-only: reads only I/Q.</summary>
public interface IAudioDemodulator
{
    AudioMode Mode { get; }
    int AudioRateHz { get; }               // 25_000
    /// <summary>Demodulate one IQ block -> PCM16LE mono samples (may be empty for a short block).</summary>
    byte[] Demodulate(IqBlock block);
}

/// <summary>
/// IQ -&gt; PCM16 mono audio at a fixed 25 kHz output rate (RF Audio Player design §2/§3/§6).
/// One instance is bound to one <see cref="AudioMode"/> for the lifetime of an audio session and
/// carries all DSP state (the <see cref="FmDiscriminator"/>'s phase, the de-emphasis/DC-blocker
/// filter state, and the fractional resampler phase) across successive <see cref="Demodulate"/>
/// calls so block boundaries in the underlying IQ stream produce seamless audio rather than clicks
/// -- the same carry pattern <see cref="FmDiscriminator"/> itself already uses.
///
/// Pipeline (design §2):
///   WBFM/NBFM: FmDiscriminator (box-decimate) -&gt; one-pole de-emphasis -&gt; resample to 25 kHz -&gt; PCM16
///   AM:        |I+jQ| envelope -&gt; box-decimate -&gt; one-pole DC-blocker -&gt; resample to 25 kHz -&gt; PCM16
///   USB/LSB:   box-decimate I/Q (complex) -&gt; TRUE phasing (Hartley) method: an odd-length,
///              windowed FIR (<see cref="HilbertTaps"/>) applies a 90-degree phase shift to Q across
///              the audio band, and the I path is delayed by the filter's group delay
///              (<see cref="HilbertGroupDelay"/> samples, a plain shift register) so the two stay
///              time-aligned; both delay lines carry their state across <see cref="Demodulate"/>
///              calls. USB = I_delayed - H{Q} keeps only the positive (upper) sideband; LSB =
///              I_delayed + H{Q} keeps only the negative (lower) sideband -- unlike a coherent
///              product detector (Re{(I+jQ)*e^-j*theta}), this genuinely rejects the opposite
///              sideband (see the two-tone rejection tests) -- then a one-pole highpass+lowpass
///              voice band (~300-3000 Hz) -&gt; resample to 25 kHz -&gt; PCM16.
///   CW:        the same phasing pipeline as USB, but the decimated I/Q is first complex-mixed by a
///              fixed ~700 Hz BFO (<see cref="CwBfoHz"/>, phase carried across calls) so a bare
///              carrier beats into an audible tone; that BFO shift lands the wanted beat note on the
///              negative-frequency side (for the small positive tuning offsets this exists to
///              correct), so CW uses the same combiner sign as LSB internally -- still logically
///              "the USB pipeline plus a BFO", just the sign that actually recovers the tone -- and a
///              narrower passband centred on it.
///
/// Decimation: the raw decimation factor is floor(SampleRateHz / 25_000) (so 2 MS/s -&gt; exactly 80,
/// landing on 25 kHz with no further resampling needed). When that floor doesn't divide evenly, the
/// intermediate rate (SampleRateHz / rawDecimation) is always &gt;= 25 kHz and is brought down to exactly
/// 25 kHz by a deterministic linear resampler that carries its fractional phase (and the input tail
/// needed for interpolation) across block boundaries.
///
/// Deterministic (P5): pure arithmetic over I/Q, no wall clock, no randomness -- same bytes in
/// produce byte-identical PCM16 out. Receive-only (L1): reads only <see cref="IqBlock.I"/> /
/// <see cref="IqBlock.Q"/>.
/// </summary>
public sealed class AudioDemodulator : IAudioDemodulator
{
    public const int TargetAudioRateHz = 25_000;

    // One-pole de-emphasis time constants. WBFM uses the broadcast-standard 75 us curve. NBFM uses a
    // much lighter (20 us, higher-corner) single-pole smoothing since narrowband voice channels don't
    // carry the broadcast pre-emphasis curve -- Phase 1 keeps this simple per the design doc note that
    // "NBFM vs WBFM differ only in de-emphasis/gain".
    private const double WbfmDeemphTauSeconds = 75e-6;
    private const double NbfmDeemphTauSeconds = 20e-6;

    // FmDiscriminator wraps its output to [-pi, pi] (WrapToPi), so scaling by 32767/pi maps the full
    // theoretical range onto int16 without ever needing to clip a well-formed FM signal.
    private const double FmScale = short.MaxValue / Math.PI;

    // AM: after DC-blocking, the audio is the AC envelope swing -- for realistic modulation depths
    // that's a fraction of the full [-1,1) I/Q magnitude range. This gain gives normal signals usable
    // headroom in the int16 range; the final clamp protects against outliers regardless.
    private const double AmScale = short.MaxValue / 0.25;

    // One-pole DC-blocker pole per the design doc: y[n] = x[n] - x[n-1] + 0.995*y[n-1].
    private const double AmDcBlockPole = 0.995;

    // SSB/CW: the product-detector output sits well within [-1,1] for a full-scale input tone, but
    // real signals rarely hit full scale -- this gain gives normal signals usable headroom while the
    // final clamp in ToPcm16 protects against outliers.
    private const double SsbScale = short.MaxValue * 0.9;

    // Voice SSB (USB/LSB) audio passband -- standard ham/HF voice bandwidth.
    private const double SsbVoiceHighpassHz = 300.0;
    private const double SsbVoiceLowpassHz = 3000.0;

    // CW: a narrow passband centred on the BFO tone so only the beat note (not band noise) comes
    // through, and the fixed BFO offset itself that turns a keyed carrier into an audible tone.
    private const double CwHighpassHz = 500.0;
    private const double CwLowpassHz = 900.0;
    private const double CwBfoHz = 700.0;

    // Phasing-method Hilbert transformer for genuine sideband rejection (USB/LSB/CW). Odd length,
    // windowed (Blackman -- very low sidelobes for a solid rejection margin) FIR: h[k] = 0 for even
    // k (relative to center), h[k] = (2/(pi*k)) * window[k] for odd k. Rate-independent (defined
    // purely in discrete-time samples relative to Nyquist), so it's precomputed once, deterministically,
    // regardless of the configured sample rate.
    private const int HilbertLength = 129;              // odd, within the 65-129 tap design range
    private const int HilbertGroupDelay = (HilbertLength - 1) / 2; // samples of delay the FIR introduces
    private static readonly double[] HilbertTaps = BuildHilbertTaps(HilbertLength);

    private static double[] BuildHilbertTaps(int length)
    {
        int center = (length - 1) / 2;
        var taps = new double[length];
        for (int n = 0; n < length; n++)
        {
            int k = n - center;
            if (k == 0 || k % 2 == 0)
            {
                taps[n] = 0.0;
                continue;
            }
            double ideal = 2.0 / (Math.PI * k);
            // Blackman window.
            double window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * n / (length - 1))
                + 0.08 * Math.Cos(4 * Math.PI * n / (length - 1));
            taps[n] = ideal * window;
        }
        return taps;
    }

    private readonly AudioMode _mode;

    private int _configuredSampleRateHz = -1;
    private int _rawDecimation;
    private double _resampleRatio;   // intermediate-rate / TargetAudioRateHz, always >= 1
    private double _deemphAlpha;     // FM one-pole de-emphasis coefficient at the intermediate rate

    private FmDiscriminator? _fm;
    private double _deemphState;
    private double _dcBlockPrevIn;
    private double _dcBlockPrevOut;

    // SSB/CW (USB/LSB/Cw) phasing-method + voice-band state.
    private double _ssbBfoHz;
    private double _ssbCombinerSign;   // -1 for Usb (I - H{Q}), +1 for Lsb and Cw (I + H{Q}); see the
                                        // class doc comment for why Cw needs the Lsb-sign combiner.
    private double _ssbPhase;          // Running BFO phase, carried across Demodulate calls.
    private double _ssbPhaseInc;
    private double _ssbHpAlpha;
    private double _ssbLpAlpha;
    private double _ssbHpPrevIn;
    private double _ssbHpPrevOut;
    private double _ssbLpState;
    private double[] _ssbQHistory = Array.Empty<double>();  // last (HilbertLength-1) mixed-Q samples
    private double[] _ssbIHistory = Array.Empty<double>();  // last HilbertGroupDelay mixed-I samples

    private float[] _resamplePending = Array.Empty<float>();
    private double _resamplePhase;

    public AudioDemodulator(AudioMode mode) => _mode = mode;

    public AudioMode Mode => _mode;
    public int AudioRateHz => TargetAudioRateHz;

    public byte[] Demodulate(IqBlock block)
    {
        if (block.SampleRateHz < TargetAudioRateHz || block.SampleCount == 0)
            return Array.Empty<byte>();

        EnsureConfigured(block.SampleRateHz);

        float[] intermediate = _mode switch
        {
            AudioMode.Am => DemodulateAmToIntermediate(block),
            AudioMode.Usb or AudioMode.Lsb or AudioMode.Cw => DemodulateSsbToIntermediate(block),
            _ => DemodulateFmToIntermediate(block),
        };

        float[] resampled = Resample(intermediate);
        return ToPcm16(resampled);
    }

    private void EnsureConfigured(int sampleRateHz)
    {
        if (sampleRateHz == _configuredSampleRateHz) return;

        _configuredSampleRateHz = sampleRateHz;
        _rawDecimation = Math.Max(1, sampleRateHz / TargetAudioRateHz);
        double intermediateRate = (double)sampleRateHz / _rawDecimation;
        _resampleRatio = intermediateRate / TargetAudioRateHz;

        bool isFm = _mode is AudioMode.Wbfm or AudioMode.Nbfm;
        _fm = isFm ? new FmDiscriminator(_rawDecimation) : null;

        double tau = _mode == AudioMode.Wbfm ? WbfmDeemphTauSeconds : NbfmDeemphTauSeconds;
        double dt = 1.0 / intermediateRate;
        _deemphAlpha = dt / (tau + dt);

        bool isSsb = _mode is AudioMode.Usb or AudioMode.Lsb or AudioMode.Cw;
        if (isSsb)
        {
            _ssbBfoHz = _mode == AudioMode.Cw ? CwBfoHz : 0.0;
            // Usb rejects the negative-frequency (lower) sideband via "-"; Lsb keeps it via "+".
            // Cw's fixed BFO shift lands the wanted beat note on the negative-frequency side (see the
            // class doc comment), so it needs the same "+" combiner as Lsb to recover it.
            _ssbCombinerSign = _mode == AudioMode.Usb ? -1.0 : 1.0;

            double hpHz = _mode == AudioMode.Cw ? CwHighpassHz : SsbVoiceHighpassHz;
            double lpHz = _mode == AudioMode.Cw ? CwLowpassHz : SsbVoiceLowpassHz;
            double hpTau = 1.0 / (2 * Math.PI * hpHz);
            _ssbHpAlpha = hpTau / (hpTau + dt);
            double lpTau = 1.0 / (2 * Math.PI * lpHz);
            _ssbLpAlpha = dt / (lpTau + dt);
            _ssbPhaseInc = 2 * Math.PI * _ssbBfoHz / intermediateRate;
        }

        _deemphState = 0.0;
        _dcBlockPrevIn = 0.0;
        _dcBlockPrevOut = 0.0;
        _ssbPhase = 0.0;
        _ssbHpPrevIn = 0.0;
        _ssbHpPrevOut = 0.0;
        _ssbLpState = 0.0;
        _ssbQHistory = new double[HilbertLength - 1];
        _ssbIHistory = new double[HilbertGroupDelay];
        _resamplePending = Array.Empty<float>();
        _resamplePhase = 0.0;
    }

    private float[] DemodulateFmToIntermediate(IqBlock block)
    {
        float[] discriminated = _fm!.Process(block);

        var audio = new float[discriminated.Length];
        double y = _deemphState;
        for (int i = 0; i < discriminated.Length; i++)
        {
            y += _deemphAlpha * (discriminated[i] - y);
            audio[i] = (float)y;
        }
        _deemphState = y;
        return audio;
    }

    private float[] DemodulateAmToIntermediate(IqBlock block)
    {
        int n = block.SampleCount;
        var magnitude = new double[n];
        for (int k = 0; k < n; k++)
        {
            double i = block.I[k], q = block.Q[k];
            magnitude[k] = Math.Sqrt(i * i + q * q);
        }

        int outLen = n / _rawDecimation;
        var decimated = new double[outLen];
        for (int m = 0; m < outLen; m++)
        {
            double sum = 0.0;
            int start = m * _rawDecimation;
            for (int j = 0; j < _rawDecimation; j++)
                sum += magnitude[start + j];
            decimated[m] = sum / _rawDecimation;
        }

        var audio = new float[outLen];
        double prevIn = _dcBlockPrevIn;
        double prevOut = _dcBlockPrevOut;
        for (int i = 0; i < outLen; i++)
        {
            double x = decimated[i];
            double y = x - prevIn + AmDcBlockPole * prevOut;
            audio[i] = (float)y;
            prevIn = x;
            prevOut = y;
        }
        _dcBlockPrevIn = prevIn;
        _dcBlockPrevOut = prevOut;

        return audio;
    }

    /// <summary>USB/LSB/CW: box-decimate complex I/Q, optionally complex-mix by a (possibly zero-Hz)
    /// BFO whose phase is carried across calls, then a TRUE phasing-method sideband selector -- an
    /// FIR Hilbert transform of the (mixed) Q against a matched delay of the (mixed) I, both carrying
    /// their delay-line state across calls -- and finally the one-pole highpass+lowpass voice-band
    /// filter (also state-carried). See the class doc comment for the combiner-sign convention.</summary>
    private float[] DemodulateSsbToIntermediate(IqBlock block)
    {
        int n = block.SampleCount;
        int outLen = n / _rawDecimation;
        var decI = new double[outLen];
        var decQ = new double[outLen];
        for (int m = 0; m < outLen; m++)
        {
            double sumI = 0.0, sumQ = 0.0;
            int start = m * _rawDecimation;
            for (int j = 0; j < _rawDecimation; j++)
            {
                sumI += block.I[start + j];
                sumQ += block.Q[start + j];
            }
            decI[m] = sumI / _rawDecimation;
            decQ[m] = sumQ / _rawDecimation;
        }

        // Complex BFO mix (identity when _ssbPhaseInc == 0, i.e. Usb/Lsb): mixedI + j*mixedQ =
        // (decI + j*decQ) * e^(-j*phase). For Cw this shifts a near-zero-offset carrier by the fixed
        // BFO so it beats at an audible tone.
        var mixedI = new double[outLen];
        var mixedQ = new double[outLen];
        double phase = _ssbPhase;
        for (int m = 0; m < outLen; m++)
        {
            double c = Math.Cos(phase);
            double s = Math.Sin(phase);
            mixedI[m] = decI[m] * c + decQ[m] * s;
            mixedQ[m] = decQ[m] * c - decI[m] * s;

            phase += _ssbPhaseInc;
            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
        }
        _ssbPhase = phase;

        // FIR Hilbert transform of mixedQ (causal -> introduces exactly HilbertGroupDelay samples of
        // delay), carrying the previous block's tail across calls so the convolution is seamless at
        // block boundaries.
        var extQ = new double[HilbertLength - 1 + outLen];
        Array.Copy(_ssbQHistory, extQ, HilbertLength - 1);
        Array.Copy(mixedQ, 0, extQ, HilbertLength - 1, outLen);

        var hilbertQ = new double[outLen];
        for (int m = 0; m < outLen; m++)
        {
            double acc = 0.0;
            int baseIdx = HilbertLength - 1 + m;
            for (int k = 0; k < HilbertLength; k++)
                acc += HilbertTaps[k] * extQ[baseIdx - k];
            hilbertQ[m] = acc;
        }
        _ssbQHistory = extQ[^(HilbertLength - 1)..];

        // Plain delay line on mixedI matching the Hilbert filter's group delay, so I and H{Q} stay
        // time-aligned.
        var extI = new double[HilbertGroupDelay + outLen];
        Array.Copy(_ssbIHistory, extI, HilbertGroupDelay);
        Array.Copy(mixedI, 0, extI, HilbertGroupDelay, outLen);
        _ssbIHistory = extI[^HilbertGroupDelay..];

        var audio = new float[outLen];
        double hpPrevIn = _ssbHpPrevIn;
        double hpPrevOut = _ssbHpPrevOut;
        double lpState = _ssbLpState;
        for (int m = 0; m < outLen; m++)
        {
            // Usb: I_delayed - H{Q} (keeps the positive/upper sideband). Lsb: I_delayed + H{Q} (keeps
            // the negative/lower sideband). Cw: same "+" as Lsb -- see the class doc comment.
            double raw = extI[m] + _ssbCombinerSign * hilbertQ[m];

            double hp = _ssbHpAlpha * (hpPrevOut + raw - hpPrevIn);
            hpPrevIn = raw;
            hpPrevOut = hp;

            lpState += _ssbLpAlpha * (hp - lpState);
            audio[m] = (float)lpState;
        }
        _ssbHpPrevIn = hpPrevIn;
        _ssbHpPrevOut = hpPrevOut;
        _ssbLpState = lpState;

        return audio;
    }

    /// <summary>Deterministic linear resampler from the intermediate rate to exactly
    /// <see cref="TargetAudioRateHz"/>, carrying its fractional phase and the input tail needed for
    /// interpolation across calls (mirrors <see cref="FmDiscriminator"/>'s phase-carry pattern).</summary>
    private float[] Resample(float[] input)
    {
        // Exact integer decimation (e.g. 2 MS/s / 80 = 25 kHz) already lands on the target rate --
        // skip interpolation entirely so it's a lossless passthrough, not just an approximation.
        if (Math.Abs(_resampleRatio - 1.0) < 1e-9)
            return input;

        float[] buffer;
        if (_resamplePending.Length > 0)
        {
            buffer = new float[_resamplePending.Length + input.Length];
            Array.Copy(_resamplePending, buffer, _resamplePending.Length);
            Array.Copy(input, 0, buffer, _resamplePending.Length, input.Length);
        }
        else
        {
            buffer = input;
        }

        if (buffer.Length == 0)
        {
            _resamplePending = Array.Empty<float>();
            return Array.Empty<float>();
        }

        var output = new List<float>(buffer.Length);
        double pos = _resamplePhase;
        while (true)
        {
            int idx = (int)Math.Floor(pos);
            int idxNext = idx + 1;
            if (idx < 0 || idxNext >= buffer.Length) break;

            double frac = pos - idx;
            output.Add((float)(buffer[idx] + (buffer[idxNext] - buffer[idx]) * frac));
            pos += _resampleRatio;
        }

        int keepFrom = Math.Clamp((int)Math.Floor(pos), 0, buffer.Length - 1);
        _resamplePending = buffer[keepFrom..];
        _resamplePhase = pos - keepFrom;

        return output.ToArray();
    }

    private byte[] ToPcm16(float[] audio)
    {
        double scale = _mode switch
        {
            AudioMode.Am => AmScale,
            AudioMode.Usb or AudioMode.Lsb or AudioMode.Cw => SsbScale,
            _ => FmScale,
        };
        var bytes = new byte[audio.Length * 2];
        for (int i = 0; i < audio.Length; i++)
        {
            double v = audio[i] * scale;
            int s = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            if (s > short.MaxValue) s = short.MaxValue;
            if (s < short.MinValue) s = short.MinValue;

            short sample = (short)s;
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }
}
