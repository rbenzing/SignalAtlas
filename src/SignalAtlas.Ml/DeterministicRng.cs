namespace SignalAtlas.Ml;

/// <summary>
/// Small deterministic PRNG (SplitMix64) with a Gaussian draw via Box–Muller. Purpose-built so the
/// ML layer NEVER touches <see cref="System.Random"/> without a seed, <c>DateTime</c>, or any other
/// non-deterministic source (SPEC §6.1 P5). Same seed → identical sequence, on every OS/run.
/// </summary>
public sealed class DeterministicRng
{
    private ulong _state;
    private double? _spareGaussian;

    public DeterministicRng(ulong seed) => _state = seed;

    private ulong NextUInt64()
    {
        // SplitMix64 — a well-known, fast, fully-deterministic mixing generator.
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0,1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        var span = (ulong)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt64() % span);
    }

    /// <summary>Standard normal draw (mean 0, std 1) via cached Box–Muller.</summary>
    public double NextGaussian()
    {
        if (_spareGaussian is { } spare)
        {
            _spareGaussian = null;
            return spare;
        }

        // u1 in (0,1] to keep Log finite.
        double u1 = 1.0 - NextDouble();
        double u2 = NextDouble();
        double mag = Math.Sqrt(-2.0 * Math.Log(u1));
        _spareGaussian = mag * Math.Sin(2.0 * Math.PI * u2);
        return mag * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Gaussian draw with the given mean and standard deviation.</summary>
    public double NextGaussian(double mean, double std) => mean + std * NextGaussian();
}
