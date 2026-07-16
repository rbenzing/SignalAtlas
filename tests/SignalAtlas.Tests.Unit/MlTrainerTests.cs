using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>M9 — reproducible seeded training (SPEC §8.9). Same seed/epochs/lr → identical weights.</summary>
public class MlTrainerTests
{
    [Fact]
    public void Train_SameSeedAndData_ProducesIdenticalWeights()
    {
        var ds = SyntheticDataset.Generate(seed: 99, n: 800);
        var hyper = new MlHyperparameters(Seed: 123, Epochs: 50, LearningRate: 0.3);

        var m1 = MlTrainer.Train(ds, hyper);
        var m2 = MlTrainer.Train(ds, hyper);

        Assert.Equal(m1.Labels, m2.Labels);
        Assert.Equal(m1.Bias, m2.Bias);
        for (int c = 0; c < m1.Weights.Length; c++)
            Assert.Equal(m1.Weights[c], m2.Weights[c]);
        Assert.Equal(m1.Means, m2.Means);
        Assert.Equal(m1.Stds, m2.Stds);
    }

    [Fact]
    public void Train_RoundTripsThroughJsonArtifact()
    {
        var ds = SyntheticDataset.Generate(seed: 5, n: 400);
        var model = MlTrainer.Train(ds, new MlHyperparameters(Epochs: 30));

        var restored = MlModel.FromJson(model.ToJson());

        Assert.Equal(model.Labels, restored.Labels);
        for (int c = 0; c < model.Weights.Length; c++)
            Assert.Equal(model.Weights[c], restored.Weights[c]);
    }
}
