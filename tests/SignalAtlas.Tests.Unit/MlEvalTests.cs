using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M9 — eval harness on a held-out synthetic split (SPEC §8.9). We assert a SYNTHETIC FLOOR
/// (top-1 ≥ 0.80, macro-F1 ≥ 0.75), NOT the NFR-A2 target (≥95% / 0.90-F1). Per §12.2 accuracy
/// phasing, NFR-A2 is gated on decode-self-labeled field-data volume and is verified nightly on
/// field data, not on this synthetic distribution. Fast (&lt;1s) so it stays in the normal suite.
/// </summary>
public class MlEvalTests
{
    [Fact]
    public void Evaluate_HeldOutSplit_MeetsSyntheticFloor()
    {
        var train = SyntheticDataset.Generate(seed: 1001, n: 2400);
        var test = SyntheticDataset.Generate(seed: 2002, n: 1200); // disjoint held-out split

        var model = MlTrainer.Train(train, MlHyperparameters.Default);
        var result = Evaluator.Evaluate(model, test);

        Assert.True(result.Top1Accuracy >= 0.80,
            $"top-1 accuracy {result.Top1Accuracy:0.###} < 0.80 synthetic floor");
        Assert.True(result.MacroF1 >= 0.75,
            $"macro-F1 {result.MacroF1:0.###} < 0.75 synthetic floor");
    }
}
