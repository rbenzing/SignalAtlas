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

    [Fact]
    public void Accept_ManySimultaneouslyFreshAircraft_CacheStaysBounded()
    {
        var r = new CprPositionResolver();
        // 4200 distinct ICAOs, all fresh at the same instant → must not exceed the 4096 cap.
        for (int k = 0; k < 4200; k++)
            r.Accept($"{k:X6}", odd: false, EvenLat, EvenLon, T0);
        Assert.True(r.TrackedAircraft <= 4096, $"cache grew to {r.TrackedAircraft}");
    }

    [Fact]
    public void Accept_OverCapWithStaggeredFreshTimes_EvictsOldestByMostRecentFrameTime()
    {
        var r = new CprPositionResolver();
        // 4146 distinct ICAOs (4096 + 50 over the cap), each with a strictly increasing, fresh
        // timestamp (1 ms apart, well within the 10 s pairing window of "now" == the last insert),
        // so the stale-purge block drops nothing and the hard-cap batch-eviction path is exercised.
        const int total = 4096 + 50;
        for (int k = 0; k < total; k++)
            r.Accept($"{k:X6}", odd: false, EvenLat, EvenLon, T0.AddMilliseconds(k));

        Assert.Equal(4096, r.TrackedAircraft);

        // The single oldest-timestamped ICAO (index 0) must have been evicted: completing its
        // pair with an odd frame finds no surviving even frame, so the pair stays incomplete and
        // Accept returns null.
        var evictedTime = T0.AddMilliseconds(total);
        Assert.Null(r.Accept("000000", odd: true, OddLat, OddLon, evictedTime));

        // The single most-recently-inserted ICAO (index total-1) must have survived: completing
        // its pair with an odd frame finds the still-cached even frame and decodes a position.
        var survivorIcao = $"{total - 1:X6}";
        var pos = r.Accept(survivorIcao, odd: true, OddLat, OddLon, T0.AddMilliseconds(total - 1 + 1));
        Assert.NotNull(pos);
    }
}
