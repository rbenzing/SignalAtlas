using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SignalAtlas.Api;

/// <summary>
/// Converts any unhandled exception into an RFC 7807 <c>application/problem+json</c> response
/// (SPEC §9.4): status 500, a generic title/detail, and the request correlationId. Deliberately
/// leaks NO internal detail — no exception message, type name, or stack trace crosses the wire
/// (5xx leak nothing). The exception itself is logged server-side for diagnosis.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        var correlationId = CorrelationId.For(ctx);
        logger.LogError(exception, "Unhandled exception (correlationId {CorrelationId})", correlationId);

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.Headers[CorrelationId.HeaderName] = correlationId.ToString();

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://httpstatuses.io/500",
                Detail = "The server encountered an internal error and could not complete the request.",
            },
        });
    }
}
