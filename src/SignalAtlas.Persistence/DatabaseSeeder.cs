using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Seeds the demo rows (SPEC §4.3 offline-first parity) into a fresh database: one LoRa signal,
/// one ADS-B device (via the real <see cref="IDeviceResolver"/>), and the new_emitter/new_device
/// alerts (via the real <see cref="IAnomalyEngine"/>). Reuses the in-memory repos as the single
/// source of the seed rows so the DB path and the offline path serve identical demo data.
/// Idempotent: seeds only when the tables are empty.
/// </summary>
public static class DatabaseSeeder
{
    public static void SeedIfEmpty(SignalAtlasDbContext db, IDeviceResolver resolver, IAnomalyEngine engine)
    {
        var seeded = false;

        if (!db.Signals.Any())
        {
            db.Signals.AddRange(new InMemorySignalRepository().GetSignals(int.MaxValue));
            seeded = true;
        }

        if (!db.Devices.Any())
        {
            db.Devices.AddRange(new InMemoryDeviceRepository(resolver).GetDevices(int.MaxValue));
            seeded = true;
        }

        if (!db.Alerts.Any())
        {
            db.Alerts.AddRange(new InMemoryAlertRepository(engine).GetAlerts(int.MaxValue));
            seeded = true;
        }

        if (seeded)
            db.SaveChanges();
    }
}
