using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Domain;

// Exposes the internal, device-free ReadAndValidateConfig(...) to the unit test project so the
// config-read/validate/fail-fast logic (previously trapped, untested, inside the ISampleSource
// factory closure) is directly unit-testable without a HackRF or a registered IHackRfDevice.
[assembly: InternalsVisibleTo("SignalAtlas.Tests.Unit")]

namespace SignalAtlas.Collector;

/// <summary>
/// Docker/hardware-OPTIONAL collector wiring (SPEC §4.1, §8.1). Registers the real HackRF
/// <see cref="ISampleSource"/> ONLY when a HackRF is physically connected over USB and the
/// SoapySDR bindings load (<see cref="IHackRfDevice.IsAvailable"/>). Otherwise it registers
/// nothing, leaving the caller free to fall back to the file/synthetic source — the app runs
/// fully offline with zero hardware.
/// </summary>
public static class CollectorServiceCollectionExtensions
{
    // Sensible field defaults for the survey rig (SPEC §4.1/§4.4); tune per deployment. Used when
    // IConfiguration is unregistered (offline/standalone) or a key is simply absent.
    private const long DefaultCenterFreqHz = 915_000_000;
    private const int DefaultSampleRateHz = 2_000_000;
    private const int DefaultSamplesPerBlock = 8192;
    private static readonly RxGain DefaultGain = RxGain.Default;
    private const bool DefaultBiasTee = false;

    /// <summary>
    /// Registers <see cref="HackRfSampleSource"/> as <see cref="ISampleSource"/> iff a HackRF is
    /// available; a no-op otherwise. Returns <c>true</c> when the hardware source was registered.
    /// Reads operator-configured center frequency / sample rate / gain / baseband filter bandwidth /
    /// bias-tee from <c>Ingestion:*</c> config keys inside the factory (resolved from the DI
    /// container at first use), falling back to the field defaults above when a key is absent or no
    /// <see cref="IConfiguration"/> is registered. Each configured value is validated against
    /// <see cref="HackRfLimits"/> and FAILS FAST (throws) on an out-of-range misconfiguration rather
    /// than silently clamping it into a wrong band — an operator typo must not go unnoticed.
    /// </summary>
    public static bool AddHackRfCollectorIfAvailable(this IServiceCollection services)
    {
        var device = new SoapyHackRfDevice();
        if (!device.IsAvailable)
            return false; // No hardware — caller falls back to file/synthetic source.

        services.AddSingleton<IHackRfDevice>(device);
        // Resilience wrapper (SPEC §5.5 NFR-R1): survives a mid-stream USB disconnect via bounded
        // exponential-backoff reconnect, emitting health events. Falls back to a no-op sink / system
        // clock if the host registered neither (offline/standalone).
        services.AddSingleton<ISampleSource>(sp =>
        {
            var cfg = ReadAndValidateConfig(sp.GetService<IConfiguration>());

            return new ReconnectingHackRfSampleSource(
                device, cfg.CenterFreqHz, cfg.SampleRateHz, cfg.Gain, cfg.BasebandBwHz, cfg.BiasTee,
                cfg.SamplesPerBlock,
                health: sp.GetService<ISdrHealthSink>() ?? NullSdrHealthSink.Instance,
                clock: sp.GetService<IClock>() ?? SystemUtcClock.Instance);
        });
        return true;
    }

    /// <summary>
    /// Reads operator-configured center frequency / sample rate / gain / baseband filter bandwidth /
    /// bias-tee from <c>Ingestion:*</c> config keys, falling back to the field defaults when a key is
    /// absent or <paramref name="config"/> is <c>null</c> (no <see cref="IConfiguration"/> registered —
    /// offline/standalone). Depends ONLY on <paramref name="config"/> — no device, no DI container — so
    /// it is directly unit-testable without a HackRF or a registered <see cref="IHackRfDevice"/>.
    /// Each value is validated against <see cref="HackRfLimits"/> and FAILS FAST (throws
    /// <see cref="ArgumentOutOfRangeException"/>) on an out-of-range misconfiguration rather than
    /// silently clamping it into a wrong band — an operator typo must not go unnoticed.
    /// </summary>
    internal static (
        long CenterFreqHz, int SampleRateHz, int SamplesPerBlock, RxGain Gain, int BasebandBwHz,
        bool BiasTee) ReadAndValidateConfig(IConfiguration? config)
    {
        long centerFreqHz = ReadLong(config, "Ingestion:CenterFreqHz", DefaultCenterFreqHz);
        int sampleRateHz = ReadInt(config, "Ingestion:SampleRateHz", DefaultSampleRateHz);
        int samplesPerBlock = ReadInt(config, "Ingestion:SamplesPerBlock", DefaultSamplesPerBlock);
        bool ampEnable = ReadBool(config, "Ingestion:Gain:AmpEnable", DefaultGain.AmpEnable);
        int lnaDb = ReadInt(config, "Ingestion:Gain:LnaDb", DefaultGain.LnaDb);
        int vgaDb = ReadInt(config, "Ingestion:Gain:VgaDb", DefaultGain.VgaDb);

        // Only fall back to the sample-rate-derived default when the key is genuinely absent — never
        // compute the clamp fallback (or, more importantly, treat it as "configured") when the
        // operator supplied an explicit value.
        int? configuredBasebandBwHz = config?.GetValue<int?>("Ingestion:BasebandBwHz");
        int basebandBwHz = configuredBasebandBwHz ?? HackRfLimits.ClampBasebandBwHz(sampleRateHz);

        bool biasTee = ReadBool(config, "Ingestion:BiasTee", DefaultBiasTee);

        var gain = new RxGain(ampEnable, lnaDb, vgaDb);

        // Fail fast on a bad configured value — never silently clamp a misconfiguration.
        HackRfLimits.ValidateFrequencyHz(centerFreqHz);
        HackRfLimits.ValidateSampleRateHz(sampleRateHz);
        HackRfLimits.ValidateGain(gain);
        HackRfLimits.ValidateBasebandBwHz(basebandBwHz);

        return (centerFreqHz, sampleRateHz, samplesPerBlock, gain, basebandBwHz, biasTee);
    }

    private static long ReadLong(IConfiguration? config, string key, long fallback) =>
        config is null ? fallback : config.GetValue<long?>(key) ?? fallback;

    private static int ReadInt(IConfiguration? config, string key, int fallback) =>
        config is null ? fallback : config.GetValue<int?>(key) ?? fallback;

    private static bool ReadBool(IConfiguration? config, string key, bool fallback) =>
        config is null ? fallback : config.GetValue<bool?>(key) ?? fallback;

    private sealed class NullSdrHealthSink : ISdrHealthSink
    {
        public static readonly NullSdrHealthSink Instance = new();
        public void Report(SdrHealthEvent e) { }
    }

    private sealed class SystemUtcClock : IClock
    {
        public static readonly SystemUtcClock Instance = new();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeSource Source => TimeSource.Host;
    }
}
