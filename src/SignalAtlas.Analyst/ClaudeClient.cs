using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// Seam for the Claude API (SPEC §8.12 CloudAnalyst). The cloud analyst hands the SAME retrieved
/// records + citations (the <paramref name="groundingRecords"/>) to Claude for free-form phrasing —
/// still grounded and cited (P6). The live HTTP client (M13) reads the API key via
/// <see cref="ISecretProvider"/> and posts only structured metadata (§4.2 L7); it is out of scope
/// here — this milestone builds the seam + a deterministic stub + the citation-passing.
/// </summary>
public interface IClaudeClient
{
    string Complete(string systemPrompt, string userPrompt, IReadOnlyList<EvidenceItem> groundingRecords);
}

/// <summary>
/// Deterministic offline stub of <see cref="IClaudeClient"/> (SPEC §8.12): used when no API key is
/// present so tests never hit the network. Returns a templated, grounded string that references the
/// same cited records — never fabricated content beyond what was grounded.
/// </summary>
public sealed class StubClaudeClient : IClaudeClient
{
    public string Complete(string systemPrompt, string userPrompt, IReadOnlyList<EvidenceItem> groundingRecords)
    {
        var ids = string.Join(", ", groundingRecords.Select(c => $"{c.Feature}:{c.Value}"));
        return $"[stub-claude] Grounded on {groundingRecords.Count} record(s): {ids}.";
    }
}
