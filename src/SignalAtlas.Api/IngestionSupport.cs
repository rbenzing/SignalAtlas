using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Default <see cref="ISdrHealthSink"/> (SPEC §5.5 NFR-R1): writes SDR connect/reconnect/failed
/// transitions to the app log so a USB drop→reconnect is observable in the field.
/// </summary>
public sealed class LoggingSdrHealthSink(ILogger<LoggingSdrHealthSink> logger) : ISdrHealthSink
{
    public void Report(SdrHealthEvent e) =>
        logger.LogInformation("SDR health {State} (attempt {Attempt}) at {Time:o}.", e.State, e.Attempt, e.Time);
}

/// <summary>
/// Shared holder for the ingestion backpressure drop count (SPEC §4.9 NFR-T1/C3). The hosted service
/// updates it from the bounded buffer; <c>/metrics</c> reads it as <c>drops_total</c>.
/// </summary>
public sealed class IngestionDropsMonitor
{
    private long _drops;

    /// <summary>Total blocks dropped under backpressure (never silent, never unbounded).</summary>
    public long Drops => Interlocked.Read(ref _drops);

    /// <summary>Record the current drop total from the bounded buffer (monotonic set).</summary>
    public void Set(long drops) => Interlocked.Exchange(ref _drops, drops);
}
