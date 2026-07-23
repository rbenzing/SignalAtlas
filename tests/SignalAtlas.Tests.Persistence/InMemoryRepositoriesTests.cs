using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// The in-memory repos are written per-block by the live ingestion pipeline while REST handlers read
/// them concurrently. Under continuous streaming (browser WebUSB) they must (1) surface the NEWEST
/// signals — not the seed/oldest, which showed the UI stuck in "demo mode" — (2) stay bounded so they
/// can't leak memory, and (3) be safe against concurrent add/read.
/// </summary>
public class InMemoryRepositoriesTests
{
    private static Signal Sig(long id) => new(
        Id: id,
        Time: DateTimeOffset.UnixEpoch.AddSeconds(id),
        ObservationId: id,
        EmitterId: null,
        DeviceId: null,
        Protocol: "Unknown",
        Confidence: 0.5,
        Classifier: "test",
        Evidence: [new EvidenceItem("feature", "value", 1.0)],
        CenterFreqHz: 915_000_000,
        BandwidthHz: 1_000,
        DurationMs: 1,
        Features: new Dictionary<string, double>());

    [Fact]
    public void Signals_GetSignals_ReturnsNewestFirst()
    {
        var repo = new InMemorySignalRepository(); // seeded with one older signal
        repo.Add(Sig(10));
        repo.Add(Sig(20)); // newest appended last

        var recent = repo.GetSignals(10);

        Assert.Equal(20, recent[0].Id); // newest first — NOT the seed
        Assert.Equal(10, recent[1].Id);
    }

    [Fact]
    public void Signals_AreBounded_EvictingOldest()
    {
        var repo = new InMemorySignalRepository();
        for (int i = 0; i < InMemorySignalRepository.Capacity + 50; i++)
            repo.Add(Sig(1000 + i));

        var all = repo.GetSignals(int.MaxValue);

        Assert.True(all.Count <= InMemorySignalRepository.Capacity,
            $"expected <= {InMemorySignalRepository.Capacity}, got {all.Count}");
        // Newest survives; the seed + earliest adds are evicted.
        Assert.Equal(1000 + InMemorySignalRepository.Capacity + 49, all[0].Id);
    }

    [Fact]
    public async Task Signals_ConcurrentAddAndRead_DoNotThrow()
    {
        var repo = new InMemorySignalRepository();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        long id = 0;
        var writer = Task.Run(() => { while (!cts.IsCancellationRequested) repo.Add(Sig(id++)); });
        var reader = Task.Run(() => { while (!cts.IsCancellationRequested) _ = repo.GetSignals(100); });

        await Task.WhenAll(writer, reader); // must not throw "Collection was modified during enumeration"
    }

    [Fact]
    public void Signals_Clear_EmptiesTheStore()
    {
        // ITransientStore.Clear() — used on retune (SPEC §8.1) so a new band starts clean.
        var repo = new InMemorySignalRepository(seedDemo: true);
        repo.Add(Sig(1));
        Assert.True(repo.Count() > 0);

        ((ITransientStore)repo).Clear();

        Assert.Equal(0, repo.Count());
    }

    [Fact]
    public void Emitters_Clear_EmptiesTheStore()
    {
        var repo = new InMemoryEmitterRepository(seedDemo: true);
        Assert.True(repo.All().Count > 0);

        ((ITransientStore)repo).Clear();

        Assert.Empty(repo.All());
    }

    [Fact]
    public void Alerts_Clear_EmptiesTheStore()
    {
        var repo = new InMemoryAlertRepository(new StubAnomalyEngine(), seedDemo: true);
        Assert.True(repo.GetAlerts().Count > 0);

        ((ITransientStore)repo).Clear();

        Assert.Empty(repo.GetAlerts());
    }

    private sealed class StubAnomalyEngine : IAnomalyEngine
    {
        public IReadOnlyList<Alert> Evaluate(AnomalyEvent evt, EmitterBaseline baseline) =>
        [
            new Alert(Guid.NewGuid(), evt.Time, evt.EmitterId, evt.DeviceId, Alert.NewEmitter, "info",
                "seed", [new EvidenceItem("seed", "seed", 1.0)])
        ];
    }

    [Fact]
    public void DeviceRepository_DoesNotImplementITransientStore()
    {
        // Devices are persistent identity (SPEC/CLAUDE.md): retune-clear must NEVER touch them.
        Assert.False(typeof(InMemoryDeviceRepository).IsAssignableTo(typeof(ITransientStore)));
    }

    [Fact]
    public void ObservationRepository_DoesNotImplementITransientStore()
    {
        Assert.False(typeof(InMemoryObservationRepository).IsAssignableTo(typeof(ITransientStore)));
    }

    [Fact]
    public void Observations_AreBounded_AndReturnNewestFirst()
    {
        var repo = new InMemoryObservationRepository();
        for (int i = 0; i < InMemoryObservationRepository.Capacity + 50; i++)
            repo.Add(new Observation(
                Time: DateTimeOffset.UnixEpoch.AddSeconds(i),
                TimeSource: TimeSource.Host,
                CollectorId: "c",
                Seq: i,
                FrequencyHz: 915_000_000,
                BandwidthHz: 1_000,
                Power: -30,
                PowerRef: PowerRef.Relative,
                SnrDb: null,
                Latitude: null,
                Longitude: null,
                PositionQuality: PositionQuality.None,
                IqRef: null,
                CorrelationId: Guid.Empty));

        var recent = repo.GetRecent(10);

        Assert.Equal(10, recent.Count);
        Assert.True(recent[0].Seq > recent[1].Seq, "newest first");
        Assert.Equal(InMemoryObservationRepository.Capacity + 49, recent[0].Seq);
    }
}
