using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// Response security headers + safe defaults (SPEC §4.6/§4.7): every response carries the
/// conservative hardening headers.
/// </summary>
public class SecurityHeadersTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/v1/signals")]
    public async Task Response_CarriesSecurityHeaders(string url)
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(url);

        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
    }
}
