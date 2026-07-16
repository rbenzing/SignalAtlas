namespace SignalAtlas.Api;

/// <summary>Versioned message envelope (SPEC §7.5): every payload carries a schema version + correlation id.</summary>
public sealed record ApiEnvelope<T>(string SchemaVersion, Guid CorrelationId, T Payload)
{
    public const string CurrentSchemaVersion = "v1";
}
