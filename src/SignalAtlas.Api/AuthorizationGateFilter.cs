using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Endpoint filter that routes every request through <see cref="IAuthorizationGate"/> (SPEC §4.7).
/// Applied to the /api/v1 group so all routes inherit it; pairs with <see cref="AuthGateMarker"/>
/// so the route-gated contract test can prove coverage statically.
/// </summary>
public sealed class AuthorizationGateFilter(IAuthorizationGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!gate.Authorize())
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");

        context.HttpContext.Response.Headers["X-Authorization-Gate"] = gate.Scheme;
        return await next(context);
    }
}
