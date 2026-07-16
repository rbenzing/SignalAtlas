using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Api;

namespace SignalAtlas.Tests.Contract;

public class RouteGatedTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    // M0-T4 — every /api/v1 route passes through the authorization gate (SPEC §4.7, §9.5).
    [Fact]
    public void EveryApiRoute_IsBehindTheAuthorizationGate()
    {
        // Force the host (and endpoint graph) to build.
        _ = _factory.CreateClient();
        var sources = _factory.Services.GetRequiredService<EndpointDataSource>();

        var apiEndpoints = sources.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(apiEndpoints);
        foreach (var ep in apiEndpoints)
        {
            Assert.True(
                ep.Metadata.GetMetadata<AuthGateMarker>() is not null,
                $"route '{ep.RoutePattern.RawText}' is not behind the authorization gate");
        }
    }
}
