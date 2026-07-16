namespace SignalAtlas.Ml;

/// <summary>
/// Reproducible training hyperparameters (SPEC §8.9 "reproducible seeded training"). A fixed
/// <see cref="Seed"/> (weight init) plus fixed <see cref="Epochs"/>/<see cref="LearningRate"/>
/// yield identical weights on every run (P5).
/// </summary>
public sealed record MlHyperparameters(
    int Seed = 20260707,
    int Epochs = 500,
    double LearningRate = 0.5,
    double L2 = 1e-4)
{
    public static MlHyperparameters Default { get; } = new();
}
