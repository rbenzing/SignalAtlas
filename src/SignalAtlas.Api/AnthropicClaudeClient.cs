using Anthropic;
using Anthropic.Models.Messages;
using SignalAtlas.Analyst;
using SignalAtlas.Domain;
using SignalAtlas.Enhancement;

namespace SignalAtlas.Api;

/// <summary>
/// The live <see cref="IClaudeClient"/> (SPEC §8.12 CloudAnalyst / §8.13 enhancement, M13) — the real
/// HTTP call to the Claude API via the official <c>Anthropic</c> SDK. Lives at the composition root (not
/// the reusable Analyst library) so the SDK dependency stays at the edge. Registered ONLY when cloud is
/// enabled + a key is present; tests and the offline default keep the <see cref="StubClaudeClient"/>, so
/// the deterministic core and AC-DA0 never depend on it.
/// <para>
/// Controlled egress (§4.2 L7, invariant #3): only structured RF metadata leaves the boundary. The
/// grounding records are {record-type, record-id} evidence — this asserts none of their keys/values name
/// raw IQ or cleartext payload (belt-and-suspenders; the <see cref="EgressGuard"/> owns the enhancement
/// payload check). Non-determinism (Claude's reply) is quarantined to this overlay, never the core (P5).
/// </para>
/// </summary>
public sealed class AnthropicClaudeClient : IClaudeClient
{
    // Analyst phrasing / reclassification rationales are short — a modest cap bounds cost and latency.
    private const int MaxTokens = 1024;

    private readonly IAnthropicClient _client;
    private readonly TimeSpan _timeout;

    public AnthropicClaudeClient(string apiKey, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A Claude API key is required for the live client.", nameof(apiKey));

        // The SDK reads ANTHROPIC_API_KEY by default; we inject the operator's key (resolved via
        // ISecretProvider) explicitly so it is never sourced from ambient env by accident.
        _client = new AnthropicClient { ApiKey = apiKey };
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<EvidenceItem> groundingRecords,
        CancellationToken ct = default)
    {
        AssertGroundingEgressSafe(groundingRecords);

        var content = BuildUserContent(userPrompt, groundingRecords);

        // Bound the network call so a wedged connection never blocks the edge (§4.3, NFR-R4). The caller's
        // token still cancels; this only ADDS a ceiling. On failure the selector degrades to offline.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        var response = await _client.Messages.Create(
            new MessageCreateParams
            {
                Model = string.IsNullOrWhiteSpace(model) ? ClaudeModels.AnalystDefault : model,
                MaxTokens = MaxTokens,
                System = systemPrompt,
                Messages = [new() { Role = Role.User, Content = content }],
                // NB: temperature/top_p/top_k are rejected on the Claude 5 family — do not set them.
            },
            cancellationToken: timeoutCts.Token).ConfigureAwait(false);

        var text = string.Join(
            "\n",
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        return string.IsNullOrWhiteSpace(text)
            ? "[claude] (no textual content returned)"
            : text;
    }

    // Compose the query with a compact, metadata-only grounding block so Claude cites exactly these records.
    private static string BuildUserContent(string userPrompt, IReadOnlyList<EvidenceItem> groundingRecords)
    {
        if (groundingRecords.Count == 0)
            return userPrompt;

        var grounding = string.Join("\n", groundingRecords.Select(r => $"- {r.Feature}: {r.Value}"));
        return $"{userPrompt}\n\nGrounding records (cite only these):\n{grounding}";
    }

    // §4.2 L7 / invariant #3: nothing that names raw IQ or cleartext payload may cross the boundary.
    private static void AssertGroundingEgressSafe(IReadOnlyList<EvidenceItem> groundingRecords)
    {
        foreach (var r in groundingRecords)
            if (EgressGuard.IsForbiddenKey(r.Feature) || EgressGuard.IsForbiddenKey(r.Value))
                throw new InvalidOperationException(
                    "Grounding record names raw IQ / cleartext content (SPEC §4.2 L7 controlled egress).");
    }
}
