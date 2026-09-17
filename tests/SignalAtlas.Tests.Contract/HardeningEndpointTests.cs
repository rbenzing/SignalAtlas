using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// Production-hardening contract tests (SPEC §9.4 problem-details, §5.5 NFR-R2 correlationId +
/// /health /ready, NFR-R3 metrics, NFR-S2 audit). RED-first per the TDD law (§6.2).
/// </summary>
public class HardeningEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    // §9.4 — an unhandled exception becomes a 500 application/problem+json with a correlationId and
    // leaks NO internal detail (no message, no type name, no stack trace).
    [Fact]
    public async Task UnhandledException_Returns500ProblemJson_WithoutLeakingInternals()
    {
        const string secret = "SECRET_LEAK_TOKEN_9f3a";
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISignalRepository>(new ThrowingSignalRepository(secret))))
            .CreateClient();

        var resp = await client.GetAsync("/api/v1/signals");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.True(root.TryGetProperty("correlationId", out _));

        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain("ThrowingSignalRepository", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain("StackTrace", body);
    }

    // NFR-R2 — every response carries an X-Correlation-ID header (generated when none supplied).
    [Fact]
    public async Task EveryResponse_CarriesCorrelationIdHeader()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");

        Assert.True(resp.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    // NFR-R2 — a supplied X-Correlation-ID is echoed back and used as the envelope correlationId.
    [Fact]
    public async Task SuppliedCorrelationId_IsEchoedAndUsedInEnvelope()
    {
        var supplied = Guid.NewGuid();
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/signals");
        req.Headers.Add("X-Correlation-ID", supplied.ToString());

        var resp = await client.SendAsync(req);

        Assert.Equal(supplied.ToString(), resp.Headers.GetValues("X-Correlation-ID").Single());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(supplied, doc.RootElement.GetProperty("correlationId").GetGuid());
    }

    // NFR-R3 — /metrics returns a 200 text exposition and is NOT behind the auth gate.
    [Fact]
    public async Task Metrics_ReturnsExposition_AndIsUngated()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE", body);
        Assert.False(resp.Headers.Contains("X-Authorization-Gate"));
    }

    // NFR-R2 — /ready is 200 with the in-memory provider (dependencies reachable).
    [Fact]
    public async Task Ready_Returns200_WithInMemoryProvider()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
    }

    // Pagination — limit out of range → 400 problem-details.
    [Theory]
    [InlineData("/api/v1/signals?limit=0")]
    [InlineData("/api/v1/signals?limit=1001")]
    [InlineData("/api/v1/signals?limit=-1")]
    [InlineData("/api/v1/signals?offset=-1")]
    public async Task InvalidPagination_Returns400ProblemJson(string url)
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    // Pagination — a valid limit caps the number of returned rows.
    [Fact]
    public async Task ValidLimit_CapsResults()
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISignalRepository>(new MultiSignalRepository(5))))
            .CreateClient();

        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/signals?limit=2")).Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("payload").GetArrayLength());
    }

    // Pagination — more rows than the limit yields a non-null nextCursor alongside the still-capped
    // payload array; feeding that cursor back walks to the next page, and the final page's
    // nextCursor is null (round-trips through Pagination.EncodeCursor/TryDecodeCursor).
    [Fact]
    public async Task MoreRowsThanLimit_EmitsNextCursor_AndCursorWalksToNextPage()
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISignalRepository>(new MultiSignalRepository(5))))
            .CreateClient();

        // Page 1: ids 1,2 + a non-null nextCursor.
        using var page1 = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/signals?limit=2")).Content.ReadAsStringAsync());
        var payload1 = page1.RootElement.GetProperty("payload");
        Assert.Equal(2, payload1.GetArrayLength());
        Assert.Equal(1, payload1[0].GetProperty("id").GetInt32());
        Assert.Equal(2, payload1[1].GetProperty("id").GetInt32());
        var cursor1 = page1.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor1));

        // Page 2: feeding cursor1 back walks to ids 3,4 (a different first element) + another cursor.
        using var page2 = JsonDocument.Parse(
            await (await client.GetAsync($"/api/v1/signals?limit=2&cursor={Uri.EscapeDataString(cursor1!)}"))
                .Content.ReadAsStringAsync());
        var payload2 = page2.RootElement.GetProperty("payload");
        Assert.Equal(2, payload2.GetArrayLength());
        Assert.Equal(3, payload2[0].GetProperty("id").GetInt32());
        Assert.Equal(4, payload2[1].GetProperty("id").GetInt32());
        var cursor2 = page2.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor2));
        Assert.NotEqual(cursor1, cursor2);

        // Page 3 (final): id 5 only, and nextCursor is null — no more pages.
        using var page3 = JsonDocument.Parse(
            await (await client.GetAsync($"/api/v1/signals?limit=2&cursor={Uri.EscapeDataString(cursor2!)}"))
                .Content.ReadAsStringAsync());
        var payload3 = page3.RootElement.GetProperty("payload");
        Assert.Equal(1, payload3.GetArrayLength());
        Assert.Equal(5, payload3[0].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, page3.RootElement.GetProperty("nextCursor").ValueKind);
    }

    // A non-list endpoint (e.g. /summary) still serializes fine with the new optional envelope field:
    // nextCursor is null and the payload shape is unchanged.
    [Fact]
    public async Task NonListEndpoint_SerializesWithNullNextCursor_AndUnchangedPayload()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/summary")).Content.ReadAsStringAsync());

        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("nextCursor", out var nextCursor));
        Assert.Equal(JsonValueKind.Null, nextCursor.ValueKind);

        var payload = root.GetProperty("payload");
        Assert.True(payload.TryGetProperty("signalCount", out _));
        Assert.True(payload.TryGetProperty("deviceCount", out _));
        Assert.True(payload.TryGetProperty("alertCount", out _));
    }

    // NFR-S2 — a /devices read writes an audit entry; probes do not.
    [Fact]
    public async Task DevicesRead_WritesAuditEntry()
    {
        var testFactory = _factory.WithWebHostBuilder(_ => { });
        var client = testFactory.CreateClient();

        await client.GetAsync("/api/v1/devices");

        var audit = testFactory.Services.GetRequiredService<IAuditLog>();
        var entries = audit.Recent(50);
        Assert.Contains(entries, e => e.Action == "read:devices" && e.Actor == "local-operator");
    }

    // NFR-S2 says EVERY identifier/location read is audit-logged. /emitters returns both
    // `identifiers` AND `estLatitude`/`estLongitude`, and /map/heatmap returns positions — but
    // neither was logged, so the two endpoints that expose emitter identity and geolocation left no
    // audit trail at all (SOC 2 CC7.2: access to sensitive data must be recorded).
    [Theory]
    [InlineData("/api/v1/emitters", "read:emitters")]
    [InlineData("/api/v1/map/heatmap", "read:map-heatmap")]
    public async Task IdentifierAndLocationReads_WriteAuditEntries(string path, string expectedAction)
    {
        var testFactory = _factory.WithWebHostBuilder(_ => { });
        var client = testFactory.CreateClient();

        await client.GetAsync(path);

        var audit = testFactory.Services.GetRequiredService<IAuditLog>();
        Assert.Contains(
            audit.Recent(50),
            e => e.Action == expectedAction && e.Actor == "local-operator");
    }

    // A single emitter read exposes that emitter's identifiers + estimated position — same rule.
    [Fact]
    public async Task SingleEmitterRead_WritesAuditEntry()
    {
        var testFactory = _factory.WithWebHostBuilder(_ => { });
        var client = testFactory.CreateClient();

        await client.GetAsync("/api/v1/emitters/emitter-does-not-exist");

        var audit = testFactory.Services.GetRequiredService<IAuditLog>();
        Assert.Contains(
            audit.Recent(50),
            e => e.Action == "read:emitter" && e.Actor == "local-operator");
    }

    private sealed class ThrowingSignalRepository(string secret) : ISignalRepository
    {
        public IReadOnlyList<Signal> GetSignals(int limit = 100) =>
            throw new InvalidOperationException(secret);
    }

    private sealed class MultiSignalRepository(int count) : ISignalRepository
    {
        public IReadOnlyList<Signal> GetSignals(int limit = 100) =>
            Enumerable.Range(1, count).Select(i => new Signal(
                Id: i,
                Time: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
                ObservationId: i,
                EmitterId: null,
                DeviceId: null,
                Protocol: "LoRa",
                Confidence: 0.9,
                Classifier: "rules-v1",
                Evidence: [new EvidenceItem("modulation", "CSS", 0.5)],
                CenterFreqHz: 915_000_000,
                BandwidthHz: 125_000,
                DurationMs: 350,
                Features: new Dictionary<string, double> { ["snr_db"] = 12.5 }))
                .Take(limit).ToList();
    }
}
