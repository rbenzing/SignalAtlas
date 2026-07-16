using SignalAtlas.Domain;
using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M9 — drift monitor (SPEC §8.9). Fires when the recent feature stream shifts beyond the
/// documented z-score threshold vs. the training baseline; stays quiet for in-distribution data.
/// </summary>
public class MlDriftTests
{
    private static readonly MlModel Model =
        MlTrainer.Train(SyntheticDataset.Generate(seed: 313, n: 1400), MlHyperparameters.Default);

    [Fact]
    public void IsDrifting_InDistributionStream_DoesNotFire()
    {
        // A fresh draw from the SAME generator is in-distribution.
        var recent = SyntheticDataset.Generate(seed: 777, n: 400).Select(s => s.Features).ToList();
        var monitor = new DriftMonitor(Model);
        Assert.False(monitor.IsDrifting(recent));
    }

    [Fact]
    public void IsDrifting_ShiftedStream_Fires()
    {
        // Everything jammed into an unused band with wildly off bandwidth/SNR → strong drift.
        var shifted = Enumerable.Range(0, 400)
            .Select(_ => new FeatureVector(
                CenterFreqHz: 5_800_000_000, Bandwidth3dBHz: 60_000_000, Bandwidth20dBHz: 80_000_000,
                PeakPowerDbfs: 0, SnrDb: 60, DurationMs: 5000, DutyCycle: 1.0, ModulationHint: null))
            .ToList<FeatureVector>();

        var monitor = new DriftMonitor(Model);
        Assert.True(monitor.IsDrifting(shifted));
    }
}
