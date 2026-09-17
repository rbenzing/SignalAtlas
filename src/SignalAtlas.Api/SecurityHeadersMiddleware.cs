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
            // Defence-in-depth against injected script (OWASP A03/A05). The API serves JSON, not
            // markup, so it can afford the strictest possible policy: deny everything and forbid
            // framing. This covers the analyst/enhancement paths, which render model- and
            // decoder-derived strings. NOTE: this middleware guards the API only — the SPA is served
            // by Vite/your static host, which needs its own (necessarily looser) CSP.
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
