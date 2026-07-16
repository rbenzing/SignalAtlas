using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

/// <summary>Eval result: top-1 accuracy and macro-averaged F1 (SPEC §8.9, NFR-A2).</summary>
public sealed record EvalResult(double Top1Accuracy, double MacroF1);

/// <summary>
/// Eval harness (SPEC §8.9 / §12.5): scores a trained <see cref="MlModel"/> against a labeled test
/// set, computing top-1 accuracy and the macro-average of the per-class F1 scores. Deterministic —
/// runs the same inference path as <see cref="MlClassifier"/>.
/// </summary>
public static class Evaluator
{
    public static EvalResult Evaluate(MlModel model, IReadOnlyList<LabeledSample> testSet)
    {
        if (testSet.Count == 0) throw new ArgumentException("Empty test set.", nameof(testSet));

        var classifier = new MlClassifier(model);
        var labels = model.Labels;
        int k = labels.Length;
        var index = new Dictionary<string, int>();
        for (int i = 0; i < k; i++) index[labels[i]] = i;

        var tp = new int[k];
        var fp = new int[k];
        var fn = new int[k];
        int correct = 0;

        foreach (var sample in testSet)
        {
            string predicted = classifier.Classify(sample.Features).Protocol;
            int t = index[sample.Label];
            int p = index[predicted];
            if (p == t) { correct++; tp[t]++; }
            else { fp[p]++; fn[t]++; }
        }

        double top1 = (double)correct / testSet.Count;

        double f1Sum = 0;
        for (int c = 0; c < k; c++)
        {
            double precision = tp[c] + fp[c] == 0 ? 0.0 : (double)tp[c] / (tp[c] + fp[c]);
            double recall = tp[c] + fn[c] == 0 ? 0.0 : (double)tp[c] / (tp[c] + fn[c]);
            double f1 = precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);
            f1Sum += f1;
        }

        return new EvalResult(top1, f1Sum / k);
    }
}
