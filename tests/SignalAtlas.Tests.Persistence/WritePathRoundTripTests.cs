using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// Docker-free round-trips for the emitter/alert write path and the audit log (SPEC §7.2, §8.5,
/// §8.8, §4.6/NFR-S2). Exercises the SAME EF stores that back Postgres in prod on a SQLite
/// in-memory database (connection kept open so the schema survives).
/// </summary>
public sealed class WritePathRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SignalAtlasDbContext> _options;

    public WritePathRoundTripTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SignalAtlasDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new SignalAtlasDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    private SignalAtlasDbContext NewContext() => new(_options);

    private static Emitter SampleEmitter(string id, long signalCount, double confidence) =>
        new(
            Id: id,
            DeviceId: null,
            Protocol: "LoRa",
            FreqCenterHz: 915_000_000,
            FreqStabilityHz: 500,
            EstLatitude: 42.36,
            EstLongitude: -71.06,
            EstUncertaintyM: 120,
            SignalCount: signalCount,
            Confidence: confidence,
            Identifiers: new Dictionary<string, string> { ["freq"] = "915M" },
            Evidence: [new EvidenceItem("freq_proximity", "1.0", 0.5)]);

    [Fact]
    public void Emitter_Upsert_Then_All_RoundTrips()
    {
        new EfEmitterRepository(NewContext()).Upsert(SampleEmitter("EMT-000001", 1, 0.6));

        var read = new EfEmitterRepository(NewContext()).All();

        var got = Assert.Single(read);
        Assert.Equal("EMT-000001", got.Id);
        Assert.Equal("LoRa", got.Protocol);
        Assert.Equal("915M", got.Identifiers["freq"]);
        Assert.NotEmpty(got.Evidence);
    }

    [Fact]
    public void Emitter_Upsert_IsIdempotent_And_Updates_ExistingRow()
    {
        new EfEmitterRepository(NewContext()).Upsert(SampleEmitter("EMT-000001", 1, 0.6));
        new EfEmitterRepository(NewContext()).Upsert(SampleEmitter("EMT-000001", 5, 0.9));

        var read = new EfEmitterRepository(NewContext()).All();

        var got = Assert.Single(read);              // same id → no duplicate
        Assert.Equal(5, got.SignalCount);           // updated in place
        Assert.Equal(0.9, got.Confidence);
    }

    private static Device SampleDevice(string icao, string? callsign) =>
        new(
            Id: icao,
            DeviceType: "Aircraft",
            PrimaryIdentifier: icao,
            Identifiers: callsign is null
                ? new Dictionary<string, string> { ["icao"] = icao }
                : new Dictionary<string, string> { ["icao"] = icao, ["callsign"] = callsign },
            Vendor: null,
            Protocol: "ADS-B",
            Confidence: 1.0,
            Evidence: [new EvidenceItem("icao", icao, 1.0)]);

    [Fact]
    public void Device_Upsert_Then_Get_RoundTrips()
    {
        new EfDeviceRepository(NewContext()).Upsert(SampleDevice("4840D6", "KLM1023"));

        var read = new EfDeviceRepository(NewContext()).GetDevices();

        var got = Assert.Single(read);
        Assert.Equal("4840D6", got.Id);
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
        Assert.NotEmpty(got.Evidence);
    }

    [Fact]
    public void Device_Upsert_IsIdempotent_AndUpdatesExistingRow()
    {
        new EfDeviceRepository(NewContext()).Upsert(SampleDevice("4840D6", null));
        new EfDeviceRepository(NewContext()).Upsert(SampleDevice("4840D6", "KLM1023"));

        var read = new EfDeviceRepository(NewContext()).GetDevices();

        var got = Assert.Single(read);                       // same id → no duplicate
        Assert.Equal("KLM1023", got.Identifiers["callsign"]); // updated in place
    }

    [Fact]
    public void Alert_Add_Then_Read_RoundTrips()
    {
        var alert = new Alert(
            Id: Guid.Parse("22222222-3333-4444-5555-666666666666"),
            Time: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            EmitterId: "EMT-000001",
            DeviceId: null,
            Kind: Alert.NewEmitter,
            Severity: "info",
            Summary: "first sighting",
            Evidence: [new EvidenceItem("first_seen", "true", 1.0)]);

        new EfAlertRepository(NewContext()).Add(alert);

        var read = new EfAlertRepository(NewContext()).GetAlerts();

        var got = Assert.Single(read);
        Assert.Equal(alert.Id, got.Id);
        Assert.Equal(Alert.NewEmitter, got.Kind);
        Assert.NotEmpty(got.Evidence);
    }

    [Fact]
    public void Audit_Record_Then_Recent_RoundTrips_MostRecentFirst()
    {
        var log = new EfAuditLog(NewContext());
        log.Record("operator", "GET /devices", "{\"limit\":100}");
        new EfAuditLog(NewContext()).Record("operator", "GET /alerts", "{\"limit\":50}");

        var recent = new EfAuditLog(NewContext()).Recent(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("GET /alerts", recent[0].Action);   // Id-descending: newest first
        Assert.Equal("GET /devices", recent[1].Action);
        Assert.All(recent, e => Assert.Equal("operator", e.Actor));
    }

    public void Dispose() => _connection.Dispose();
}
