namespace SignalAtlas.Api;

/// <summary>
/// Adds conservative response security headers to every API response (SPEC §4.6/§4.7 safe defaults):
/// <c>X-Content-Type-Options: nosniff</c> (no MIME sniffing), <c>X-Frame-Options: DENY</c> (no
/// framing/clickjacking), and <c>Referrer-Policy: no-referrer</c> (no referrer leakage). Set before
/// the response starts so they apply to success and problem-details responses alike.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
