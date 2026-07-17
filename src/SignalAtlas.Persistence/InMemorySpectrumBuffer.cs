using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first bounded ring of recent PSD frames feeding the Spectrum waterfall (SPEC §8.2,
/// NFR-C3 bounded-buffer discipline). Thread-safe pushes from the live pipeline; oldest frames are
/// evicted once <see cref="_capacity"/> is reached so the buffer never grows unboundedly.
/// </summary>
public sealed class InMemorySpectrumBuffer : ISpectrumBuffer, IDemoSeedStore
{
    /// <summary>Default retained-frame count — enough for a scrolling waterfall (SPEC §8.2).</summary>
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly LinkedList<SpectrumFrame> _frames = new(); // oldest → newest.
    private readonly object _sync = new();

    public InMemorySpectrumBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Push(SpectrumFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        lock (_sync)
        {
            _frames.AddLast(f);
            while (_frames.Count > _capacity)
                _frames.RemoveFirst();
        }
    }

    /// <summary>Drop the seeded demo frames so a connected device's waterfall shows live frames only.</summary>
    public void ClearDemoSeed()
    {
        lock (_sync)
            _frames.Clear();
    }

    /// <summary>Most-recent <paramref name="n"/> frames, newest first (clamped to what is held).</summary>
    public IReadOnlyList<SpectrumFrame> Recent(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        lock (_sync)
        {
            var result = new List<SpectrumFrame>(Math.Min(n, _frames.Count));
            for (var node = _frames.Last; node is not null && result.Count < n; node = node.Previous)
                result.Add(node.Value);
            return result;
        }
    }
}
