using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// <see cref="DeterministicRng"/> feeds <c>SyntheticDataset</c>/<c>MlTrainer</c> (P5, deterministic
/// core) — the numeric sequence it produces for VALID ranges must never change, since existing ML
/// tests assert on the resulting trained model. These tests pin the empty/inverted-range guards
/// (#18) and confirm the normal-range draw sequence is untouched by them.
/// </summary>
public class DeterministicRngTests
{
    [Fact]
    public void NextInt_EmptyRange_ReturnsMinWithoutThrowing()
    {
        var rng = new DeterministicRng(42);
        var result = rng.NextInt(5, 5);
        Assert.Equal(5, result);
    }

    [Fact]
    public void NextInt_EmptyRange_DoesNotAdvanceStream()
    {
        // A draw from the empty-range branch must consume nothing: the next real draw from a rng
        // that took the empty-range branch must equal the very first draw from a fresh rng with the
        // same seed.
        var withEmptyDraw = new DeterministicRng(42);
        withEmptyDraw.NextInt(5, 5); // should be a no-op on the stream
        var afterEmpty = withEmptyDraw.NextInt(0, 10);

        var fresh = new DeterministicRng(42);
        var freshFirst = fresh.NextInt(0, 10);

        Assert.Equal(freshFirst, afterEmpty);
    }

    [Fact]
    public void NextInt_InvertedRange_Throws()
    {
        var rng = new DeterministicRng(42);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(3, 1));
    }

    [Fact]
    public void NextInt_NormalRange_SequenceUnchanged()
    {
        // Pinned value for seed 42, NextInt(0,10) — captured from the unmodified NextUInt64()/modulo
        // code path BEFORE the empty/inverted-range guards were added, to prove the fix does not
        // shift the generated sequence for valid ranges (SyntheticDataset/MlTrainer determinism, P5).
        var rng = new DeterministicRng(42);
        var result = rng.NextInt(0, 10);
        Assert.Equal(3, result);
    }
}
