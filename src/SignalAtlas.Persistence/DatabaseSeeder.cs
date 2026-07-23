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
    /// <summary>
    /// Seeds the demo rows into an empty DB. Only reached when the caller (<c>DatabaseInitializer</c>)
    /// has already gated on <c>SeedDemoData</c> config, but <paramref name="seedDemo"/> also defaults
    /// false here so a direct/test call is empty unless explicitly opted in.
    /// </summary>
    public static void SeedIfEmpty(SignalAtlasDbContext db, IDeviceResolver resolver, IAnomalyEngine engine, bool seedDemo = false)
    {
        if (!seedDemo) return;

        var seeded = false;

        if (!db.Signals.Any())
        {
            db.Signals.AddRange(new InMemorySignalRepository(seedDemo: true).GetSignals(int.MaxValue));
            seeded = true;
        }

        if (!db.Devices.Any())
        {
            db.Devices.AddRange(new InMemoryDeviceRepository(resolver, seedDemo: true).GetDevices(int.MaxValue));
            seeded = true;
        }

        if (!db.Alerts.Any())
        {
            db.Alerts.AddRange(new InMemoryAlertRepository(engine, seedDemo: true).GetAlerts(int.MaxValue));
            seeded = true;
        }

        if (seeded)
            db.SaveChanges();
    }
}
