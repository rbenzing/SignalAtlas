using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>List-backed fake repos + spectrum buffer for the deterministic analyst tests (no DB, no LLM).</summary>
internal sealed class FakeSignalRepository(IEnumerable<Signal> signals) : ISignalRepository
{
    private readonly List<Signal> _signals = signals.ToList();
    public IReadOnlyList<Signal> GetSignals(int limit = 100) => _signals.Take(limit).ToList();
}

internal sealed class FakeDeviceRepository(IEnumerable<Device> devices) : IDeviceRepository
{
    private readonly List<Device> _devices = devices.ToList();
    public IReadOnlyList<Device> GetDevices(int limit = 100) => _devices.Take(limit).ToList();
}

internal sealed class FakeEmitterRepository(IEnumerable<Emitter> emitters) : IEmitterRepository
{
    private readonly List<Emitter> _emitters = emitters.ToList();
    public IReadOnlyList<Emitter> All() => _emitters;
    public void Upsert(Emitter e) => _emitters.Add(e);
}

internal sealed class FakeAlertRepository(IEnumerable<Alert> alerts) : IAlertRepository
{
    private readonly List<Alert> _alerts = alerts.ToList();
    public IReadOnlyList<Alert> GetAlerts(int limit = 100) => _alerts.Take(limit).ToList();
}

internal sealed class FakeSpectrumBuffer(IEnumerable<SpectrumFrame> frames) : ISpectrumBuffer
{
    private readonly List<SpectrumFrame> _frames = frames.ToList();
    public void Push(SpectrumFrame f) => _frames.Insert(0, f);
    public IReadOnlyList<SpectrumFrame> Recent(int n) => _frames.Take(n).ToList();
}

/// <summary>Convenience builders for analyst test records.</summary>
internal static class AnalystData
{
    private static readonly IReadOnlyList<EvidenceItem> Ev = [new EvidenceItem("f", "v", 1.0)];

    public static Signal Signal(long id, string protocol, DateTimeOffset time, long freqHz = 915_000_000)
        => new(id, time, id, null, null, protocol, 0.9, "rules", Ev, freqHz, 125_000, 100,
            new Dictionary<string, double>());

    public static Emitter Emitter(string id, string protocol, long freqHz,
        double? lat = null, double? lon = null)
        => new(id, null, protocol, freqHz, 1000, lat, lon, lat is null ? null : 100.0, 1, 0.9,
            new Dictionary<string, string>(), Ev);

    public static Device Device(string id, string protocol)
        => new(id, "type", null, new Dictionary<string, string>(), null, protocol, 0.9, Ev);

    public static Alert Alert(string kind, DateTimeOffset time)
        => new(Guid.NewGuid(), time, "emitter-x", null, kind, "info", "summary", Ev);

    public static SpectrumFrame Frame(DateTimeOffset time, double[] bins, long centerHz = 2_437_000_000)
        => new(time, centerHz, 20_000_000, bins);
}
