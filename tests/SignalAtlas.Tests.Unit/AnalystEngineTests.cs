using SignalAtlas.Analyst;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M12 (SPEC §8.12): the offline/cloud engines over the shared retrieval layer. Unsupported → honest
/// capability fallback listing supported types; supported → grounded cited answer; cloud delegates
/// phrasing to the (stubbed) Claude client while carrying the SAME citations. Selector picks offline
/// when disconnected, cloud when online + enabled + key present.
/// </summary>
public class AnalystEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private static AnalystRetrieval Retrieval(IEnumerable<Emitter> emitters)
        => new(
            new FakeSignalRepository([]),
            new FakeDeviceRepository([]),
            new FakeEmitterRepository(emitters),
            new FakeAlertRepository([]),
            new FakeSpectrumBuffer([]),
            new FixedClock(Now));

    private static readonly Emitter[] Emitters =
    [
        AnalystData.Emitter("e-wifi", "Wi-Fi", 2_437_000_000),
    ];

    [Fact]
    public void Offline_Unsupported_ListsSupportedCapabilities()
    {
        var engine = new OfflineAnalyst(new IntentClassifier(), Retrieval(Emitters));
        var ans = engine.Answer(new AnalystQuery("write me a poem"));

        Assert.Equal(AnalystAnswer.OfflineMode, ans.Mode);
        Assert.Equal(nameof(AnalystQueryType.Unsupported), ans.QueryType);
        Assert.Empty(ans.Citations);
        Assert.Contains("counts by protocol", ans.Text);
        Assert.Contains("occupancy", ans.Text);
    }

    [Fact]
    public void Offline_SupportedQuery_IsGroundedAndCited()
    {
        var engine = new OfflineAnalyst(new IntentClassifier(), Retrieval(Emitters));
        var ans = engine.Answer(new AnalystQuery("list wifi"));

        Assert.Equal(AnalystAnswer.OfflineMode, ans.Mode);
        Assert.Equal(nameof(AnalystQueryType.ListByProtocol), ans.QueryType);
        Assert.NotEmpty(ans.Citations);
    }

    [Fact]
    public void Cloud_SupportedQuery_UsesClaude_WithSameCitations()
    {
        var engine = new CloudAnalyst(new IntentClassifier(), Retrieval(Emitters), new StubClaudeClient());
        var ans = engine.Answer(new AnalystQuery("list wifi"));

        Assert.Equal(AnalystAnswer.CloudMode, ans.Mode);
        Assert.NotEmpty(ans.Citations);
        Assert.Contains("stub-claude", ans.Text);
    }

    [Fact]
    public void Cloud_EmptyResult_DoesNotFabricate_ViaClaude()
    {
        var engine = new CloudAnalyst(new IntentClassifier(), Retrieval([]), new ThrowingClaudeClient());
        var ans = engine.Answer(new AnalystQuery("list wifi"));

        Assert.Equal(AnalystAnswer.CloudMode, ans.Mode);
        Assert.Empty(ans.Citations);
        Assert.Contains("None found", ans.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selector_PicksOffline_WhenDisconnected()
    {
        var selector = BuildSelector(online: false, cloudEnabled: true, keyPresent: true);
        Assert.False(selector.UsesCloud);
        Assert.Equal(AnalystAnswer.OfflineMode, selector.Answer(new AnalystQuery("list wifi")).Mode);
    }

    [Fact]
    public void Selector_PicksCloud_WhenOnlineEnabledAndKeyPresent()
    {
        var selector = BuildSelector(online: true, cloudEnabled: true, keyPresent: true);
        Assert.True(selector.UsesCloud);
        Assert.Equal(AnalystAnswer.CloudMode, selector.Answer(new AnalystQuery("list wifi")).Mode);
    }

    [Theory]
    [InlineData(false, true)]  // enabled but no key
    [InlineData(true, false)]  // key but disabled
    public void Selector_FallsBackToOffline_WhenAnyConditionMissing(bool cloudEnabled, bool keyPresent)
    {
        var selector = BuildSelector(online: true, cloudEnabled: cloudEnabled, keyPresent: keyPresent);
        Assert.False(selector.UsesCloud);
    }

    private static AnalystEngineSelector BuildSelector(bool online, bool cloudEnabled, bool keyPresent)
    {
        var retrieval = Retrieval(Emitters);
        return new AnalystEngineSelector(
            new OfflineAnalyst(new IntentClassifier(), retrieval),
            new CloudAnalyst(new IntentClassifier(), retrieval, new StubClaudeClient()),
            new StaticConnectivity(online),
            cloudEnabled,
            keyPresent);
    }

    private sealed class ThrowingClaudeClient : IClaudeClient
    {
        public string Complete(string s, string u, IReadOnlyList<EvidenceItem> g)
            => throw new InvalidOperationException("Claude must not be called for an empty result (P6).");
    }
}
