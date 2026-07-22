using SignalAtlas.Domain;

namespace SignalAtlas.Processing;

/// <summary>
/// FM-discriminates an <see cref="IqBlock"/> into decimated audio samples: the instantaneous
/// frequency (phase difference of successive complex samples), box-average-decimated by
/// <see cref="Decimation"/>. Carries the last sample's phase across calls so block boundaries
/// don't drop a sample. Deterministic: same input bytes always produce the same output.
/// </summary>
public sealed class FmDiscriminator
{
    private double _lastPhase;

    public FmDiscriminator(int decimation)
    {
        Decimation = decimation;
        _lastPhase = 0.0;
    }

    public int Decimation { get; }

    /// <summary>FM-discriminate this block (instantaneous frequency = phase difference of successive
    /// complex samples) then box-average-decimate by Decimation. Carries the last sample's phase
    /// across calls so block boundaries don't drop a sample. Deterministic; returns audio samples
    /// (radians/sample, roughly [-pi, pi]).</summary>
    public float[] Process(IqBlock block)
    {
        int n = block.SampleCount;
        var discriminated = new double[n];
        double prevPhase = _lastPhase;

        for (int k = 0; k < n; k++)
        {
            double phase = Math.Atan2(block.Q[k], block.I[k]);
            discriminated[k] = WrapToPi(phase - prevPhase);
            prevPhase = phase;
        }

        _lastPhase = prevPhase;

        int outLen = n / Decimation;
        var audio = new float[outLen];
        for (int m = 0; m < outLen; m++)
        {
            double sum = 0.0;
            int start = m * Decimation;
            for (int j = 0; j < Decimation; j++)
                sum += discriminated[start + j];
            audio[m] = (float)(sum / Decimation);
        }

        return audio;
    }

    private static double WrapToPi(double x)
    {
        while (x <= -Math.PI) x += 2 * Math.PI;
        while (x > Math.PI) x -= 2 * Math.PI;
        return x;
    }
}
