using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory audit log (SPEC §4.3, §4.6 / NFR-S2). Append-only, monotonic ids.
/// Thread-safe (#10): every REST read is audited (see AuthorizationGateFilter), so concurrent
/// request handlers hit <see cref="Record"/> concurrently; guarded with a lock like the other
/// in-memory repos. Bounded (#10): unguarded growth would let a long-running offline session leak
/// memory one audited request at a time, so entries beyond <see cref="MaxEntries"/> are dropped
/// oldest-first while <see cref="_next"/> stays monotonic (ids are never reused).
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    /// <summary>Retained-entry cap; oldest entries are evicted once exceeded.</summary>
    public const int MaxEntries = 100_000;

    private readonly List<AuditEntry> _entries = [];
    private readonly object _gate = new();
    private long _next = 1;

    public void Record(string actor, string action, string query)
    {
        lock (_gate)
        {
            _entries.Add(new AuditEntry(_next++, DateTimeOffset.UtcNow, actor, action, query));
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries); // drop oldest
        }
    }

    // Most-recent-first, matching the EF store's Id-descending order.
    public IReadOnlyList<AuditEntry> Recent(int limit)
    {
        lock (_gate)
            return _entries.AsEnumerable().Reverse().Take(limit).ToList();
    }
}
