using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Default <see cref="ISecretProvider"/> (SPEC §4.6): reads secrets from
/// <see cref="IConfiguration"/> — which on the host layers environment variables and .NET
/// user-secrets over appsettings, so the OS secret store / env is authoritative and NO secret is
/// ever committed to the repo. Returns <c>null</c> for an unset secret (offline-first default).
/// Never logs the value.
/// </summary>
public sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    public string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = _configuration[name];
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>
/// Typed accessor for the optional Claude API key (SPEC §4.6, §4.10). Reads
/// <c>SignalAtlas:ClaudeApiKey</c> first, then the conventional <c>CLAUDE_API_KEY</c> env var, via
/// <see cref="ISecretProvider"/>. Returns <c>null</c> when unset — the platform stays fully offline
/// (cloud uplift disabled). The value is NEVER logged and NEVER written to the repo/DB.
/// </summary>
public sealed class ClaudeApiKeyAccessor(ISecretProvider secrets)
{
    /// <summary>Primary configuration key (user-secrets / appsettings section).</summary>
    public const string PrimaryKey = "SignalAtlas:ClaudeApiKey";

    /// <summary>Fallback environment-variable style key.</summary>
    public const string FallbackKey = "CLAUDE_API_KEY";

    private readonly ISecretProvider _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    /// <summary>The API key, or <c>null</c> when unset (offline default). Callers must never log it.</summary>
    public string? ApiKey => _secrets.Get(PrimaryKey) ?? _secrets.Get(FallbackKey);

    /// <summary>True when a key is configured and cloud uplift MAY be enabled by the operator.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    /// <summary>
    /// A redaction-SAFE status string for logging/telemetry — reports only presence, NEVER the value.
    /// Use this instead of the key anywhere a value might be logged (SPEC §4.6).
    /// </summary>
    public string Describe() => IsConfigured ? "Claude API key: configured" : "Claude API key: not configured";
}
