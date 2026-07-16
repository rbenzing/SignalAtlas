using Microsoft.Extensions.Configuration;
using SignalAtlas.Api;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// Secrets provider + typed Claude API-key accessor (SPEC §4.6): key comes from configuration
/// (env / user-secrets), returns null when unset (offline default), and is NEVER logged.
/// </summary>
public class SecretsTests
{
    private static ISecretProvider Provider(params (string key, string val)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.key, p.val)))
            .Build();
        return new ConfigurationSecretProvider(config);
    }

    [Fact]
    public void Provider_ReturnsConfiguredValue()
    {
        var provider = Provider(("SignalAtlas:ClaudeApiKey", "sk-test-123"));
        Assert.Equal("sk-test-123", provider.Get("SignalAtlas:ClaudeApiKey"));
    }

    [Fact]
    public void Provider_ReturnsNull_WhenAbsent()
    {
        Assert.Null(Provider().Get("SignalAtlas:ClaudeApiKey"));
        Assert.Null(Provider().Get("does:not:exist"));
    }

    [Fact]
    public void ClaudeAccessor_ReadsPrimaryKey()
    {
        var accessor = new ClaudeApiKeyAccessor(Provider(("SignalAtlas:ClaudeApiKey", "sk-primary")));
        Assert.Equal("sk-primary", accessor.ApiKey);
        Assert.True(accessor.IsConfigured);
    }

    [Fact]
    public void ClaudeAccessor_FallsBackToEnvStyleKey()
    {
        var accessor = new ClaudeApiKeyAccessor(Provider(("CLAUDE_API_KEY", "sk-env")));
        Assert.Equal("sk-env", accessor.ApiKey);
    }

    [Fact]
    public void ClaudeAccessor_ReturnsNull_WhenUnset()
    {
        var accessor = new ClaudeApiKeyAccessor(Provider());
        Assert.Null(accessor.ApiKey);
        Assert.False(accessor.IsConfigured);
    }

    // Redaction (SPEC §4.6): the safe description reports presence but NEVER the key value, so nothing
    // written to a log/collector can leak the secret.
    [Fact]
    public void ClaudeAccessor_NeverLeaksValue_ToLogCollector()
    {
        const string secret = "sk-super-secret-9f3a";
        var accessor = new ClaudeApiKeyAccessor(Provider(("SignalAtlas:ClaudeApiKey", secret)));

        var logCollector = new List<string>();
        logCollector.Add(accessor.Describe()); // what a startup log line would emit.

        Assert.True(accessor.IsConfigured);
        Assert.DoesNotContain(logCollector, line => line.Contains(secret));
        Assert.Contains(logCollector, line => line.Contains("configured"));
    }
}
