namespace SignalAtlas.Domain;

/// <summary>Read access to determined devices (SPEC §9.2 GET /devices).</summary>
public interface IDeviceRepository
{
    IReadOnlyList<Device> GetDevices(int limit = 100);
}
