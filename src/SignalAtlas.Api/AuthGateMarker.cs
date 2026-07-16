namespace SignalAtlas.Api;

/// <summary>
/// Endpoint metadata proving a route is behind the authorization gate. The route-gated
/// contract test enumerates endpoints and asserts every /api/v1 route carries this marker.
/// </summary>
public sealed class AuthGateMarker;
