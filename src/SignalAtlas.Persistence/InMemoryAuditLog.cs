using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory audit log (SPEC §4.3, §4.6 / NFR-S2). Append-only, monotonic ids.
/// Thread-safe (#10): every REST read is audited (see AuthorizationGateFilter), so concurrent
/// request handlers hit <see cref="Record"/> concurrently; guarded with a lock like the other
/// in-memory repos. Bounded (#10): unguarded growth would let a long-running offline session leak
/// memory one audited request at a time, so entries beyond <see cref="MaxEntries"/> are dropped
/// oldest-first while <see cref="_next"/> stays monotonic (ids are never reused). Backed by a
/// <see cref="LinkedList{T}"/> (oldest → newest), mirroring <see cref="InMemorySignalRepository"/> /
/// <see cref="InMemoryAlertRepository"/>, so eviction is O(1) <c>RemoveFirst()</c> per call instead of
/// an O(n) list shift once the cap is reached.
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    /// <summary>Retained-entry cap; oldest entries are evicted once exceeded.</summary>
    public const int MaxEntries = 100_000;

    private readonly LinkedList<AuditEntry> _entries = new(); // oldest → newest
    private readonly object _gate = new();
    private long _next = 1;

    public void Record(string actor, string action, string query)
    {
        lock (_gate)
        {
            _entries.AddLast(new AuditEntry(_next++, DateTimeOffset.UtcNow, actor, action, query));
            while (_entries.Count > MaxEntries)
                _entries.RemoveFirst(); // drop oldest — O(1) per removal
        }
    }

    // Most-recent-first, matching the EF store's Id-descending order.
    public IReadOnlyList<AuditEntry> Recent(int limit)
    {
        lock (_gate)
        {
            var result = new List<AuditEntry>(Math.Min(limit, _entries.Count));
            for (var node = _entries.Last; node is not null && result.Count < limit; node = node.Previous)
                result.Add(node.Value); // newest first
            return result;
        }
    }
}
