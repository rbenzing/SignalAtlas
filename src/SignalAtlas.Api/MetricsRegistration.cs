using Prometheus;
using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Prometheus metrics wiring (SPEC §5.5 NFR-R3) using <c>prometheus-net.AspNetCore</c>. A per-app
/// <see cref="CollectorRegistry"/> (never the global default) is used so repeated
/// WebApplicationFactory hosts in one test process don't share/leak collectors. Exposes a request
/// counter plus signal/device/alert gauges (read from the repos on scrape) at an ungated
/// <c>/metrics</c> endpoint.
/// </summary>
public static class MetricsRegistration
{
    public static CollectorRegistry AddSignalAtlasMetrics(this IServiceCollection services)
    {
        var registry = Metrics.NewCustomRegistry();
        services.AddSingleton(registry);
        return registry;
    }

    public static void UseSignalAtlasMetrics(this WebApplication app, CollectorRegistry registry)
    {
        var metrics = Metrics.WithCustomRegistry(registry);

        var requests = metrics.CreateCounter(
            "signalatlas_http_requests_total",
            "Total HTTP requests handled (SPEC NFR-R3).",
            new CounterConfiguration { LabelNames = ["method"] });

        var signals = metrics.CreateGauge("signalatlas_signals_total", "Signals currently stored (SPEC NFR-R3).");
        var devices = metrics.CreateGauge("signalatlas_devices_total", "Devices determined (SPEC NFR-R3).");
        var alerts = metrics.CreateGauge("signalatlas_alerts_total", "Alerts raised (SPEC NFR-R3).");
        // Backpressure drops (SPEC §4.9 NFR-T1/C3): bounded-queue drop-oldest count — never silent.
        var drops = metrics.CreateGauge("signalatlas_ingestion_drops_total", "IQ blocks dropped under backpressure (SPEC NFR-T1/C3).");

        registry.AddBeforeCollectCallback(() =>
        {
            // Metrics must never break the app: a scrape failure is swallowed, not propagated.
            try
            {
                using var scope = app.Services.CreateScope();
                var sp = scope.ServiceProvider;
                signals.Set(sp.GetService<ISignalRepository>()?.GetSignals(int.MaxValue).Count ?? 0);
                devices.Set(sp.GetService<IDeviceRepository>()?.GetDevices(int.MaxValue).Count ?? 0);
                alerts.Set(sp.GetService<IAlertRepository>()?.GetAlerts(int.MaxValue).Count ?? 0);
                drops.Set(sp.GetService<IngestionDropsMonitor>()?.Drops ?? 0);
            }
            catch
            {
                // best-effort gauges
            }
        });

        app.Use(async (ctx, next) =>
        {
            requests.WithLabels(ctx.Request.Method).Inc();
            await next(ctx);
        });

        // Ungated: mapped at the app root, outside the /api/v1 authorization group (SPEC §5.5).
        app.MapMetrics("/metrics", registry);
    }
}
