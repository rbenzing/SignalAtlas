namespace SignalAtlas.Domain;

/// <summary>
/// An append-only audit record (SPEC §7.2 audit_log, §4.6, NFR-S2): who (<paramref name="Actor"/>)
/// did what (<paramref name="Action"/>) with which filter (<paramref name="Query"/>) and when.
/// </summary>
public sealed record AuditEntry(long Id, DateTimeOffset Time, string Actor, string Action, string Query);

/// <summary>
/// Append-only query audit log (SPEC §4.6 / NFR-S2). Every read of device identifiers/locations
/// is recorded via <see cref="Record"/>; <see cref="Recent"/> exposes the tail for inspection.
/// (Wiring audit onto the identifier/location read endpoints is a later slice — this is the store.)
/// </summary>
public interface IAuditLog
{
    void Record(string actor, string action, string query);
    IReadOnlyList<AuditEntry> Recent(int limit);
}
