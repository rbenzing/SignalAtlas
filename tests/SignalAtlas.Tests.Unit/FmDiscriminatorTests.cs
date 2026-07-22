using SignalAtlas.Domain;
using SignalAtlas.Processing;
using Xunit;

public class FmDiscriminatorTests
{
    // Synthesize IQ whose instantaneous frequency is a constant toneHz offset: phase advances by
    // 2*pi*toneHz/fs each sample. The discriminator output should be ~constant = 2*pi*toneHz/fs.
    private static IqBlock FmTone(double toneHz, int fs, int n)
    {
        var i = new float[n]; var q = new float[n];
        double ph = 0, step = 2 * Math.PI * toneHz / fs;
        for (int k = 0; k < n; k++) { i[k] = (float)Math.Cos(ph); q[k] = (float)Math.Sin(ph); ph += step; }
        return new IqBlock(137_100_000, fs, i, q);
    }

    [Fact]
    public void Process_ConstantToneFrequency_ProducesConstantAudioLevel()
    {
        var d = new FmDiscriminator(100);
        var audio = d.Process(FmTone(3000, 2_000_000, 200_000));
        Assert.Equal(2000, audio.Length);           // 200000/100
        double expected = 2 * Math.PI * 3000 / 2_000_000;
        double mean = audio.Skip(5).Take(1990).Average();
        Assert.InRange(mean, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void Process_IsDeterministic()
    {
        var block = FmTone(3000, 2_000_000, 100_000);
        Assert.Equal(new FmDiscriminator(100).Process(block), new FmDiscriminator(100).Process(block));
    }
}
