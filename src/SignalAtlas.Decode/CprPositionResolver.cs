using SignalAtlas.Domain;

namespace SignalAtlas.Decode;

/// <summary>
/// Global (even/odd) airborne CPR decoder with a bounded per-ICAO frame cache (SPEC §8.4 Stage 2).
/// The math is the standard RTCA DO-260 global airborne algorithm; the NL(lat) longitude-zone check
/// rejects a pair that straddles a latitude zone. Thread-safe (single lock); deterministic.
/// </summary>
public sealed class CprPositionResolver : ICprPositionResolver
{
    private static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(10);
    private const int MaxAircraft = 4096;
    private const double Two17 = 131072.0; // 2^17

    private readonly record struct Frame(int Lat, int Lon, DateTimeOffset Time);

    private readonly object _sync = new();
    private readonly Dictionary<string, (Frame? Even, Frame? Odd)> _cache = new(StringComparer.Ordinal);

    /// <summary>Number of aircraft currently tracked in the pairing cache (for tests/metrics).</summary>
    public int TrackedAircraft { get { lock (_sync) return _cache.Count; } }

    public GeoPosition? Accept(string icao, bool odd, int cprLat17, int cprLon17, DateTimeOffset time)
    {
        lock (_sync)
        {
            _cache.TryGetValue(icao, out var pair);
            var f = new Frame(cprLat17, cprLon17, time);
            pair = odd ? (pair.Even, f) : (f, pair.Odd);
            _cache[icao] = pair;

            if (_cache.Count > MaxAircraft)
                Evict(time);

            if (pair.Even is not { } e || pair.Odd is not { } o)
                return null;
            if (time - e.Time > PairWindow || time - o.Time > PairWindow)
                return null;

            bool useOdd = o.Time >= e.Time; // most-recent frame is the reference
            return GlobalDecode(e, o, useOdd);
        }
    }

    private static GeoPosition? GlobalDecode(Frame even, Frame odd, bool useOdd)
    {
        double latCprE = even.Lat / Two17, latCprO = odd.Lat / Two17;
        double lonCprE = even.Lon / Two17, lonCprO = odd.Lon / Two17;

        int j = (int)Math.Floor(59.0 * latCprE - 60.0 * latCprO + 0.5);
        double latE = (360.0 / 60.0) * (Mod(j, 60) + latCprE);
        double latO = (360.0 / 59.0) * (Mod(j, 59) + latCprO);
        if (latE >= 270.0) latE -= 360.0;
        if (latO >= 270.0) latO -= 360.0;

        if (Nl(latE) != Nl(latO)) return null; // pair straddles a latitude zone — await a fresh pair

        double lat = useOdd ? latO : latE;
        int nl = Nl(lat);
        int ni = Math.Max(nl - (useOdd ? 1 : 0), 1);
        double m = Math.Floor(lonCprE * (nl - 1) - lonCprO * nl + 0.5);
        double lonCpr = useOdd ? lonCprO : lonCprE;
        double lon = (360.0 / ni) * (Mod(m, ni) + lonCpr);
        if (lon >= 180.0) lon -= 360.0;

        return new GeoPosition(lat, lon);
    }

    /// <summary>Number of longitude zones at a latitude (RTCA NL table, via the closed form).</summary>
    private static int Nl(double lat)
    {
        double absLat = Math.Abs(lat);
        if (absLat >= 87.0) return 1;
        const double nz = 15.0;
        double a = 1.0 - Math.Cos(Math.PI / (2.0 * nz));
        double cosLat = Math.Cos(Math.PI * absLat / 180.0);
        double x = a / (cosLat * cosLat);
        return (int)Math.Floor(2.0 * Math.PI / Math.Acos(1.0 - x));
    }

    private static double Mod(double a, double b) => a - b * Math.Floor(a / b);

    private void Evict(DateTimeOffset now)
    {
        // Drop entries whose both slots are stale relative to the pairing window.
        foreach (var key in _cache.Where(kv =>
                     (kv.Value.Even is not { } e || now - e.Time > PairWindow) &&
                     (kv.Value.Odd is not { } o || now - o.Time > PairWindow))
                 .Select(kv => kv.Key).ToList())
            _cache.Remove(key);

        // Hard cap: if still over the limit (many simultaneously-fresh aircraft), evict the
        // entries with the oldest most-recent-frame time in a single O(n log n) batch instead of
        // an O(n) scan per removal.
        int over = _cache.Count - MaxAircraft;
        if (over > 0)
        {
            var victims = _cache
                .OrderBy(kv => MostRecent(kv.Value))
                .Take(over)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in victims) _cache.Remove(key);
        }
    }

    private static DateTimeOffset MostRecent((Frame? Even, Frame? Odd) pair)
    {
        var e = pair.Even?.Time ?? DateTimeOffset.MinValue;
        var o = pair.Odd?.Time ?? DateTimeOffset.MinValue;
        return e >= o ? e : o;
    }
}
