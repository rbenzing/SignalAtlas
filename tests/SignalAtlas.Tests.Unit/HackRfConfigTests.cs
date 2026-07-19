using Microsoft.Extensions.Configuration;
using SignalAtlas.Collector;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// Direct coverage for <see cref="CollectorServiceCollectionExtensions.ReadAndValidateConfig"/> — the
/// config-read/validate/fail-fast logic that fixes the "hardwired 915 MHz / gain 32, ignores config"
/// bug. Extracted to a pure, device-free internal method precisely so it is testable without a HackRF
/// or a registered <see cref="IHackRfDevice"/> (the public
/// <see cref="CollectorServiceCollectionExtensions.AddHackRfCollectorIfAvailable"/> never runs its
/// factory in a build with no hardware, since <see cref="SoapyHackRfDevice.IsAvailable"/> is false).
/// </summary>
public class HackRfConfigTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // The core bug this task fixes: a configured center frequency must override the 915 MHz default.
    [Fact]
    public void ConfiguredCenterFreq_OverridesDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:CenterFreqHz"] = "1090000000",
        });

        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(config);

        Assert.Equal(1_090_000_000L, cfg.CenterFreqHz);
    }

    [Fact]
    public void NullConfiguration_FallsBackToAllValidDefaults()
    {
        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(null);

        Assert.Equal(915_000_000L, cfg.CenterFreqHz);
        Assert.Equal(2_000_000, cfg.SampleRateHz);
        Assert.Equal(8192, cfg.SamplesPerBlock);
        Assert.Equal(RxGain.Default, cfg.Gain);
        Assert.False(cfg.BiasTee);
        Assert.True(HackRfLimits.IsValidBasebandBwHz(cfg.BasebandBwHz));
    }

    [Fact]
    public void AbsentKeys_FallBackToDefaults()
    {
        // A registered but empty configuration — every key is absent, not just IConfiguration being null.
        var config = BuildConfig([]);

        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(config);

        Assert.Equal(915_000_000L, cfg.CenterFreqHz);
        Assert.Equal(2_000_000, cfg.SampleRateHz);
        Assert.Equal(8192, cfg.SamplesPerBlock);
        Assert.Equal(RxGain.Default, cfg.Gain);
        Assert.False(cfg.BiasTee);
        Assert.True(HackRfLimits.IsValidBasebandBwHz(cfg.BasebandBwHz));
    }

    [Fact]
    public void OutOfRangeCenterFreq_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:CenterFreqHz"] = "7000000000", // above the 6 GHz ceiling
        });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CollectorServiceCollectionExtensions.ReadAndValidateConfig(config));
    }

    [Fact]
    public void OutOfRangeLnaDb_NotAMultipleOfStep_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:Gain:LnaDb"] = "20", // not a multiple of the 8 dB LNA step
        });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CollectorServiceCollectionExtensions.ReadAndValidateConfig(config));
    }

    [Fact]
    public void ValidCustomGain_IsReadIntoTuple()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:Gain:LnaDb"] = "24",
            ["Ingestion:Gain:VgaDb"] = "30",
            ["Ingestion:Gain:AmpEnable"] = "true",
        });

        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(config);

        Assert.Equal(new RxGain(AmpEnable: true, LnaDb: 24, VgaDb: 30), cfg.Gain);
    }

    // Minor fix verification: the baseband-BW default is derived from the sample rate ONLY when the
    // key is absent — an explicitly configured value must be used as-is, not silently overridden.
    [Fact]
    public void ConfiguredBasebandBw_IsUsedAsIs_NotOverriddenBySampleRateDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:SampleRateHz"] = "10000000",
            ["Ingestion:BasebandBwHz"] = "5000000",
        });

        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(config);

        Assert.Equal(5_000_000, cfg.BasebandBwHz);
    }

    [Fact]
    public void AbsentBasebandBw_DefaultsFromSampleRate()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:SampleRateHz"] = "10000000",
        });

        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(config);

        Assert.Equal(HackRfLimits.ClampBasebandBwHz(10_000_000), cfg.BasebandBwHz);
    }

    [Fact]
    public void BiasTee_ReadsConfiguredValue()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ingestion:BiasTee"] = "true",
        });

        var cfg = CollectorServiceCollectionExtensions.ReadAndValidateConfig(config);

        Assert.True(cfg.BiasTee);
    }
}
