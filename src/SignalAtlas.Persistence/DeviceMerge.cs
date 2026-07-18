using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Merges an incoming determined device into the one already stored under the same id so identity
/// accumulates across blocks (SPEC §8.4): a later ICAO-only squitter must not erase a callsign an
/// earlier identification squitter contributed. Identifiers union (incoming wins on shared keys;
/// existing-only keys like callsign are preserved); evidence unions (deduped by value equality);
/// the incoming device's type/protocol/confidence win (latest determination); primary id / vendor
/// fall back to the existing value when the incoming one lacks them. Evidence stays non-empty (P4).
/// </summary>
internal static class DeviceMerge
{
    public static Device Merge(Device existing, Device incoming)
    {
        var identifiers = new Dictionary<string, string>(existing.Identifiers, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in incoming.Identifiers)
            identifiers[k] = v;

        var evidence = existing.Evidence.Concat(incoming.Evidence).Distinct().ToList();

        return incoming with
        {
            Identifiers = identifiers,
            Evidence = evidence,
            PrimaryIdentifier = incoming.PrimaryIdentifier ?? existing.PrimaryIdentifier,
            Vendor = incoming.Vendor ?? existing.Vendor,
        };
    }
}
