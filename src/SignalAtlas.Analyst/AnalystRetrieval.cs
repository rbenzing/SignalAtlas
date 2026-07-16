using System.Globalization;
using SignalAtlas.Domain;

namespace SignalAtlas.Analyst;

/// <summary>
/// The shared deterministic retrieval/tool + citation layer (SPEC §8.12). Queries the repos per
/// query type + slots, renders a grounded, template answer, and emits a citation for EVERY record
/// used — reusing <see cref="EvidenceItem"/> as {feature = record-type, value = record-id, weight}.
/// Empty result → "None found." + ZERO citations, never fabricated content (P6). No LLM here.
/// </summary>
public sealed class AnalystRetrieval(
    ISignalRepository signals,
    IDeviceRepository devices,
    IEmitterRepository emitters,
    IAlertRepository alerts,
    ISpectrumBuffer spectrum,
    IClock clock) : IAnalystRetrieval
{
    private const string NoneFound = "None found.";
    private const double OccupancyMarginDb = 6.0;

    private readonly ISignalRepository _signals = signals;
    private readonly IDeviceRepository _devices = devices;
    private readonly IEmitterRepository _emitters = emitters;
    private readonly IAlertRepository _alerts = alerts;
    private readonly ISpectrumBuffer _spectrum = spectrum;
    private readonly IClock _clock = clock;

    public AnalystRetrievalResult Retrieve(AnalystIntent intent) => intent.QueryType switch
    {
        AnalystQueryType.WhatChanged => WhatChanged(intent),
        AnalystQueryType.NearLocation => NearLocation(intent),
        AnalystQueryType.UnknownInBand => UnknownInBand(intent),
        AnalystQueryType.CountByProtocol => CountByProtocol(intent),
        AnalystQueryType.ListByProtocol => ListByProtocol(intent),
        AnalystQueryType.Occupancy => Occupancy(),
        _ => Empty(),
    };

    private static AnalystRetrievalResult Empty() => new(NoneFound, [], 0);

    private static EvidenceItem Cite(string recordType, string id) => new(recordType, id, 1.0);

    private AnalystRetrievalResult WhatChanged(AnalystIntent intent)
    {
        var window = intent.Window ?? TimeSpan.FromHours(24);
        var cutoff = _clock.UtcNow - window;

        var recentAlerts = _alerts.GetAlerts(int.MaxValue).Where(a => a.Time >= cutoff).ToList();
        var recentSignals = _signals.GetSignals(int.MaxValue).Where(s => s.Time >= cutoff).ToList();

        if (recentAlerts.Count == 0 && recentSignals.Count == 0)
            return Empty();

        var citations = new List<EvidenceItem>();
        citations.AddRange(recentAlerts.Select(a => Cite("alert", a.Id.ToString())));
        citations.AddRange(recentSignals.Select(s => Cite("signal", s.Id.ToString(CultureInfo.InvariantCulture))));

        var kinds = recentAlerts.Select(a => a.Kind).Distinct();
        var protos = recentSignals.Select(s => s.Protocol).Distinct();
        var answer =
            $"In the last {Describe(window)}: {recentAlerts.Count} alert(s) [{string.Join(", ", kinds)}], "
            + $"{recentSignals.Count} signal(s) [{string.Join(", ", protos)}].";

        return new AnalystRetrievalResult(answer, citations, citations.Count);
    }

    private AnalystRetrievalResult NearLocation(AnalystIntent intent)
    {
        if (intent.Latitude is null || intent.Longitude is null)
            return new AnalystRetrievalResult(
                "No location was recognized in the query. Provide coordinates as 'lat,lon'.", [], 0);

        double lat = intent.Latitude.Value, lon = intent.Longitude.Value;
        double radius = intent.RadiusMeters ?? 1000.0;

        var matches = _emitters.All()
            .Where(e => e.EstLatitude is not null && e.EstLongitude is not null
                && GeoMath.HaversineMeters(lat, lon, e.EstLatitude.Value, e.EstLongitude.Value) <= radius)
            .ToList();

        if (matches.Count == 0) return Empty();

        var citations = matches.Select(e => Cite("emitter", e.Id)).ToList();
        var answer =
            $"{matches.Count} emitter(s) within {radius:0} m of ({lat},{lon}): "
            + string.Join(", ", matches.Select(e => $"{e.Id} ({e.Protocol})")) + ".";
        return new AnalystRetrievalResult(answer, citations, citations.Count);
    }

    private AnalystRetrievalResult UnknownInBand(AnalystIntent intent)
    {
        bool InBand(long hz) => intent.BandLowHz is null || intent.BandHighHz is null
            || (hz >= intent.BandLowHz && hz <= intent.BandHighHz);

        var unkEmitters = _emitters.All()
            .Where(e => IsUnknown(e.Protocol) && InBand(e.FreqCenterHz)).ToList();
        var unkSignals = _signals.GetSignals(int.MaxValue)
            .Where(s => IsUnknown(s.Protocol) && InBand(s.CenterFreqHz)).ToList();

        if (unkEmitters.Count == 0 && unkSignals.Count == 0) return Empty();

        var citations = new List<EvidenceItem>();
        citations.AddRange(unkEmitters.Select(e => Cite("emitter", e.Id)));
        citations.AddRange(unkSignals.Select(s => Cite("signal", s.Id.ToString(CultureInfo.InvariantCulture))));

        var band = intent.BandLowHz is null ? "any band"
            : $"{intent.BandLowHz.Value / 1_000_000.0:0.###}-{intent.BandHighHz!.Value / 1_000_000.0:0.###} MHz";
        var answer = $"{unkEmitters.Count} unknown emitter(s) and {unkSignals.Count} unknown signal(s) in {band}.";
        return new AnalystRetrievalResult(answer, citations, citations.Count);
    }

    private AnalystRetrievalResult CountByProtocol(AnalystIntent intent)
    {
        var all = _emitters.All();

        if (intent.Protocol is not null)
        {
            var matches = all.Where(e => ProtoEq(e.Protocol, intent.Protocol)).ToList();
            if (matches.Count == 0) return Empty();
            var cites = matches.Select(e => Cite("emitter", e.Id)).ToList();
            return new AnalystRetrievalResult(
                $"{matches.Count} {intent.Protocol} emitter(s).", cites, cites.Count);
        }

        if (all.Count == 0) return Empty();

        var groups = all.GroupBy(e => e.Protocol)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}");
        var citations = all.Select(e => Cite("emitter", e.Id)).ToList();
        return new AnalystRetrievalResult(
            $"Emitter counts by protocol: {string.Join(", ", groups)}.", citations, citations.Count);
    }

    private AnalystRetrievalResult ListByProtocol(AnalystIntent intent)
    {
        var matchEmitters = intent.Protocol is null
            ? _emitters.All().ToList()
            : _emitters.All().Where(e => ProtoEq(e.Protocol, intent.Protocol)).ToList();
        var matchDevices = intent.Protocol is null
            ? new List<Device>()
            : _devices.GetDevices(int.MaxValue).Where(d => ProtoEq(d.Protocol, intent.Protocol)).ToList();

        if (matchEmitters.Count == 0 && matchDevices.Count == 0) return Empty();

        var citations = new List<EvidenceItem>();
        citations.AddRange(matchEmitters.Select(e => Cite("emitter", e.Id)));
        citations.AddRange(matchDevices.Select(d => Cite("device", d.Id)));

        var label = intent.Protocol ?? "all";
        var answer =
            $"{matchEmitters.Count} {label} emitter(s), {matchDevices.Count} {label} device(s): "
            + string.Join(", ", matchEmitters.Select(e => e.Id).Concat(matchDevices.Select(d => d.Id))) + ".";
        return new AnalystRetrievalResult(answer, citations, citations.Count);
    }

    private AnalystRetrievalResult Occupancy()
    {
        var latest = _spectrum.Recent(1).FirstOrDefault();
        if (latest is null || latest.PowerDbfs.Length == 0) return Empty();

        double median = Median(latest.PowerDbfs);
        double threshold = median + OccupancyMarginDb;
        int occupied = latest.PowerDbfs.Count(b => b >= threshold);
        double fraction = (double)occupied / latest.PowerDbfs.Length;

        var citation = Cite("spectrum",
            $"{latest.CenterFreqHz}@{latest.Time.ToString("o", CultureInfo.InvariantCulture)}");
        var answer =
            $"Spectrum occupancy at {latest.CenterFreqHz / 1_000_000.0:0.###} MHz: "
            + $"{fraction * 100:0.#}% of bins occupied ({occupied}/{latest.PowerDbfs.Length}).";
        return new AnalystRetrievalResult(answer, [citation], 1);
    }

    private static bool IsUnknown(string protocol) => ProtoEq(protocol, "Unknown");

    private static bool ProtoEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Describe(TimeSpan w) =>
        w.TotalHours >= 1 ? $"{w.TotalHours:0.#}h" : $"{w.TotalMinutes:0}min";

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }
}
