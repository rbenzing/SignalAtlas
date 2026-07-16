namespace SignalAtlas.Domain;

/// <summary>
/// Reads named secrets from the host's secret store (SPEC §4.6): OS secret store / environment /
/// user-secrets — NEVER the repo or the database. Returns <c>null</c> when a secret is unset so the
/// platform's offline-first default (no cloud uplift) holds. Implementations must never log values.
/// </summary>
public interface ISecretProvider
{
    string? Get(string name);
}
