using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// TEST-ONLY inverse of <c>AdsBDemodulator</c>: renders a 14-byte Mode S extended squitter as
/// Mode S PPM IQ (amplitude in I, Q carries only optional noise). Deterministic (seeded RNG) so
/// fixtures are reproducible. NOT shipped — lives in the test project.
/// </summary>
public static class AdsBModulator
{
    private const int FrameBits = 112;
    private const int PreambleSlots = 16;                    // 8 µs = 16 half-µs slots
    private const int FrameSlots = PreambleSlots + FrameBits * 2; // 240
    private static readonly int[] PulseSlots = { 0, 2, 7, 9 };

    public static IqBlock Modulate(
        byte[] frame,
        int sampleRateHz = 2_000_000,
        long centerFreqHz = 1_090_000_000,
        int leadSlots = 4,
        int trailSlots = 4,
        double amp = 1.0,
        double noiseSigma = 0.0,
        int seed = 12345)
    {
        if (frame.Length != 14) throw new ArgumentException("frame must be 14 bytes", nameof(frame));
        int hus = (sampleRateHz / 1_000_000) / 2;
        if (hus < 1) throw new ArgumentException("sample rate must be >= 2 MS/s", nameof(sampleRateHz));

        int totalSlots = leadSlots + FrameSlots + trailSlots;
        int n = totalSlots * hus;
        var i = new float[n];
        var q = new float[n];

        int baseSlot = leadSlots;
        foreach (var s in PulseSlots) FillSlot(i, baseSlot + s, hus, amp);
        for (int b = 0; b < FrameBits; b++)
        {
            bool one = (frame[b >> 3] & (0x80 >> (b & 7))) != 0;
            int slot = baseSlot + PreambleSlots + 2 * b + (one ? 0 : 1);
            FillSlot(i, slot, hus, amp);
        }

        if (noiseSigma > 0) AddNoise(i, q, noiseSigma, seed);
        return new IqBlock(centerFreqHz, sampleRateHz, i, q);
    }

    private static void FillSlot(float[] i, int slot, int hus, double amp)
    {
        int start = slot * hus;
        for (int k = 0; k < hus; k++) i[start + k] = (float)amp;
    }

    private static void AddNoise(float[] i, float[] q, double sigma, int seed)
    {
        var rng = new Random(seed); // TEST-ONLY seeded RNG → deterministic fixtures.
        for (int k = 0; k < i.Length; k++)
        {
            i[k] += (float)(Gaussian(rng) * sigma);
            q[k] += (float)(Gaussian(rng) * sigma);
        }
    }

    private static double Gaussian(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
