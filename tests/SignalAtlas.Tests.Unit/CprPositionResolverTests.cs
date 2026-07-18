using SignalAtlas.Decode;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class CprPositionResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    // junzis canonical vector: even (93000,51372) + odd (74158,50194), even as most-recent
    // reference → 52.2572, 3.91937.
    private const int EvenLat = 93000, EvenLon = 51372, OddLat = 74158, OddLon = 50194;

    [Fact]
    public void Accept_SingleFrame_ReturnsNull()
    {
        var r = new CprPositionResolver();
        Assert.Null(r.Accept("40621D", odd: false, EvenLat, EvenLon, T0));
    }

    [Fact]
    public void Accept_OddThenEven_DecodesCanonicalPosition()
    {
        var r = new CprPositionResolver();
        Assert.Null(r.Accept("40621D", odd: true, OddLat, OddLon, T0));
        var pos = r.Accept("40621D", odd: false, EvenLat, EvenLon, T0.AddSeconds(1)); // even most recent

        Assert.NotNull(pos);
        Assert.Equal(52.2572, pos!.Latitude, 3);   // 3 decimal places
        Assert.Equal(3.91937, pos.Longitude, 3);
    }

    [Fact]
    public void Accept_EvenThenOdd_AlsoResolves()
    {
        var r = new CprPositionResolver();
        Assert.Null(r.Accept("40621D", odd: false, EvenLat, EvenLon, T0));
        var pos = r.Accept("40621D", odd: true, OddLat, OddLon, T0.AddSeconds(1));
        Assert.NotNull(pos); // odd-as-reference position (near the same place)
        Assert.Equal(52.26, pos!.Latitude, 1);
    }

    [Fact]
    public void Accept_StalePartner_ReturnsNull()
    {
        var r = new CprPositionResolver();
        r.Accept("40621D", odd: true, OddLat, OddLon, T0);
        // Even arrives 11 s later — past the 10 s pairing window.
        Assert.Null(r.Accept("40621D", odd: false, EvenLat, EvenLon, T0.AddSeconds(11)));
    }

    [Fact]
    public void Accept_DifferentIcaos_DoNotCrossContaminate()
    {
        var r = new CprPositionResolver();
        r.Accept("AAAAAA", odd: true, OddLat, OddLon, T0);
        // A different ICAO's even frame must not pair with AAAAAA's odd frame.
        Assert.Null(r.Accept("BBBBBB", odd: false, EvenLat, EvenLon, T0.AddSeconds(1)));
    }
}
