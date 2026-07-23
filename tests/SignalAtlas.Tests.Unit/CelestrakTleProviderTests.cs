using System.Net;
using SignalAtlas.Geospatial;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// <see cref="CelestrakTleProvider"/> (SPEC §8.4 Phase 2 design §3): fetches the NOAA-weather TLE
/// set from Celestrak over an injected <see cref="HttpClient"/>, caches it in-memory for the TTL,
/// and degrades to null (never throws) offline / on an unknown satellite — the overlay is simply
/// absent (design §1 "offline-degrading"). Time is injected (a <c>Func&lt;DateTimeOffset&gt;</c>)
/// so the TTL logic stays deterministic/testable (P5 — no direct <c>DateTimeOffset.Now</c>).
/// </summary>
public class CelestrakTleProviderTests
{
    private const string Noaa19Name = "NOAA 19";
    private const string Noaa19Line1 = "1 33591U 09005A   26204.29298328  .00000011  00000+0  29886-4 0  9990";
    private const string Noaa19Line2 = "2 33591  98.9503 275.1908 0012716 289.5371  70.4428 14.13479265899587";

    private static string CannedBlob => string.Join("\n", Noaa19Name, Noaa19Line1, Noaa19Line2) + "\n";

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int CallCount { get; private set; }

        public CountingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(_status) { Content = new StringContent(_body) };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }

    // 1. Canned NOAA TLE blob -> GetAsync("NOAA-19") parses + returns the matching TLE.
    [Fact]
    public async Task GetAsync_ParsesCannedBlob_ReturnsMatchingTle()
    {
        var handler = new CountingHandler(CannedBlob);
        var httpClient = new HttpClient(handler);
        var provider = new CelestrakTleProvider(httpClient, () => DateTimeOffset.UtcNow);

        var tle = await provider.GetAsync("NOAA-19");

        Assert.NotNull(tle);
        Assert.Equal(33591, tle!.NoradId);
    }

    // 2. Match also works via the exact TLE name string as published ("NOAA 19").
    [Fact]
    public async Task GetAsync_MatchesByExactTleName()
    {
        var handler = new CountingHandler(CannedBlob);
        var httpClient = new HttpClient(handler);
        var provider = new CelestrakTleProvider(httpClient, () => DateTimeOffset.UtcNow);

        var tle = await provider.GetAsync("NOAA 19");

        Assert.NotNull(tle);
    }

    // 3. A second call within the TTL does NOT re-fetch (handler hit exactly once).
    [Fact]
    public async Task GetAsync_SecondCallWithinTtl_DoesNotRefetch()
    {
        var handler = new CountingHandler(CannedBlob);
        var httpClient = new HttpClient(handler);
        var now = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        var provider = new CelestrakTleProvider(httpClient, () => now);

        var first = await provider.GetAsync("NOAA-19");
        var second = await provider.GetAsync("NOAA-19");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, handler.CallCount);
    }

    // 4. Past the TTL, the next call re-fetches.
    [Fact]
    public async Task GetAsync_AfterTtlExpires_Refetches()
    {
        var handler = new CountingHandler(CannedBlob);
        var httpClient = new HttpClient(handler);
        var now = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        var provider = new CelestrakTleProvider(httpClient, () => now);

        await provider.GetAsync("NOAA-19");
        now = now.AddHours(13); // > 12h TTL
        await provider.GetAsync("NOAA-19");

        Assert.Equal(2, handler.CallCount);
    }

    // 5. HTTP failure (offline) -> null, never a throw (design §1 offline-degrading).
    [Fact]
    public async Task GetAsync_HttpFailure_ReturnsNull()
    {
        var httpClient = new HttpClient(new ThrowingHandler());
        var provider = new CelestrakTleProvider(httpClient, () => DateTimeOffset.UtcNow);

        var tle = await provider.GetAsync("NOAA-19");

        Assert.Null(tle);
    }

    // 6. An unknown satellite name (not in the fetched set) -> null.
    [Fact]
    public async Task GetAsync_UnknownSatellite_ReturnsNull()
    {
        var handler = new CountingHandler(CannedBlob);
        var httpClient = new HttpClient(handler);
        var provider = new CelestrakTleProvider(httpClient, () => DateTimeOffset.UtcNow);

        var tle = await provider.GetAsync("SPUTNIK-99");

        Assert.Null(tle);
    }

    // 7. A non-success HTTP status is treated the same as a transport failure -> null.
    [Fact]
    public async Task GetAsync_NonSuccessStatus_ReturnsNull()
    {
        var handler = new CountingHandler("service unavailable", HttpStatusCode.ServiceUnavailable);
        var httpClient = new HttpClient(handler);
        var provider = new CelestrakTleProvider(httpClient, () => DateTimeOffset.UtcNow);

        var tle = await provider.GetAsync("NOAA-19");

        Assert.Null(tle);
    }
}
