using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

public class SignalsEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // GetSignals_EverySignalHasNonEmptyEvidence asserts the SEEDED signal is present — opt in to
    // demo seeding explicitly (SeedDemoData defaults false since the product no longer seeds by default).
    private readonly WebApplicationFactory<Program> _factory =
        factory.WithWebHostBuilder(b => b.UseSetting("SeedDemoData", "true"));

    // M0-T5 — GET /signals returns the versioned envelope {schemaVersion, correlationId, payload}.
    [Fact]
    public async Task GetSignals_ReturnsVersionedEnvelope()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/signals");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());
        Assert.NotEqual(Guid.Empty, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("payload").ValueKind);
    }

    // M0-T5 — each signal in the payload carries non-empty evidence (P4 / NFR-A6).
    [Fact]
    public async Task GetSignals_EverySignalHasNonEmptyEvidence()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/signals")).Content.ReadAsStringAsync());

        var payload = doc.RootElement.GetProperty("payload");
        Assert.NotEqual(0, payload.GetArrayLength());
        foreach (var sig in payload.EnumerateArray())
        {
            var evidence = sig.GetProperty("evidence");
            Assert.Equal(JsonValueKind.Array, evidence.ValueKind);
            Assert.True(evidence.GetArrayLength() > 0, "evidence must be non-empty");
        }
    }
}
