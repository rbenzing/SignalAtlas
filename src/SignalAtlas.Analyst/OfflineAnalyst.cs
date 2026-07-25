using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// The offline NL analyst (SPEC §8.12, §4.3) — NO LLM. Runs intent classification → the shared
/// deterministic retrieval/tool layer → a template-rendered grounded answer. Unsupported phrasing →
/// an honest capability fallback that LISTS what it can answer (never a guess). Mode is always
/// "offline". Grounded by construction: citations come straight from the retrieval layer (P6).
/// </summary>
public sealed class OfflineAnalyst(IIntentClassifier classifier, IAnalystRetrieval retrieval) : IAnalystEngine
{
    private readonly IIntentClassifier _classifier = classifier;
    private readonly IAnalystRetrieval _retrieval = retrieval;

    // No LLM: the work is synchronous, wrapped in a completed task to satisfy the async contract.
    public Task<AnalystAnswer> AnswerAsync(AnalystQuery q, CancellationToken ct = default)
    {
        var intent = _classifier.Classify(q.Text ?? string.Empty);

        if (intent.QueryType == AnalystQueryType.Unsupported)
            return Task.FromResult(new AnalystAnswer(
                AnalystCapabilities.FallbackText(),
                [],
                AnalystAnswer.OfflineMode,
                AnalystQueryType.Unsupported.ToString()));

        var result = _retrieval.Retrieve(intent);
        return Task.FromResult(new AnalystAnswer(
            result.Answer,
            result.Citations,
            AnalystAnswer.OfflineMode,
            intent.QueryType.ToString()));
    }
}
