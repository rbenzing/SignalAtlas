using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using SignalAtlas.Api;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// Contract tests for the real-time live hub (SPEC §9.3 <c>/hub/live</c>). Covers (1) the SignalR
/// negotiate endpoint is mapped, and (2) <see cref="SignalRLiveNotifier"/> broadcasts each event to
/// all clients with the exact camelCase client-method name + the same DTO payload as REST — verified
/// with a capturing fake <see cref="IHubContext{THub}"/> (mocking the real one is heavy).
/// </summary>
public class LiveHubTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task Negotiate_IsMapped_ReturnsOk()
    {
        var client = _factory.CreateClient();

        // The SignalR negotiate handshake is a POST with an empty body (SPEC §9.3 /hub/live).
        var resp = await client.PostAsync("/hub/live/negotiate", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", body);
    }

    [Fact]
    public void SignalCreated_BroadcastsSignalCreatedToAllClients_WithSignalPayload()
    {
        var proxy = new CapturingClientProxy();
        var notifier = new SignalRLiveNotifier(new FakeHubContext(new SingleTargetHubClients(proxy)));

        var sig = new Signal(
            Id: 1, Time: DateTimeOffset.UnixEpoch, ObservationId: 1, EmitterId: null, DeviceId: null,
            Protocol: "wifi", Confidence: 0.9, Classifier: "rule", Evidence: [new EvidenceItem("f", "v", 1.0)],
            CenterFreqHz: 2_412_000_000, BandwidthHz: 20_000_000, DurationMs: 5,
            Features: new Dictionary<string, double> { ["snr_db"] = 10.0 });

        notifier.SignalCreated(sig);

        var call = Assert.Single(proxy.Calls);
        Assert.Equal("signalCreated", call.Method);
        Assert.Same(sig, call.Arg);
    }

    [Fact]
    public void AlertRaised_BroadcastsAlertRaisedToAllClients_WithAlertPayload()
    {
        var proxy = new CapturingClientProxy();
        var notifier = new SignalRLiveNotifier(new FakeHubContext(new SingleTargetHubClients(proxy)));

        var alert = new Alert(
            Id: Guid.NewGuid(), Time: DateTimeOffset.UnixEpoch, EmitterId: "e1", DeviceId: null,
            Kind: Alert.NewEmitter, Severity: "info", Summary: "new emitter",
            Evidence: [new EvidenceItem("f", "v", 1.0)]);

        notifier.AlertRaised(alert);

        var call = Assert.Single(proxy.Calls);
        Assert.Equal("alertRaised", call.Method);
        Assert.Same(alert, call.Arg);
    }

    // --- Test doubles: capture the client method name + first arg passed to Clients.All ---

    private sealed class CapturingClientProxy : IClientProxy
    {
        public List<(string Method, object? Arg)> Calls { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }

    private sealed class SingleTargetHubClients(IClientProxy target) : IHubClients
    {
        public IClientProxy All { get; } = target;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Client(string connectionId) => All;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
        public IClientProxy Group(string groupName) => All;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
        public IClientProxy User(string userId) => All;
        public IClientProxy Users(IReadOnlyList<string> userIds) => All;
    }

    private sealed class FakeHubContext(IHubClients clients) : IHubContext<LiveHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }
}
