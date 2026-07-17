using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

public sealed class InMemoryDeviceRepositoryTests
{
    // The repo ctor runs one seed frame through an IDeviceResolver; a fake keeps this project
    // free of a SignalAtlas.Decode reference (it does not reference Decode).
    private sealed class FakeResolver : IDeviceResolver
    {
        public Device? Resolve(IReadOnlyList<DecodedFrame> frames)
        {
            if (frames.Count == 0) return null;
            var id = frames[0].Identifiers.TryGetValue("icao", out var icao) ? icao : "seed";
            return new Device(id, "Aircraft", id, frames[0].Identifiers, null, "ADS-B", 1.0,
                [new EvidenceItem("crc", "pass", 1.0)]);
        }
    }

    private static Device Aircraft(string icao, string? callsign = null) =>
        new(icao, "Aircraft", icao,
            new Dictionary<string, string>(callsign is null
                ? new Dictionary<string, string> { ["icao"] = icao }
                : new Dictionary<string, string> { ["icao"] = icao, ["callsign"] = callsign }),
            null, "ADS-B", 1.0, [new EvidenceItem("icao", icao, 1.0)]);

    private static InMemoryDeviceRepository New()
    {
        var repo = new InMemoryDeviceRepository(new FakeResolver());
        repo.ClearDemoSeed(); // start empty so assertions count only what we upsert
        return repo;
    }

    [Fact]
    public void Upsert_NewDevice_IsReturnedByGet()
    {
        var repo = New();
        repo.Upsert(Aircraft("4840D6", "KLM1023"));

        var got = Assert.Single(repo.GetDevices());
        Assert.Equal("4840D6", got.Id);
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
    }

    [Fact]
    public void Upsert_SameId_IsIdempotent_AndUpdatesInPlace()
    {
        var repo = New();
        repo.Upsert(Aircraft("4840D6"));                 // no callsign yet
        repo.Upsert(Aircraft("4840D6", "KLM1023"));      // later frame adds callsign

        var got = Assert.Single(repo.GetDevices());      // one entry, not two
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
    }

    [Fact]
    public void Upsert_OrdersNewestFirst()
    {
        var repo = New();
        repo.Upsert(Aircraft("AAAAAA"));
        repo.Upsert(Aircraft("BBBBBB"));

        var all = repo.GetDevices();
        Assert.Equal("BBBBBB", all[0].Id);   // most recent upsert first
        Assert.Equal("AAAAAA", all[1].Id);
    }

    [Fact]
    public void Upsert_SameId_Concurrently_KeepsOneEntry()
    {
        var repo = New();
        Parallel.For(0, 200, _ => repo.Upsert(Aircraft("4840D6")));
        Assert.Single(repo.GetDevices());
    }
}
