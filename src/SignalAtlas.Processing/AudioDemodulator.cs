using SignalAtlas.Domain;

namespace SignalAtlas.Processing;

/// <summary>Audio demodulation mode selectable per audio session (RF Audio Player design §2/§3).</summary>
public enum AudioMode { Wbfm, Nbfm, Am }

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

    private readonly AudioMode _mode;

    private int _configuredSampleRateHz = -1;
    private int _rawDecimation;
    private double _resampleRatio;   // intermediate-rate / TargetAudioRateHz, always >= 1
    private double _deemphAlpha;     // FM one-pole de-emphasis coefficient at the intermediate rate

    private FmDiscriminator? _fm;
    private double _deemphState;
    private double _dcBlockPrevIn;
    private double _dcBlockPrevOut;

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

        float[] intermediate = _mode == AudioMode.Am
            ? DemodulateAmToIntermediate(block)
            : DemodulateFmToIntermediate(block);

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

        _fm = _mode == AudioMode.Am ? null : new FmDiscriminator(_rawDecimation);

        double tau = _mode == AudioMode.Wbfm ? WbfmDeemphTauSeconds : NbfmDeemphTauSeconds;
        double dt = 1.0 / intermediateRate;
        _deemphAlpha = dt / (tau + dt);

        _deemphState = 0.0;
        _dcBlockPrevIn = 0.0;
        _dcBlockPrevOut = 0.0;
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
        double scale = _mode == AudioMode.Am ? AmScale : FmScale;
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
