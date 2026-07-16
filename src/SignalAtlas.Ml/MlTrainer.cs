using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

/// <summary>
/// Trains a multinomial logistic-regression (softmax) classifier by seeded FULL-BATCH gradient
/// descent over standardized features (SPEC §8.9 "reproducible seeded training"). Numeric features
/// are z-scored using the training mean/std; the modulation hint is one-hot encoded (see
/// <see cref="FeatureEncoder"/>). Weights are initialized from a seeded <see cref="DeterministicRng"/>
/// and, because the batch gradient is order-independent, training is fully reproducible for a fixed
/// seed/epochs/lr (P5). Pure C#, no external ML packages.
///
/// This is honest "ML behind the seam": the production upgrade is a gradient-boosted tree or a
/// spectrogram CNN executed via ONNX Runtime, loaded into the same <see cref="MlModel"/>/
/// <see cref="IClassifier"/> contract with no caller changes.
/// </summary>
public static class MlTrainer
{
    public static MlModel Train(IReadOnlyList<LabeledSample> dataset, MlHyperparameters hyper)
    {
        if (dataset.Count == 0) throw new ArgumentException("Empty dataset.", nameof(dataset));

        var labels = SyntheticDataset.Protocols;
        int numClasses = labels.Length;
        int dim = FeatureEncoder.Dimension;
        int m = dataset.Count;

        // Encode raw rows and integer targets.
        var rawX = new double[m][];
        var y = new int[m];
        for (int i = 0; i < m; i++)
        {
            rawX[i] = FeatureEncoder.Encode(dataset[i].Features);
            y[i] = Array.IndexOf(labels, dataset[i].Label);
            if (y[i] < 0) throw new ArgumentException($"Unknown label '{dataset[i].Label}'.");
        }

        // Standardization baseline (per feature). Constant columns get std=1 to avoid /0.
        var means = new double[dim];
        var stds = new double[dim];
        for (int j = 0; j < dim; j++)
        {
            double sum = 0;
            for (int i = 0; i < m; i++) sum += rawX[i][j];
            means[j] = sum / m;
            double sq = 0;
            for (int i = 0; i < m; i++) { double d = rawX[i][j] - means[j]; sq += d * d; }
            stds[j] = Math.Sqrt(sq / m);
            if (stds[j] < 1e-9) stds[j] = 1.0;
        }

        // Standardize in place.
        var x = new double[m][];
        for (int i = 0; i < m; i++)
        {
            x[i] = new double[dim];
            for (int j = 0; j < dim; j++) x[i][j] = (rawX[i][j] - means[j]) / stds[j];
        }

        // Seeded small weight init (symmetry-breaking; batch GD keeps it reproducible).
        var rng = new DeterministicRng(unchecked((ulong)hyper.Seed));
        var w = new double[numClasses][];
        var b = new double[numClasses];
        for (int c = 0; c < numClasses; c++)
        {
            w[c] = new double[dim];
            for (int j = 0; j < dim; j++) w[c][j] = 0.01 * rng.NextGaussian();
        }

        double lr = hyper.LearningRate;
        double l2 = hyper.L2;
        var probs = new double[numClasses];

        for (int epoch = 0; epoch < hyper.Epochs; epoch++)
        {
            var gradW = new double[numClasses][];
            for (int c = 0; c < numClasses; c++) gradW[c] = new double[dim];
            var gradB = new double[numClasses];

            for (int i = 0; i < m; i++)
            {
                Softmax(w, b, x[i], probs);
                for (int c = 0; c < numClasses; c++)
                {
                    double err = probs[c] - (c == y[i] ? 1.0 : 0.0);
                    var gwc = gradW[c];
                    var xi = x[i];
                    for (int j = 0; j < dim; j++) gwc[j] += err * xi[j];
                    gradB[c] += err;
                }
            }

            for (int c = 0; c < numClasses; c++)
            {
                for (int j = 0; j < dim; j++)
                    w[c][j] -= lr * (gradW[c][j] / m + l2 * w[c][j]);
                b[c] -= lr * (gradB[c] / m);
            }
        }

        return new MlModel
        {
            Labels = (string[])labels.Clone(),
            FeatureNames = (string[])FeatureEncoder.FeatureNames.Clone(),
            Means = means,
            Stds = stds,
            Weights = w,
            Bias = b,
        };
    }

    /// <summary>Numerically-stable softmax of the class logits for one standardized row.</summary>
    internal static void Softmax(double[][] w, double[] b, double[] xStd, double[] outProbs)
    {
        int numClasses = w.Length;
        double max = double.NegativeInfinity;
        for (int c = 0; c < numClasses; c++)
        {
            double z = b[c];
            var wc = w[c];
            for (int j = 0; j < xStd.Length; j++) z += wc[j] * xStd[j];
            outProbs[c] = z;
            if (z > max) max = z;
        }

        double sum = 0;
        for (int c = 0; c < numClasses; c++)
        {
            double e = Math.Exp(outProbs[c] - max);
            outProbs[c] = e;
            sum += e;
        }
        for (int c = 0; c < numClasses; c++) outProbs[c] /= sum;
    }
}
