using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// Picks the analyst engine per query (SPEC §8.12): CloudAnalyst when cloud is enabled by config AND
/// the node is online AND an API key is present; otherwise the offline engine. Offline is the safe
/// default (NFR-R4), so any missing condition falls back to offline. If the cloud path is selected but
/// the live Claude call FAILS (network drop, timeout, API error), we degrade gracefully to the grounded
/// offline answer rather than surfacing an error — the platform is never blocked by connectivity (§4.3).
/// Implements <see cref="IAnalystEngine"/> so callers depend only on the analyst contract.
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

    public async Task<AnalystAnswer> AnswerAsync(AnalystQuery q, CancellationToken ct = default)
    {
        if (!UsesCloud)
            return await _offline.AnswerAsync(q, ct).ConfigureAwait(false);

        try
        {
            return await _cloud.AnswerAsync(q, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Graceful degrade (§4.3, NFR-R4): a failed cloud call never breaks the analyst — fall back
            // to the deterministic, grounded offline answer. Cancellation is honored (not swallowed).
            return await _offline.AnswerAsync(q, ct).ConfigureAwait(false);
        }
    }
}
