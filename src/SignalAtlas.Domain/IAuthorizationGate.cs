namespace SignalAtlas.Domain;

/// <summary>
/// Every API route passes through this gate (SPEC §4.7, ADR-2). The default implementation is a
/// single-operator pass-through; adding token auth + RBAC later means implementing this seam,
/// not reworking controllers. A contract test asserts every /api/v1 route is gated.
/// </summary>
public interface IAuthorizationGate
{
    /// <summary>The name of the active gate, surfaced for observability/audit.</summary>
    string Scheme { get; }

    /// <summary>Returns true when the operator is authorized for the request.</summary>
    bool Authorize();
}

/// <summary>Default: single operator, no login friction (SPEC §4.7).</summary>
public sealed class SingleOperatorPassThroughGate : IAuthorizationGate
{
    public string Scheme => "single-operator";
    public bool Authorize() => true;
}
