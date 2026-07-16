using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Correlation-id plumbing (SPEC §5.5 NFR-R2): one id follows an observation/request through the
/// pipeline. Read from the inbound <c>X-Correlation-ID</c> header when supplied, otherwise derived
/// deterministically from the trace id (SPEC §6.1 P5). Surfaced on the response header, the logging
/// scope, and the <see cref="ApiEnvelope{T}"/>.
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "SignalAtlas.CorrelationId";

    /// <summary>The correlation id for the current request (middleware sets it; falls back to the trace id).</summary>
    public static Guid For(HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var v) && v is Guid g ? g : DeterministicGuid.From(ctx.TraceIdentifier);

    internal static Guid Resolve(HttpContext ctx)
    {
        var header = ctx.Request.Headers[HeaderName].ToString();
        Guid id;
        if (!string.IsNullOrWhiteSpace(header))
            id = Guid.TryParse(header, out var parsed) ? parsed : DeterministicGuid.From(header);
        else
            id = DeterministicGuid.From(ctx.TraceIdentifier);

        ctx.Items[ItemKey] = id;
        return id;
    }
}

/// <summary>
/// Establishes the request correlation id (SPEC §5.5 NFR-R2): resolves it, opens a logging scope,
/// and echoes it on the <c>X-Correlation-ID</c> response header (via OnStarting so it survives a
/// response reset by the exception handler).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = CorrelationId.Resolve(ctx);
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[CorrelationId.HeaderName] = id.ToString();
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await next(ctx);
    }
}
