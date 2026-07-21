namespace SignalAtlas.Domain;

/// <summary>
/// A block of complex baseband samples produced by an <see cref="ISampleSource"/>.
/// I and Q are normalized to [-1, 1) from the source's native sample format.
/// </summary>
public sealed class IqBlock
{
    public IqBlock(long centerFreqHz, int sampleRateHz, float[] i, float[] q, ReceiverConfig? receiverConfig = null)
    {
        if (i.Length != q.Length)
            throw new ArgumentException("I and Q must have equal length.");
        CenterFreqHz = centerFreqHz;
        SampleRateHz = sampleRateHz;
        I = i;
        Q = q;
        ReceiverConfig = receiverConfig;
    }

    public long CenterFreqHz { get; }
    public int SampleRateHz { get; }
    public float[] I { get; }
    public float[] Q { get; }
    public int SampleCount => I.Length;

    /// <summary>The receiver configuration that shaped this capture; null when the source has no real
    /// receiver (file replay, synthetic) or the config is otherwise unknown.</summary>
    public ReceiverConfig? ReceiverConfig { get; }
}
