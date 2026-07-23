using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Domain;
using SignalAtlas.Geospatial;
using Xunit;

namespace SignalAtlas.Tests.Integration;

/// <summary>
/// GET /api/v1/devices/{id}/geo (SPEC §8.4 Phase 2 design §3/§6): the NOAA APT georeference quad
/// endpoint. Gated (inside the `api` group, invariant #5); resolves the APT device's
/// satellite/passStart/lines identifiers, fetches its TLE via <see cref="ITleProvider"/>, and
/// returns the approximate overlay quad — or 404 when the device is missing, not an APT device, or
/// no TLE/fix is available (offline-degrading, design §1).
/// </summary>
public class DeviceGeoEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Noaa19Name = "NOAA 19";
    private const string Noaa19Line1 = "1 33591U 09005A   26204.29298328  .00000011  00000+0  29886-4 0  9990";
    private const string Noaa19Line2 = "2 33591  98.9503 275.1908 0012716 289.5371  70.4428 14.13479265899587";

    private sealed class FakeTleProvider : ITleProvider
    {
        public Task<Tle?> GetAsync(string satelliteName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Tle?>(Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2));
    }

    private sealed class NullTleProvider : ITleProvider
    {
        public Task<Tle?> GetAsync(string satelliteName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Tle?>(null);
    }

    private static Device AptDevice(string id, DateTimeOffset passStart, int lines = 240) => new(
        Id: id,
        DeviceType: "Satellite",
        PrimaryIdentifier: "NOAA-19",
        Identifiers: new Dictionary<string, string>
        {
            ["satellite"] = "NOAA-19",
            ["frequencyMhz"] = "137.1",
            ["lines"] = lines.ToString(),
            ["passStart"] = passStart.ToString("o"),
        },
        Vendor: null,
        Protocol: "NOAA-APT",
        Confidence: 1.0,
        Evidence: [new EvidenceItem("apt_sync", "locked", 1.0)]);

    // 1. A positioned APT device with a resolvable TLE -> 200 + a 4-corner approximate quad.
    [Fact]
    public async Task GetGeo_AptDeviceWithTle_ReturnsApproximateQuad()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
                services.AddSingleton<ITleProvider>(new FakeTleProvider()));
        });

        var devices = app.Services.GetRequiredService<IDeviceRepository>();
        var passStart = Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2).Epoch;
        devices.Upsert(AptDevice("apt-geo-1", passStart));

        var client = app.CreateClient();
        var response = await client.GetAsync("/api/v1/devices/apt-geo-1/geo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("approximate").GetBoolean());
        Assert.Equal(4, payload.GetProperty("corners").GetArrayLength());
    }

    // 2. Unknown device id -> 404.
    [Fact]
    public async Task GetGeo_UnknownDevice_Returns404()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
                services.AddSingleton<ITleProvider>(new FakeTleProvider()));
        });
        var client = app.CreateClient();

        var response = await client.GetAsync("/api/v1/devices/nonexistent-device/geo");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // 3. A non-APT device (e.g. the seeded ADS-B aircraft protocol) -> 404, never a geo quad.
    [Fact]
    public async Task GetGeo_NonAptDevice_Returns404()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
                services.AddSingleton<ITleProvider>(new FakeTleProvider()));
        });

        var devices = app.Services.GetRequiredService<IDeviceRepository>();
        var nonApt = new Device(
            Id: "not-apt-1",
            DeviceType: "Aircraft",
            PrimaryIdentifier: "KLM1023",
            Identifiers: new Dictionary<string, string> { ["icao"] = "4840D6" },
            Vendor: null,
            Protocol: "ADS-B",
            Confidence: 1.0,
            Evidence: [new EvidenceItem("crc", "pass", 1.0)]);
        devices.Upsert(nonApt);

        var client = app.CreateClient();
        var response = await client.GetAsync("/api/v1/devices/not-apt-1/geo");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // 4. An APT device whose TLE can't be resolved (offline / unknown sat) -> 404, never a throw.
    [Fact]
    public async Task GetGeo_AptDeviceWithNoTle_Returns404()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
                services.AddSingleton<ITleProvider>(new NullTleProvider()));
        });

        var devices = app.Services.GetRequiredService<IDeviceRepository>();
        devices.Upsert(AptDevice("apt-geo-no-tle", DateTimeOffset.UtcNow));

        var client = app.CreateClient();
        var response = await client.GetAsync("/api/v1/devices/apt-geo-no-tle/geo");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    // 5. Sanity: audit log records the read (SPEC §4.6/NFR-S2 — every identifier/location read is
    //    audit-logged), mirroring the /devices/{id}/image and /devices patterns.
    [Fact]
    public async Task GetGeo_AptDeviceWithTle_RecordsAuditEntry()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
                services.AddSingleton<ITleProvider>(new FakeTleProvider()));
        });

        var devices = app.Services.GetRequiredService<IDeviceRepository>();
        var passStart = Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2).Epoch;
        devices.Upsert(AptDevice("apt-geo-audit", passStart));

        var client = app.CreateClient();
        await client.GetAsync("/api/v1/devices/apt-geo-audit/geo");

        var audit = app.Services.GetRequiredService<IAuditLog>();
        Assert.Contains(audit.Recent(50), e => e.Action == "read:device-geo" && e.Query == "apt-geo-audit");
    }
}
