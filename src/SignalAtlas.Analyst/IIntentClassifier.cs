using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// Deterministic keyword/pattern intent classifier + slot extraction (SPEC §8.12) — NO LLM.
/// Maps free text to a supported <see cref="AnalystQueryType"/> plus extracted slots, or Unsupported.
/// </summary>
public interface IIntentClassifier
{
    AnalystIntent Classify(string text);
}
