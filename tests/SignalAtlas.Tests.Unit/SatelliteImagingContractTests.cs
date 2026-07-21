using SignalAtlas.Domain;
using Xunit;

public class SatelliteImagingContractTests
{
    [Fact]
    public void SatellitePass_CarriesImageAndMetadata()
    {
        var dev = new Device("id", "Satellite", "NOAA-19", new Dictionary<string, string>(),
            null, "NOAA-APT", 0.9, [new EvidenceItem("apt_sync", "locked", 0.9)]);
        var pass = new SatellitePass(dev, [1, 2, 3], 4, 0.9);
        Assert.Equal("NOAA-19", pass.Device.PrimaryIdentifier);
        Assert.Equal(3, pass.PngImage.Length);
        Assert.Equal(4, pass.Lines);
    }
}
