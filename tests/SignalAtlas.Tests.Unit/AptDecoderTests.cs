using SignalAtlas.Decode;
using SignalAtlas.Domain;
using Xunit;

public class AptDecoderTests
{
    private static FeatureVector Features(long f) => new(f, 0, 0, 0, 0, 0, 0, null);

    private static byte[][] Gradient(int lines)
    {
        var rows = new byte[lines][];
        for (int r = 0; r < lines; r++) { rows[r] = new byte[2080]; for (int c = 0; c < 2080; c++) rows[r][c] = (byte)((c * 255) / 2079); }
        return rows;
    }

    [Fact]
    public void Accept_SyntheticPass_EmitsSatelliteDeviceWithImage()
    {
        var rows = Gradient(8);
        var block = AptModulator.Modulate(rows, centerFreqHz: 137_100_000, sampleRateHz: 2_000_000);
        var d = new AptDecoder();
        var pass = d.Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);

        Assert.NotNull(pass);
        Assert.Equal("Satellite", pass!.Device.DeviceType);
        Assert.Equal("NOAA-APT", pass.Device.Protocol);
        Assert.Equal("NOAA-19", pass.Device.PrimaryIdentifier);
        Assert.NotEmpty(pass.Device.Evidence);                 // P4/P6
        Assert.True(pass.Lines >= 4);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, pass.PngImage.Take(4).ToArray()); // PNG magic
    }

    [Fact]
    public void AppliesTo_OnlyNoaaAptCenters()
    {
        var d = new AptDecoder();
        Assert.True(d.AppliesTo(137_100_000));
        Assert.True(d.AppliesTo(137_912_500));
        Assert.False(d.AppliesTo(915_000_000));
    }

    [Fact]
    public void Accept_IsDeterministic()
    {
        var block = AptModulator.Modulate(Gradient(6));
        var a = new AptDecoder().Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);
        var b = new AptDecoder().Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);
        Assert.Equal(a!.PngImage, b!.PngImage);        // byte-identical (P5)
        Assert.Equal(a.Device.Id, b.Device.Id);
    }
}
