using Microsoft.AspNetCore.SignalR;
using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Real-time push hub (SPEC §9.3 <c>/hub/live</c>). Server → client broadcast only: the edge pipeline
/// produces artifacts and <see cref="SignalRLiveNotifier"/> fans them out to every connected client via
/// the client methods <c>signalCreated</c>, <c>emitterUpdated</c>, <c>alertRaised</c>, <c>spectrumFrame</c>,
/// <c>deviceDetermined</c> — each carrying the same camelCase DTO shape as the REST payloads.
///
/// AUTH: the hub is mapped OUTSIDE <c>/api/v1</c>, so it is not behind the AuthorizationGateFilter. That
/// follows the single-operator posture (SPEC §4.7): no login friction by default, loopback bind. Adding
/// token auth for the hub (e.g. [Authorize] + access_token query wiring) is the upgrade point.
/// </summary>
public sealed class LiveHub : Hub
{
}

/// <summary>
/// <see cref="ILiveNotifier"/> backed by SignalR: broadcasts each pipeline artifact to all clients via
/// <see cref="IHubContext{THub}"/>. Fire-and-forget (SPEC §9.3) — the pipeline hot loop is never blocked.
/// </summary>
public sealed class SignalRLiveNotifier(IHubContext<LiveHub> hub) : ILiveNotifier
{
    public void SignalCreated(Signal signal) => Broadcast("signalCreated", signal);
    public void EmitterUpdated(Emitter emitter) => Broadcast("emitterUpdated", emitter);
    public void AlertRaised(Alert alert) => Broadcast("alertRaised", alert);
    public void SpectrumFrame(SpectrumFrame frame) => Broadcast("spectrumFrame", frame);
    public void DeviceDetermined(Device device) => Broadcast("deviceDetermined", device);

    // Fire-and-forget: discard the Task so a slow/absent client never stalls the pipeline.
    private void Broadcast(string method, object payload) =>
        _ = hub.Clients.All.SendAsync(method, payload);
}
