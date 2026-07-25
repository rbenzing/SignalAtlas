using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// The cloud NL analyst (SPEC §8.12): same intent + deterministic retrieval/tool + citation layer as
/// <see cref="OfflineAnalyst"/>, but delegates the free-form PHRASING to Claude via
/// <see cref="IClaudeClient"/> using <paramref name="model"/> (default <see cref="ClaudeModels.AnalystDefault"/>).
/// The Claude call is grounded on the SAME retrieved records/citations, and the answer carries those same
/// citations (P6). Mode is "cloud". If retrieval found nothing, Claude is NOT invoked — we return the
/// honest "None found." with no citations (no fabrication).
/// </summary>
public sealed class CloudAnalyst(
    IIntentClassifier classifier,
    IAnalystRetrieval retrieval,
    IClaudeClient claude,
    string? model = null) : IAnalystEngine
{
    private const string SystemPrompt =
        "You are Signal Atlas's spectrum analyst. Answer ONLY from the provided grounding records. "
        + "Cite them. If they do not support a claim, say so. Never fabricate.";

    private readonly IIntentClassifier _classifier = classifier;
    private readonly IAnalystRetrieval _retrieval = retrieval;
    private readonly IClaudeClient _claude = claude;
    private readonly string _model = string.IsNullOrWhiteSpace(model) ? ClaudeModels.AnalystDefault : model!;

    public async Task<AnalystAnswer> AnswerAsync(AnalystQuery q, CancellationToken ct = default)
    {
        var intent = _classifier.Classify(q.Text ?? string.Empty);

        if (intent.QueryType == AnalystQueryType.Unsupported)
            return new AnalystAnswer(
                AnalystCapabilities.FallbackText(),
                [],
                AnalystAnswer.CloudMode,
                AnalystQueryType.Unsupported.ToString());

        var result = _retrieval.Retrieve(intent);

        // P6: no cited record ⇒ no claim. Don't let Claude invent an answer over an empty result.
        if (result.Citations.Count == 0)
            return new AnalystAnswer(
                result.Answer,
                [],
                AnalystAnswer.CloudMode,
                intent.QueryType.ToString());

        var text = await _claude
            .CompleteAsync(_model, SystemPrompt, q.Text ?? string.Empty, result.Citations, ct)
            .ConfigureAwait(false);
        return new AnalystAnswer(
            text,
            result.Citations,
            AnalystAnswer.CloudMode,
            intent.QueryType.ToString());
    }
}
