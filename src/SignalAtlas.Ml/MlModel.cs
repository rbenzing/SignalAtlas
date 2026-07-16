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

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static MlModel FromJson(string json) =>
        JsonSerializer.Deserialize<MlModel>(json)
        ?? throw new InvalidOperationException("MlModel JSON deserialized to null.");
}
