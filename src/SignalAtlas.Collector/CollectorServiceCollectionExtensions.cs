using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Domain;

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
    // Sensible field defaults for the survey rig (SPEC §4.1/§4.4); tune per deployment.
    private const long DefaultCenterFreqHz = 915_000_000;
    private const int DefaultSampleRateHz = 2_000_000;
    private const double DefaultGainDb = 32.0;
    private const int DefaultSamplesPerBlock = 8192;

    /// <summary>
    /// Registers <see cref="HackRfSampleSource"/> as <see cref="ISampleSource"/> iff a HackRF is
    /// available; a no-op otherwise. Returns <c>true</c> when the hardware source was registered.
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
        services.AddSingleton<ISampleSource>(sp => new ReconnectingHackRfSampleSource(
            device, DefaultCenterFreqHz, DefaultSampleRateHz, DefaultGainDb, DefaultSamplesPerBlock,
            health: sp.GetService<ISdrHealthSink>() ?? NullSdrHealthSink.Instance,
            clock: sp.GetService<IClock>() ?? SystemUtcClock.Instance));
        return true;
    }

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
