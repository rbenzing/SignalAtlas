namespace SignalAtlas.Domain;

/// <summary>A free-text natural-language question for the spectrum analyst (SPEC §8.12, §9.2).</summary>
public sealed record AnalystQuery(string Text);

/// <summary>
/// A grounded, cited answer from the NL analyst (SPEC §8.12, P6). <see cref="Citations"/> cite the
/// exact stored records the answer is built from (reusing <see cref="EvidenceItem"/> as
/// {feature = record-type, value = record-id, weight}); NO cited record ⇒ no claim. <see cref="Mode"/>
/// is <c>offline</c> (pattern/ML, no LLM) or <c>cloud</c> (Claude phrasing, still grounded).
/// </summary>
public sealed record AnalystAnswer(
    string Text,
    IReadOnlyList<EvidenceItem> Citations,
    string Mode,
    string QueryType)
{
    public const string OfflineMode = "offline";
    public const string CloudMode = "cloud";
}

/// <summary>The enumerated set of query types the analyst supports (SPEC §8.12). Unrecognized → Unsupported.</summary>
public enum AnalystQueryType
{
    /// <summary>Recent alerts + newly-seen activity in a time window ("what changed today").</summary>
    WhatChanged,

    /// <summary>Emitters within a radius of a coordinate ("near X"), via haversine.</summary>
    NearLocation,

    /// <summary>Emitters/signals of protocol Unknown, optionally band-filtered ("unknown in band Y").</summary>
    UnknownInBand,

    /// <summary>Counts grouped by protocol ("how many", "count").</summary>
    CountByProtocol,

    /// <summary>Emitters/devices of a named protocol ("show/list wifi").</summary>
    ListByProtocol,

    /// <summary>Spectrum occupancy summary from the PSD buffer ("busiest", "occupancy").</summary>
    Occupancy,

    /// <summary>Phrasing the classifier cannot map to a supported query → honest capability fallback.</summary>
    Unsupported,
}

/// <summary>
/// A classified query type + extracted slots (SPEC §8.12). Produced by the deterministic intent
/// classifier and consumed by <see cref="IAnalystRetrieval"/>. Slots are null when not present.
/// </summary>
public sealed record AnalystIntent(
    AnalystQueryType QueryType,
    TimeSpan? Window = null,
    double? Latitude = null,
    double? Longitude = null,
    double? RadiusMeters = null,
    string? Protocol = null,
    long? BandLowHz = null,
    long? BandHighHz = null);

/// <summary>
/// The result of the deterministic retrieval/tool layer (SPEC §8.12): a template-rendered grounded
/// <see cref="Answer"/>, the <see cref="Citations"/> for EVERY record used (non-empty unless nothing
/// matched), and the number of records used. Empty result ⇒ "None found." + empty citations (P6).
/// </summary>
public sealed record AnalystRetrievalResult(
    string Answer,
    IReadOnlyList<EvidenceItem> Citations,
    int RecordCount);

/// <summary>
/// The shared deterministic tool + citation layer (SPEC §8.12). Queries the repos per query type +
/// slots and returns the matched records as a grounded, template-rendered answer with citations.
/// Used identically by the offline and cloud analysts so both are grounded by construction (P6).
/// </summary>
public interface IAnalystRetrieval
{
    AnalystRetrievalResult Retrieve(AnalystIntent intent);
}

/// <summary>
/// The NL spectrum analyst (SPEC §8.12). Two impls share one <see cref="IAnalystRetrieval"/> layer:
/// an offline pattern/ML engine (no LLM) and a Claude-backed cloud engine. Auto-selected by
/// connectivity + config. Every answer is grounded and cited (P6).
/// </summary>
public interface IAnalystEngine
{
    Task<AnalystAnswer> AnswerAsync(AnalystQuery q, CancellationToken ct = default);
}
