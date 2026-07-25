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

    /// <summary>A single device by its deterministic id, or <c>null</c> (#8 — avoids scanning the whole
    /// devices table for one lookup, as <c>GET /devices/{id}</c> otherwise would). Additive: the default
    /// derives from <see cref="GetDevices"/>; the EF store overrides it with a keyed <c>Find</c> and the
    /// in-memory store with a locked lookup.</summary>
    Device? Get(string id) => GetDevices(int.MaxValue).FirstOrDefault(d => d.Id == id);
}
