using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

public class LiveSessionTests
{
    private sealed class CountingStore : IDemoSeedStore
    {
        public int Cleared;
        public void ClearDemoSeed() => Cleared++;
    }

    [Fact]
    public void OnDeviceStreamStarted_ClearsEveryStoreOnce()
    {
        var a = new CountingStore();
        var b = new CountingStore();
        var session = new LiveSession([a, b]);

        session.OnDeviceStreamStarted();

        Assert.Equal(1, a.Cleared);
        Assert.Equal(1, b.Cleared);
    }

    [Fact]
    public void OnDeviceStreamStarted_IsLatched_ReconnectsDoNotReClear()
    {
        var store = new CountingStore();
        var session = new LiveSession([store]);

        session.OnDeviceStreamStarted();
        session.OnDeviceStreamStarted(); // reconnect
        session.OnDeviceStreamStarted(); // reconnect

        Assert.Equal(1, store.Cleared); // cleared exactly once
    }

    [Fact]
    public void InMemoryStores_ClearDemoSeed_EmptiesThem()
    {
        var signals = new InMemorySignalRepository();      // seeded with 1
        var emitters = new InMemoryEmitterRepository();     // seeded with 3
        Assert.NotEmpty(signals.GetSignals(10));
        Assert.NotEmpty(emitters.All());

        ((IDemoSeedStore)signals).ClearDemoSeed();
        ((IDemoSeedStore)emitters).ClearDemoSeed();

        Assert.Empty(signals.GetSignals(10));
        Assert.Empty(emitters.All());
    }
}
