using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// The honest capability fallback (SPEC §8.12): when a query cannot be mapped to a supported type,
/// the analyst NEVER guesses — it lists exactly what it can answer. Single source of the wording so
/// both engines stay consistent.
/// </summary>
public static class AnalystCapabilities
{
    /// <summary>Short human labels for each supported query type, in a stable order.</summary>
    public static readonly IReadOnlyList<string> SupportedDescriptions =
    [
        "what changed recently (recent alerts + new activity in a time window)",
        "emitters near a location (lat,lon + radius)",
        "unknown/unidentified emitters, optionally in a band",
        "counts by protocol (how many)",
        "list emitters/devices of a protocol (show/list)",
        "spectrum occupancy (busiest / usage)",
    ];

    /// <summary>The rendered fallback answer. Deterministic; no fabricated content.</summary>
    public static string FallbackText()
        => "I can't answer that. I can answer questions about: "
           + string.Join("; ", SupportedDescriptions) + ".";
}
