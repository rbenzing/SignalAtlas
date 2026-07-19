using SignalAtlas.Collector;

namespace SignalAtlas.Tests.Unit;

/// <summary>Table-driven valid/invalid cases for the HackRF One's RX ground truth (SPEC §4.1).</summary>
public class HackRfLimitsTests
{
    [Theory]
    [InlineData(1_000_000, true)]      // low edge
    [InlineData(6_000_000_000, true)]  // high edge
    [InlineData(915_000_000, true)]
    [InlineData(999_999, false)]       // just below low edge
    [InlineData(6_000_000_001, false)] // just above high edge
    [InlineData(0, false)]
    public void IsValidFrequencyHz_MatchesHackRfRange(long hz, bool expected) =>
        Assert.Equal(expected, HackRfLimits.IsValidFrequencyHz(hz));

    [Theory]
    [InlineData(2_000_000, true)]  // low edge
    [InlineData(20_000_000, true)] // high edge
    [InlineData(10_000_000, true)]
    [InlineData(1_999_999, false)]
    [InlineData(20_000_001, false)]
    public void IsValidSampleRateHz_MatchesHackRfRange(int hz, bool expected) =>
        Assert.Equal(expected, HackRfLimits.IsValidSampleRateHz(hz));

    [Theory]
    [InlineData(0, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(40, true)]  // high edge
    [InlineData(20, false)] // not a multiple of the 8 dB step
    [InlineData(-8, false)] // below range
    [InlineData(48, false)] // above range
    public void IsValidLnaDb_MatchesHackRfRange(int db, bool expected) =>
        Assert.Equal(expected, HackRfLimits.IsValidLnaDb(db));

    [Theory]
    [InlineData(0, true)]
    [InlineData(20, true)]
    [InlineData(62, true)]  // high edge
    [InlineData(21, false)] // not a multiple of the 2 dB step
    [InlineData(-2, false)] // below range
    [InlineData(64, false)] // above range
    public void IsValidVgaDb_MatchesHackRfRange(int db, bool expected) =>
        Assert.Equal(expected, HackRfLimits.IsValidVgaDb(db));

    [Theory]
    [InlineData(1_750_000, true)]  // low edge
    [InlineData(28_000_000, true)] // high edge
    [InlineData(2_000_000, true)]
    [InlineData(1_749_999, false)]
    [InlineData(28_000_001, false)]
    public void IsValidBasebandBwHz_MatchesHackRfRange(int hz, bool expected) =>
        Assert.Equal(expected, HackRfLimits.IsValidBasebandBwHz(hz));

    [Fact]
    public void IsValidGain_TrueOnlyWhenBothStagesValid()
    {
        Assert.True(HackRfLimits.IsValidGain(new RxGain(false, 16, 20)));
        Assert.False(HackRfLimits.IsValidGain(new RxGain(false, 20, 20))); // bad LNA step
        Assert.False(HackRfLimits.IsValidGain(new RxGain(false, 16, 21))); // bad VGA step
    }

    [Fact]
    public void ValidateFrequencyHz_ThrowsOnOutOfRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HackRfLimits.ValidateFrequencyHz(500_000));

    [Fact]
    public void ValidateFrequencyHz_DoesNotThrow_WhenInRange() =>
        HackRfLimits.ValidateFrequencyHz(915_000_000);

    [Fact]
    public void ValidateSampleRateHz_ThrowsOnOutOfRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HackRfLimits.ValidateSampleRateHz(1_000_000));

    [Fact]
    public void ValidateLnaDb_ThrowsOnBadStep() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HackRfLimits.ValidateLnaDb(20));

    [Fact]
    public void ValidateVgaDb_ThrowsOnBadStep() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HackRfLimits.ValidateVgaDb(21));

    [Fact]
    public void ValidateBasebandBwHz_ThrowsOnOutOfRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HackRfLimits.ValidateBasebandBwHz(1_000_000));

    [Fact]
    public void ValidateGain_ThrowsWhenEitherStageInvalid() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HackRfLimits.ValidateGain(new RxGain(false, 20, 20)));

    [Fact]
    public void ValidateGain_DoesNotThrow_WhenValid() =>
        HackRfLimits.ValidateGain(RxGain.Default);

    [Fact]
    public void ClampFrequencyHz_ClampsIntoRange()
    {
        Assert.Equal(HackRfLimits.MinFrequencyHz, HackRfLimits.ClampFrequencyHz(0));
        Assert.Equal(HackRfLimits.MaxFrequencyHz, HackRfLimits.ClampFrequencyHz(long.MaxValue));
        Assert.Equal(915_000_000, HackRfLimits.ClampFrequencyHz(915_000_000));
    }

    [Fact]
    public void ClampSampleRateHz_ClampsIntoRange()
    {
        Assert.Equal(HackRfLimits.MinSampleRateHz, HackRfLimits.ClampSampleRateHz(0));
        Assert.Equal(HackRfLimits.MaxSampleRateHz, HackRfLimits.ClampSampleRateHz(100_000_000));
    }

    [Fact]
    public void ClampBasebandBwHz_ClampsIntoRange()
    {
        Assert.Equal(HackRfLimits.MinBasebandBwHz, HackRfLimits.ClampBasebandBwHz(0));
        Assert.Equal(HackRfLimits.MaxBasebandBwHz, HackRfLimits.ClampBasebandBwHz(100_000_000));
        // A typical sample rate (2 MS/s) clamps into the valid baseband bandwidth range unchanged.
        Assert.Equal(2_000_000, HackRfLimits.ClampBasebandBwHz(2_000_000));
    }

    [Fact]
    public void ClampGain_RoundsDownToNearestValidStep_AndClampsRange()
    {
        var clamped = HackRfLimits.ClampGain(new RxGain(true, LnaDb: 22, VgaDb: 63));
        Assert.True(clamped.AmpEnable);
        Assert.Equal(16, clamped.LnaDb); // 22 rounds down to the 16 dB step
        Assert.Equal(62, clamped.VgaDb); // 63 clamps to the 62 dB max (already on-step)

        var negative = HackRfLimits.ClampGain(new RxGain(false, LnaDb: -5, VgaDb: -1));
        Assert.Equal(0, negative.LnaDb);
        Assert.Equal(0, negative.VgaDb);
    }
}
