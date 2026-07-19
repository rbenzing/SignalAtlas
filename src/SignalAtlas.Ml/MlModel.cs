using System.Text.Json;

namespace SignalAtlas.Ml;

/// <summary>
/// A trained multinomial logistic-regression (softmax) model — the serializable artifact loaded for
/// inference (SPEC §8.9). Holds the label set, the ordered feature names, the standardization
/// baseline (per-feature mean/std computed over the training set) and the weight matrix
/// (<c>Weights[class][feature]</c>) plus a per-class bias. Pure data + JSON round-trip; all math
/// lives in <see cref="MlTrainer"/> / <see cref="MlClassifier"/>. Serializes to a JSON artifact so a
/// GBM/CNN-via-ONNX upgrade can be swapped behind the same <see cref="Domain.IClassifier"/> seam.
/// </summary>
public sealed class MlModel
{
    public required string[] Labels { get; init; }
    public required string[] FeatureNames { get; init; }
    public required double[] Means { get; init; }
    public required double[] Stds { get; init; }

    /// <summary>Weights[class][feature], one row per label, aligned to <see cref="FeatureNames"/>.</summary>
    public required double[][] Weights { get; init; }

    /// <summary>Per-class bias term, aligned to <see cref="Labels"/>.</summary>
    public required double[] Bias { get; init; }

    /// <summary>
    /// Minimum standard deviation enforced on load — mirrors <c>MlTrainer</c>'s own clamp so a
    /// well-formed model's stds (already &gt;= this) are byte-identical, while a degenerate/hand-edited
    /// artifact (std == 0) can no longer produce Infinity/NaN at the standardization divide
    /// (<see cref="MlClassifier"/>, <see cref="DriftMonitor"/>).
    /// </summary>
    public const double MinStd = 1e-9;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static MlModel FromJson(string json)
    {
        var model = JsonSerializer.Deserialize<MlModel>(json)
            ?? throw new InvalidOperationException("MlModel JSON deserialized to null.");

        for (int j = 0; j < model.Stds.Length; j++)
            if (model.Stds[j] < MinStd) model.Stds[j] = MinStd;

        return model;
    }
}
