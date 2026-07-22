using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Domain;
using Xunit;

public class AptImageEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetDeviceImage_ReturnsPngWhenPresent_404WhenNot()
    {
        var app = factory.WithWebHostBuilder(_ => { });
        var store = app.Services.GetRequiredService<IAptImageStore>();
        store.Put("sat-1", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var client = app.CreateClient();

        var ok = await client.GetAsync("/api/v1/devices/sat-1/image");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);

        var missing = await client.GetAsync("/api/v1/devices/nope/image");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
