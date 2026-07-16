using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M9 — synthetic labeled dataset (SPEC §12.2 source 1). The generator MUST be deterministic
/// for a fixed seed (P5) so training, goldens and eval stay stable.
/// </summary>
public class MlDatasetTests
{
    [Fact]
    public void Generate_SameSeed_ProducesIdenticalDataset()
    {
        var a = SyntheticDataset.Generate(seed: 42, n: 500);
        var b = SyntheticDataset.Generate(seed: 42, n: 500);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Label, b[i].Label);
            Assert.Equal(a[i].Features, b[i].Features); // record value equality
        }
    }

    [Fact]
    public void Generate_CoversEveryProtocolLabel()
    {
        var ds = SyntheticDataset.Generate(seed: 7, n: 1400);
        var labels = ds.Select(s => s.Label).Distinct().ToHashSet();
        foreach (var p in SyntheticDataset.Protocols)
            Assert.Contains(p, labels);
    }

    [Fact]
    public void Generate_DifferentSeed_ProducesDifferentData()
    {
        var a = SyntheticDataset.Generate(seed: 1, n: 200);
        var b = SyntheticDataset.Generate(seed: 2, n: 200);
        // Not identical across all rows — sanity that the seed actually drives the stream.
        Assert.False(a.Select(s => s.Features).SequenceEqual(b.Select(s => s.Features)));
    }
}
