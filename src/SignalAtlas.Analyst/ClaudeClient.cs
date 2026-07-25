using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// Seam for the Claude API (SPEC §8.12 CloudAnalyst, §8.13 enhancement). A cloud path hands the SAME
/// retrieved records + citations (the <paramref name="groundingRecords"/>) to Claude for free-form
/// phrasing/reasoning — still grounded and cited (P6). The call is <b>async</b> because the live impl
/// (see <c>AnthropicClaudeClient</c> in the API layer) is a network call; it reads the API key via
/// <see cref="ISecretProvider"/> and posts only structured metadata (§4.2 L7). The <paramref name="model"/>
/// is chosen by the caller (analyst default vs the per-run enhancement model — <see cref="ClaudeModels"/>).
/// </summary>
public interface IClaudeClient
{
    Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<EvidenceItem> groundingRecords,
        CancellationToken ct = default);
}

/// <summary>
/// Deterministic offline stub of <see cref="IClaudeClient"/> (SPEC §8.12): used when cloud is disabled /
/// no API key is present, so tests never hit the network. Returns — synchronously via a completed task —
/// a templated, grounded string that references the same cited records; never fabricated content beyond
/// what was grounded, and identical output for identical input (P5).
/// </summary>
public sealed class StubClaudeClient : IClaudeClient
{
    public Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<EvidenceItem> groundingRecords,
        CancellationToken ct = default)
    {
        var ids = string.Join(", ", groundingRecords.Select(c => $"{c.Feature}:{c.Value}"));
        return Task.FromResult($"[stub-claude] Grounded on {groundingRecords.Count} record(s): {ids}.");
    }
}
