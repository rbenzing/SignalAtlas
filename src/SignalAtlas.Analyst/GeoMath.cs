namespace SignalAtlas.Analyst;

/// <summary>Great-circle distance helper for the NearLocation query (SPEC §8.12, §8.6).</summary>
internal static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000.0;

    /// <summary>Haversine distance in meters between two WGS-84 coordinates.</summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                   * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
