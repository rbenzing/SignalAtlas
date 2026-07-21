namespace SignalAtlas.Api;

/// <summary>Versioned message envelope (SPEC §7.5): every payload carries a schema version + correlation id.</summary>
/// <param name="NextCursor">
/// Opaque forward-pagination cursor for list endpoints; null when there is no next page (or the
/// endpoint isn't paginated). Additive field — defaults to null so existing call sites keep compiling.
/// </param>
public sealed record ApiEnvelope<T>(string SchemaVersion, Guid CorrelationId, T Payload, string? NextCursor = null)
{
    public const string CurrentSchemaVersion = "v1";
}
