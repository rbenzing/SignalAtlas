using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// RECEIVE-ONLY resilience wrapper over an <see cref="IHackRfDevice"/> (SPEC §5.5 NFR-R1). Behaves
/// like <see cref="HackRfSampleSource"/> but SURVIVES a mid-stream USB disconnect: when
/// <see cref="IHackRfDevice.ReadBlock"/> throws or <see cref="IHackRfDevice.OpenReceive"/> fails, it
/// reconnects with BOUNDED EXPONENTIAL BACKOFF, emitting <see cref="SdrHealthEvent"/>s via an
/// <see cref="ISdrHealthSink"/>. After the attempt budget is exhausted it STOPS GRACEFULLY (yields no
/// more blocks) instead of throwing — losing at most the in-flight block (NFR-R1).
///
/// Backoff schedule (documented, deterministic): delay(attempt) = min(BaseDelay * 2^(attempt-1), MaxDelay),
/// with <see cref="BaseDelay"/>=250 ms, <see cref="MaxDelay"/>=8 s, <see cref="MaxAttempts"/>=5 →
/// 250 ms, 500 ms, 1 s, 2 s, 4 s. The delay is applied through an injected <c>Action&lt;TimeSpan&gt;</c>
/// so tests substitute a no-op (never really sleep) and assert the exact schedule.
///
/// The transmit path is never touched — the receive-only invariant (SPEC §4.2 L1) is preserved.
/// </summary>
public sealed class ReconnectingHackRfSampleSource : ISampleSource
{
    /// <summary>Base backoff delay: the first reconnect attempt waits this long.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling on any single backoff delay (exponential growth is clamped here).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);

    /// <summary>Maximum reconnect attempts before the source stops gracefully.</summary>
    public const int MaxAttempts = 5;

    private readonly IHackRfDevice _device;
    private readonly long _centerFreqHz;
    private readonly int _sampleRateHz;
    private readonly RxGain _gain;
    private readonly int _basebandBwHz;
    private readonly bool _biasTee;
    private readonly int _samplesPerBlock;
    private readonly ISdrHealthSink _health;
    private readonly IClock _clock;
    private readonly Action<TimeSpan> _delay;

    public ReconnectingHackRfSampleSource(
        IHackRfDevice device,
        long centerFreqHz,
        int sampleRateHz,
        RxGain gain,
        int basebandBwHz,
        bool biasTee,
        int samplesPerBlock,
        ISdrHealthSink health,
        IClock clock,
        Action<TimeSpan>? delay = null)
    {
        if (samplesPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerBlock));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _centerFreqHz = centerFreqHz;
        _sampleRateHz = sampleRateHz;
        _gain = gain;
        _basebandBwHz = basebandBwHz;
        _biasTee = biasTee;
        _samplesPerBlock = samplesPerBlock;
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? Thread.Sleep; // real backoff in production; injected no-op in tests.
    }

    /// <summary>The documented backoff delay for a 1-based reconnect attempt.</summary>
    public static TimeSpan BackoffFor(int attempt)
    {
        if (attempt < 1) return TimeSpan.Zero;
        // 2^(attempt-1) grows quickly; clamp the exponent so the shift never overflows before the cap.
        double factor = Math.Pow(2, attempt - 1);
        double ms = Math.Min(BaseDelay.TotalMilliseconds * factor, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }

    public IEnumerable<IqBlock> Blocks()
    {
        if (!_device.IsAvailable)
            yield break; // Graceful fallback — no hardware, no blocks (SPEC §8.1).

        // Initial connect; a failure here is treated as a drop and goes straight to backoff reconnect.
        if (!TryOpen(attempt: 0) && !Reconnect())
            yield break;

        try
        {
            while (true)
            {
                var (ok, buffer) = NextBuffer();
                if (!ok || buffer.IsEmpty)
                    yield break; // permanent failure (budget exhausted) or clean end-of-stream.

                yield return Decode(buffer);
            }
        }
        finally
        {
            SafeClose();
        }
    }

    // Reads the next block, transparently reconnecting once on a mid-stream drop. Returns ok=false
    // only when the reconnect budget is exhausted (the caller then stops gracefully).
    private (bool ok, ReadOnlyMemory<byte> buffer) NextBuffer()
    {
        try
        {
            return (true, _device.ReadBlock());
        }
        catch
        {
            SafeClose();
            if (!Reconnect())
                return (false, ReadOnlyMemory<byte>.Empty);
            try
            {
                return (true, _device.ReadBlock());
            }
            catch
            {
                return (false, ReadOnlyMemory<byte>.Empty); // still failing right after reconnect — stop.
            }
        }
    }

    // Bounded exponential-backoff reconnect. Emits Reconnecting per attempt, Connected on success,
    // Failed once the budget is spent. Never throws.
    private bool Reconnect()
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _health.Report(new SdrHealthEvent(SdrHealthState.Reconnecting, attempt, _clock.UtcNow));
            _delay(BackoffFor(attempt));

            if (!_device.IsAvailable)
                continue; // hardware not back yet — wait out the next backoff.

            if (TryOpen(attempt))
                return true;
        }

        _health.Report(new SdrHealthEvent(SdrHealthState.Failed, MaxAttempts, _clock.UtcNow));
        return false;
    }

    // Opens the device for RECEIVE; reports Connected on success. Swallows open failures so the
    // caller can fall through to (or continue) the backoff loop.
    private bool TryOpen(int attempt)
    {
        try
        {
            _device.OpenReceive(_centerFreqHz, _sampleRateHz, _gain, _basebandBwHz, _biasTee);
            _health.Report(new SdrHealthEvent(SdrHealthState.Connected, attempt, _clock.UtcNow));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SafeClose()
    {
        try { _device.Close(); } catch { /* closing a dropped device may itself fault — ignore. */ }
    }

    // Same normalization convention as FileSampleSource / HackRfSampleSource (i/128f).
    private IqBlock Decode(ReadOnlyMemory<byte> buffer)
    {
        var span = buffer.Span;
        int sampleCount = span.Length / 2;
        var i = new float[sampleCount];
        var q = new float[sampleCount];
        for (int s = 0; s < sampleCount; s++)
        {
            int idx = s * 2;
            i[s] = (sbyte)span[idx] / 128f;
            q[s] = (sbyte)span[idx + 1] / 128f;
        }
        return new IqBlock(_centerFreqHz, _sampleRateHz, i, q,
            new ReceiverConfig(_gain.AmpEnable, _gain.LnaDb, _gain.VgaDb, _basebandBwHz, _biasTee));
    }
}
