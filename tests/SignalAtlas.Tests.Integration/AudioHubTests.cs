using SignalAtlas.Api;
using SignalAtlas.Domain;
using SignalAtlas.Processing;
using Xunit;

namespace SignalAtlas.Tests.Integration;

// RF Audio Player tap (design §2/§3/§6): AudioHub unit tests. Lives under Tests.Integration (not
// Tests.Unit) because AudioHub is defined in SignalAtlas.Api, which only Tests.Integration
// references.
public class AudioHubTests
{
    // 25 kHz == AudioDemodulator.TargetAudioRateHz: exact-decimation passthrough (rawDecimation=1,
    // resampleRatio=1), so tiny blocks produce audio without needing a resampler warm-up.
    private const int AudioRateHz = 25_000;

    private static IqBlock Block(int n = 64)
    {
        var i = new float[n];
        var q = new float[n];
        double phase = 0.0;
        for (int k = 0; k < n; k++)
        {
            i[k] = (float)Math.Cos(phase);
            q[k] = (float)Math.Sin(phase);
            phase += 2 * Math.PI * 1000.0 / AudioRateHz; // a mild 1 kHz FM-ish tone, not zero-signal
        }
        return new IqBlock(100_000_000, AudioRateHz, i, q);
    }

    [Fact]
    public void Accept_NoSubscribers_IsNoOp_NoThrow()
    {
        var hub = new AudioHub();
        hub.Configure(AudioMode.Wbfm, enabled: true);

        var ex = Record.Exception(() => hub.Accept(Block()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Accept_Disabled_SubscriberReceivesNothing()
    {
        var hub = new AudioHub();
        var sub = hub.Register();
        // Never enabled (default false).

        hub.Accept(Block());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await sub.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Accept_EnabledWithSubscriber_YieldsPcmBytesOnSubscriber()
    {
        var hub = new AudioHub();
        var sub = hub.Register();
        hub.Configure(AudioMode.Wbfm, enabled: true);

        hub.Accept(Block());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] pcm = await sub.ReadAsync(cts.Token);

        Assert.NotEmpty(pcm);
    }

    [Fact]
    public async Task Accept_FansOutToAllSubscribers()
    {
        var hub = new AudioHub();
        var subA = hub.Register();
        var subB = hub.Register();
        hub.Configure(AudioMode.Am, enabled: true);

        hub.Accept(Block());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] a = await subA.ReadAsync(cts.Token);
        byte[] b = await subB.ReadAsync(cts.Token);

        Assert.NotEmpty(a);
        Assert.Equal(a, b); // Both subscribers fanned the SAME demodulated PCM chunk.
    }

    [Fact]
    public async Task Unregister_CompletesTheSubscriberQueue()
    {
        var hub = new AudioHub();
        var sub = hub.Register();
        hub.Configure(AudioMode.Wbfm, enabled: true);

        hub.Unregister(sub);

        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(
            async () => await sub.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ModeSwitch_RebuildsDemodulator_MatchesFreshInstanceOutput()
    {
        // Feed an FM-shaped block under Wbfm first so the shared demod picks up real DSP state
        // (discriminator phase, de-emphasis, resampler tail), THEN switch to Am. If the switch
        // didn't rebuild the demod, the stale FM state would corrupt (or the mode field would
        // mismatch) the AM output. The hub's post-switch output must equal what a brand-new
        // AudioDemodulator(Am) produces for the same block -- proving the swap started clean.
        var hub = new AudioHub();
        var sub = hub.Register();
        hub.Configure(AudioMode.Wbfm, enabled: true);
        hub.Accept(Block());
        using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await sub.ReadAsync(cts1.Token); // drain the WBFM chunk

        hub.Configure(AudioMode.Am, enabled: true);
        var amBlock = Block();
        hub.Accept(amBlock);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] fromHub = await sub.ReadAsync(cts.Token);
        byte[] fromFresh = new AudioDemodulator(AudioMode.Am).Demodulate(amBlock);

        Assert.Equal(fromFresh, fromHub);
    }

    [Fact]
    public async Task DropOldest_BoundHolds_ExcessPushesAreCountedAsDrops()
    {
        var hub = new AudioHub();
        var sub = hub.Register();
        hub.Configure(AudioMode.Wbfm, enabled: true);

        // Never drain: push well past the bounded subscriber queue capacity so drop-oldest kicks in.
        for (int n = 0; n < 200; n++)
            hub.Accept(Block());

        Assert.True(sub.Drops > 0, "expected saturation drops to be counted once the bounded queue filled");

        // The queue itself is still healthy (bounded, not unbounded growth, not closed).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] pcm = await sub.ReadAsync(cts.Token);
        Assert.NotEmpty(pcm);
    }
}
