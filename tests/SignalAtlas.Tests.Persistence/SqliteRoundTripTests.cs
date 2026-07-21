using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// Docker-free persistence round-trips (SPEC §4.3 offline-first, §7.2 data model). The SAME
/// <see cref="SignalAtlasDbContext"/> that runs on Postgres in prod is exercised here on a SQLite
/// in-memory database (connection kept open so the schema survives), proving the JSON
/// <c>ValueConverter</c> mapping is provider-portable.
/// </summary>
public sealed class SqliteRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SignalAtlasDbContext> _options;

    public SqliteRoundTripTests()
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

    [Fact]
    public void Observation_RoundTrips_Unchanged()
    {
        var obs = new Observation(
            Time: new DateTimeOffset(2026, 7, 7, 12, 34, 56, TimeSpan.Zero),
            TimeSource: TimeSource.Gps,
            CollectorId: "col-1",
            Seq: 42,
            FrequencyHz: 915_000_000,
            BandwidthHz: 125_000,
            Power: -37.5,
            PowerRef: PowerRef.Relative,
            SnrDb: 11.25,
            Latitude: 42.36,
            Longitude: -71.06,
            PositionQuality: PositionQuality.Good,
            IqRef: "ring://0/42",
            CorrelationId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            ReceiverConfig: new ReceiverConfig(AmpEnable: true, LnaDb: 24, VgaDb: 20, BasebandBwHz: 1_750_000, BiasTee: false));

        new EfObservationRepository(NewContext()).Add(obs);

        var read = new EfObservationRepository(NewContext()).GetRecent(10);

        var got = Assert.Single(read);
        Assert.Equal(obs.Time, got.Time);
        Assert.Equal(obs.Power, got.Power);
        Assert.Equal(obs.PowerRef, got.PowerRef);          // relative power + power_ref
        Assert.Equal(obs.PositionQuality, got.PositionQuality);
        Assert.Equal(obs.CorrelationId, got.CorrelationId);
        Assert.Equal(obs.TimeSource, got.TimeSource);
        Assert.Equal(obs.Seq, got.Seq);
        Assert.Equal(obs.ReceiverConfig, got.ReceiverConfig); // receiver-config provenance preserved
    }

    // #13 — a null ReceiverConfig (file/synthetic capture path) round-trips as null, not a default record.
    [Fact]
    public void Observation_WithNullReceiverConfig_RoundTripsAsNull()
    {
        var obs = new Observation(
            Time: new DateTimeOffset(2026, 7, 7, 12, 34, 56, TimeSpan.Zero),
            TimeSource: TimeSource.Gps,
            CollectorId: "col-1",
            Seq: 43,
            FrequencyHz: 915_000_000,
            BandwidthHz: 2_000_000,
            Power: -37.5,
            PowerRef: PowerRef.Relative,
            SnrDb: null,
            Latitude: null,
            Longitude: null,
            PositionQuality: PositionQuality.None,
            IqRef: null,
            CorrelationId: Guid.Parse("22222222-3333-4444-5555-666666666666"),
            ReceiverConfig: null);

        new EfObservationRepository(NewContext()).Add(obs);

        var read = new EfObservationRepository(NewContext()).GetRecent(10);

        var got = Assert.Single(read);
        Assert.Null(got.ReceiverConfig);
    }

    [Fact]
    public void Signal_WithEvidence_RoundTrips_Preserved()
    {
        var signal = new Signal(
            Id: 7,
            Time: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            ObservationId: 1,
            EmitterId: null,
            DeviceId: null,
            Protocol: "LoRa",
            Confidence: 0.92,
            Classifier: "rules-v1",
            Evidence:
            [
                new EvidenceItem("modulation", "CSS", 0.5),
                new EvidenceItem("bandwidth_hz", "125000", 0.42)
            ],
            CenterFreqHz: 915_000_000,
            BandwidthHz: 125_000,
            DurationMs: 350,
            Features: new Dictionary<string, double> { ["snr_db"] = 12.5 });

        using (var ctx = NewContext())
        {
            ctx.Signals.Add(signal);
            ctx.SaveChanges();
        }

        var read = new EfSignalRepository(NewContext()).GetSignals();

        var got = Assert.Single(read);
        Assert.Equal(2, got.Evidence.Count);
        Assert.Equal(signal.Evidence, got.Evidence);       // non-empty evidence preserved
        Assert.Equal(12.5, got.Features["snr_db"]);
        Assert.Equal("LoRa", got.Protocol);
    }

    [Fact]
    public void Device_RoundTrips_IdentifiersAndVendorPreserved()
    {
        var device = new Device(
            Id: "DEV-000001",
            DeviceType: "Aircraft",
            PrimaryIdentifier: "4840D6",
            Identifiers: new Dictionary<string, string> { ["icao"] = "4840D6", ["callsign"] = "KLM1023" },
            Vendor: "Boeing",
            Protocol: "ADS-B",
            Confidence: 0.98,
            Evidence: [new EvidenceItem("crc", "pass", 1.0)]);

        using (var ctx = NewContext())
        {
            ctx.Devices.Add(device);
            ctx.SaveChanges();
        }

        var read = new EfDeviceRepository(NewContext()).GetDevices();

        var got = Assert.Single(read);
        Assert.Equal("4840D6", got.Identifiers["icao"]);   // identifiers preserved
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
        Assert.Equal("Boeing", got.Vendor);                // vendor preserved
        Assert.Equal("Aircraft", got.DeviceType);
    }

    public void Dispose() => _connection.Dispose();
}
