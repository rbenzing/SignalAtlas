namespace SignalAtlas.Geospatial;

/// <summary>
/// Supplies the current <see cref="Tle"/> for a named satellite (SPEC §8.4 Phase 2 design §3).
/// Offline-degrading (design §1): a fetch failure, an offline host, or an unrecognized satellite
/// all resolve to <c>null</c> — the caller (the <c>/devices/{id}/geo</c> endpoint) turns that into
/// a 404 and the map simply shows no weather overlay, it never breaks.
/// </summary>
public interface ITleProvider
{
    Task<Tle?> GetAsync(string satelliteName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches the NOAA-weather TLE set from Celestrak (SPEC §8.4 Phase 2 design §3) over an injected
/// <see cref="HttpClient"/>, caches the whole set in-memory for <see cref="CacheTtl"/>, and matches
/// a requested satellite by its published TLE name (e.g. "NOAA 19"/"NOAA-19") or its NORAD catalog
/// id (NOAA-15=25338, NOAA-18=28654, NOAA-19=33591).
///
/// <para>Deterministic-core note (P5): the fetch itself is I/O and therefore outside the pure
/// geometry core, but the cache TTL check never calls <c>DateTimeOffset.Now/UtcNow</c> directly —
/// the "now" source is an injected <see cref="Func{TResult}"/> (defaulting to
/// <see cref="DateTimeOffset.UtcNow"/> only at the composition root) so tests stay deterministic.</para>
///
/// <para>Never throws out of <see cref="GetAsync"/>: any transport failure, non-success HTTP
/// status, or unparsable body is swallowed and treated as "no data available" (falls back to
/// whatever is already cached, or null if nothing has ever been fetched successfully).</para>
/// </summary>
public sealed class CelestrakTleProvider : ITleProvider
{
    private const string CelestrakNoaaTleUrl =
        "https://celestrak.org/NORAD/elements/gp.php?GROUP=noaa&FORMAT=tle";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    // NORAD catalog ids for the active NOAA POES weather satellites (SPEC §8.4 Phase 2 design §3).
    private static readonly IReadOnlyDictionary<string, int> NoradIdBySatelliteName =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["NOAA-15"] = 25338,
            ["NOAA-18"] = 28654,
            ["NOAA-19"] = 33591,
        };

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<Tle>? _cachedSet;
    private DateTimeOffset _cachedAt;

    /// <param name="httpClient">Injected so it can be mocked in tests (design §3) and so the real
    /// composition root can share one <c>HttpClient</c> instance/handler.</param>
    /// <param name="now">Clock seam (P5) — defaults to <see cref="DateTimeOffset.UtcNow"/> only at
    /// the composition root; tests pass a fixed/steppable function instead.</param>
    public CelestrakTleProvider(HttpClient httpClient, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<Tle?> GetAsync(string satelliteName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(satelliteName);

        var set = await GetTleSetAsync(cancellationToken).ConfigureAwait(false);
        if (set is null)
            return null;

        foreach (var tle in set)
        {
            if (Matches(tle, satelliteName))
                return tle;
        }

        return null;
    }

    private static bool Matches(Tle tle, string satelliteName)
    {
        if (string.Equals(tle.Name, satelliteName, StringComparison.OrdinalIgnoreCase))
            return true;

        // The Celestrak-published name uses a space ("NOAA 19"); callers commonly use a hyphen
        // ("NOAA-19", matching Device.Identifiers["satellite"] from AptDecoder) — normalize both.
        var normalizedTleName = tle.Name.Replace(' ', '-');
        if (string.Equals(normalizedTleName, satelliteName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (NoradIdBySatelliteName.TryGetValue(satelliteName, out var knownNoradId) &&
            tle.NoradId == knownNoradId)
            return true;

        if (int.TryParse(satelliteName, out var requestedNoradId) && tle.NoradId == requestedNoradId)
            return true;

        return false;
    }

    private async Task<IReadOnlyList<Tle>?> GetTleSetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _now();
            if (_cachedSet is not null && now - _cachedAt < CacheTtl)
                return _cachedSet;

            string blob;
            try
            {
                using var response = await _httpClient
                    .GetAsync(CelestrakNoaaTleUrl, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return _cachedSet; // offline-degrading: keep whatever was last cached (maybe null)

                blob = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return _cachedSet;
            }

            IReadOnlyList<Tle> parsed;
            try
            {
                parsed = Tle.ParseMany(blob);
            }
            catch (FormatException)
            {
                return _cachedSet;
            }

            _cachedSet = parsed;
            _cachedAt = now;
            return _cachedSet;
        }
        finally
        {
            _gate.Release();
        }
    }
}
