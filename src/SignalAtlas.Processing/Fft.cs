namespace SignalAtlas.Processing;

/// <summary>
/// Dependency-free, deterministic radix-2 Cooley-Tukey FFT (SPEC §8.2, P5). In-place,
/// decimation-in-time, iterative (no recursion). Length must be a power of two.
/// </summary>
internal static class Fft
{
    /// <summary>In-place complex FFT. re/im are the real/imag parts; length must be a power of two.</summary>
    public static void Forward(double[] re, double[] im)
    {
        int n = re.Length;
        if (n != im.Length) throw new ArgumentException("re and im must have equal length.");
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("length must be a power of two.");

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Butterflies.
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len; // forward transform
            double wReStep = Math.Cos(ang);
            double wImStep = Math.Sin(ang);
            for (int start = 0; start < n; start += len)
            {
                double wRe = 1.0, wIm = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = start + k;
                    int b = start + k + len / 2;
                    double tRe = re[b] * wRe - im[b] * wIm;
                    double tIm = re[b] * wIm + im[b] * wRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    double nwRe = wRe * wReStep - wIm * wImStep;
                    wIm = wRe * wImStep + wIm * wReStep;
                    wRe = nwRe;
                }
            }
        }
    }
}
