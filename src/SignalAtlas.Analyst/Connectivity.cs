namespace SignalAtlas.Analyst;

/// <summary>
/// Whether the edge node currently has upstream connectivity (SPEC §4.3). The analyst uses this to
/// decide offline (default) vs cloud. The edge is fully operational offline (NFR-R4), so the safe
/// default impl reports offline.
/// </summary>
public interface IConnectivity
{
    bool IsOnline { get; }
}

/// <summary>Offline-first default (SPEC §4.3): always reports offline, so tests never reach the network.</summary>
public sealed class AlwaysOfflineConnectivity : IConnectivity
{
    public bool IsOnline => false;
}

/// <summary>A fixed connectivity state, for composition/tests.</summary>
public sealed class StaticConnectivity(bool online) : IConnectivity
{
    public bool IsOnline { get; } = online;
}
