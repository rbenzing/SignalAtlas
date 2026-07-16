using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>Offline-first in-memory audit log (SPEC §4.3, §4.6 / NFR-S2). Append-only, monotonic ids.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];
    private long _next = 1;

    public void Record(string actor, string action, string query) =>
        _entries.Add(new AuditEntry(_next++, DateTimeOffset.UtcNow, actor, action, query));

    // Most-recent-first, matching the EF store's Id-descending order.
    public IReadOnlyList<AuditEntry> Recent(int limit) =>
        _entries.AsEnumerable().Reverse().Take(limit).ToList();
}
