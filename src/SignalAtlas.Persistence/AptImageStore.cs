using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Bounded thread-safe in-memory store of decoded APT satellite image bytes, keyed by device id
/// (SPEC §8.4 / NOAA APT Phase 1). Content is decoded PNG bytes only — NEVER written to disk or a
/// database (invariant-#3 carve-out: this is in-memory content, not the metadata-only persistence
/// path). Recency is tracked on <see cref="Put"/> only; <see cref="Get"/> does not refresh order
/// (kept simple for Phase 1, per the task brief).
/// </summary>
public sealed class AptImageStore : IAptImageStore
{
    /// <summary>Default max number of distinct device images retained before LRU eviction.</summary>
    public const int DefaultCapacity = 16;

    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly Dictionary<string, byte[]> _images = new();
    private readonly LinkedList<string> _recency = new(); // least-recently-put (front) → most-recent (back).
    private readonly object _sync = new();

    public AptImageStore(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Put(string deviceId, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(png);

        lock (_sync)
        {
            if (_nodes.TryGetValue(deviceId, out var existing))
            {
                _recency.Remove(existing);
                _recency.AddLast(existing);
            }
            else
            {
                var node = new LinkedListNode<string>(deviceId);
                _recency.AddLast(node);
                _nodes[deviceId] = node;
            }

            _images[deviceId] = png;

            while (_nodes.Count > _capacity)
            {
                var lru = _recency.First!;
                _recency.RemoveFirst();
                _nodes.Remove(lru.Value);
                _images.Remove(lru.Value);
            }
        }
    }

    public byte[]? Get(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        lock (_sync)
            return _images.TryGetValue(deviceId, out var png) ? png : null;
    }
}
