namespace SignalAtlas.Domain;

/// <summary>Read + write access to determined devices (SPEC §9.2 GET /devices; §8.4 write).</summary>
public interface IDeviceRepository
{
    IReadOnlyList<Device> GetDevices(int limit = 100);

    /// <summary>Idempotent insert-or-update keyed on the deterministic <see cref="Device.Id"/>
    /// (SPEC §8.4): the same aircraft re-seen across blocks converges instead of duplicating.</summary>
    void Upsert(Device device);

    /// <summary>Total device count (#8 — avoids a full-table read just to count rows). Additive: the
    /// default derives from <see cref="GetDevices"/> so existing implementers keep compiling; the real
    /// EF/in-memory stores override it with an efficient, provider/lock-appropriate count.</summary>
    int Count() => GetDevices(int.MaxValue).Count;
}
