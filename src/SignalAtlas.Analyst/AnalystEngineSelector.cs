using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// Picks the analyst engine per query (SPEC §8.12): CloudAnalyst when cloud is enabled by config AND
/// the node is online AND an API key is present; otherwise the offline engine. Offline is the safe
/// default (NFR-R4), so any missing condition falls back to offline. Implements
/// <see cref="IAnalystEngine"/> so callers depend only on the analyst contract.
/// </summary>
public sealed class AnalystEngineSelector(
    OfflineAnalyst offline,
    CloudAnalyst cloud,
    IConnectivity connectivity,
    bool cloudEnabled,
    bool keyPresent) : IAnalystEngine
{
    private readonly OfflineAnalyst _offline = offline;
    private readonly CloudAnalyst _cloud = cloud;
    private readonly IConnectivity _connectivity = connectivity;
    private readonly bool _cloudEnabled = cloudEnabled;
    private readonly bool _keyPresent = keyPresent;

    /// <summary>True when all three conditions hold and the cloud engine should serve.</summary>
    public bool UsesCloud => _cloudEnabled && _keyPresent && _connectivity.IsOnline;

    public IAnalystEngine Selected => UsesCloud ? _cloud : _offline;

    public AnalystAnswer Answer(AnalystQuery q) => Selected.Answer(q);
}
