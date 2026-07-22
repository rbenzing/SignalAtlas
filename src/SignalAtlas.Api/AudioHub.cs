using System.Threading.Channels;
using SignalAtlas.Domain;
using SignalAtlas.Processing;

namespace SignalAtlas.Api;

/// <summary>
/// Thread-safe SINGLETON fan-out for the RF Audio Player (design §2/§3). Holds the active
/// <see cref="AudioMode"/>, an enabled flag, and the set of connected `/audio` subscribers.
/// <see cref="Accept"/> is called once per IQ block by every live <see cref="SignalAtlas.Pipeline.IngestionPipeline"/>
/// run; it is a no-op (zero cost) whenever disabled or nobody is listening, else it demodulates the
/// block with ONE shared <see cref="AudioDemodulator"/> for the active mode and fans the resulting
/// PCM16LE bytes out to every subscriber's bounded, drop-oldest queue (counted, never a silent
/// unbounded grow -- mirrors <see cref="SignalAtlas.Pipeline.BoundedBlockBuffer"/>'s pattern).
///
/// Deterministic-core note: this hub is API-layer infrastructure (like <see cref="SignalRLiveNotifier"/>)
/// -- it legitimately uses a lock and Channels; the DSP inside <see cref="AudioDemodulator"/> itself
/// stays pure/deterministic (P5). Audio is never persisted (transient stream only).
/// </summary>
public sealed class AudioHub : IAudioSink
{
    private const int SubscriberQueueCapacity = 64;

    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = new();
    private AudioMode _mode = AudioMode.Wbfm;
    private bool _enabled;
    private IAudioDemodulator? _demod;

    /// <summary>One connected `/audio` client: a bounded, drop-oldest queue of PCM16LE chunks.</summary>
    public sealed class Subscriber
    {
        private readonly Channel<byte[]> _channel;
        private long _drops;

        internal Subscriber()
        {
            _channel = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(SubscriberQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                itemDropped: _ => Interlocked.Increment(ref _drops));
        }

        /// <summary>Count of PCM chunks dropped due to saturation (drop-oldest policy).</summary>
        public long Drops => Interlocked.Read(ref _drops);

        internal bool TryWrite(byte[] pcm) => _channel.Writer.TryWrite(pcm);

        /// <summary>Await the next PCM16LE chunk; throws <see cref="ChannelClosedException"/> once
        /// the subscriber has been unregistered and its queue drained.</summary>
        public ValueTask<byte[]> ReadAsync(CancellationToken ct = default) => _channel.Reader.ReadAsync(ct);

        internal void Complete() => _channel.Writer.TryComplete();
    }

    /// <summary>Sets the active demod mode + enabled flag from the client's JSON config frame. A
    /// mode change rebuilds the shared demodulator -- its carried DSP state (discriminator phase,
    /// de-emphasis/DC-block state, resampler tail) belongs to the OLD mode and must never leak into
    /// the new one.</summary>
    public void Configure(AudioMode mode, bool enabled)
    {
        lock (_gate)
        {
            if (_demod is null || _mode != mode)
            {
                _mode = mode;
                _demod = null; // Rebuilt lazily on the next Accept that actually needs it.
            }
            _enabled = enabled;
        }
    }

    /// <summary>Registers a new subscriber (a connecting `/audio` WebSocket). Caller must
    /// <see cref="Unregister"/> it when the connection closes.</summary>
    public Subscriber Register()
    {
        var sub = new Subscriber();
        lock (_gate) { _subscribers.Add(sub); }
        return sub;
    }

    /// <summary>Removes a subscriber and completes its queue (unblocks a pending <see cref="Subscriber.ReadAsync"/>).</summary>
    public void Unregister(Subscriber sub)
    {
        lock (_gate) { _subscribers.Remove(sub); }
        sub.Complete();
    }

    /// <inheritdoc/>
    public void Accept(IqBlock block)
    {
        lock (_gate)
        {
            // Zero-cost no-op: nobody listening, or listening explicitly disabled.
            if (!_enabled || _subscribers.Count == 0) return;

            _demod ??= new AudioDemodulator(_mode);
            byte[] pcm = _demod.Demodulate(block);
            if (pcm.Length == 0) return;

            foreach (var sub in _subscribers)
                sub.TryWrite(pcm);
        }
    }
}
