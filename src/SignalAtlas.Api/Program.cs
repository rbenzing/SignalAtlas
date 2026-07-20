using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SignalAtlas.Analyst;
using SignalAtlas.Api;
using SignalAtlas.Enhancement;
using SignalAtlas.Collector;
using SignalAtlas.Decode;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;

var builder = WebApplication.CreateBuilder(args);

// SAFE-DEFAULT BINDING (SPEC §4.7 review note): the operator model is single-operator with a
// pass-through auth gate, so the API should bind to LOCALHOST (loopback) by default. Only bind a
// non-loopback address after the auth gate is upgraded to real token auth + RBAC. Configure the
// bind address via ASPNETCORE_URLS / Kestrel config in the deployment, not by widening the default.

builder.Services.AddSingleton<IAuthorizationGate, SingleOperatorPassThroughGate>();

// Secrets provider (SPEC §4.6): Claude API key (when cloud uplift is enabled) comes from the OS
// secret store / env / user-secrets — never the repo or DB. Null when unset (offline default).
builder.Services.AddSingleton<ISecretProvider>(sp =>
    new ConfigurationSecretProvider(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ClaudeApiKeyAccessor>();

// SDR health sink (SPEC §5.5 NFR-R1): reconnect/health events land here. Default is a log sink.
builder.Services.AddSingleton<ISdrHealthSink, LoggingSdrHealthSink>();

// Ingestion drops counter (SPEC §4.9 NFR-T1/C3): shared holder surfaced at /metrics as drops_total.
builder.Services.AddSingleton<IngestionDropsMonitor>();

// RFC 7807 problem-details (SPEC §9.4): every problem response (thrown, 400, 404, 429, 503) carries
// the request correlationId (NFR-R2). Registered once here so Results.Problem + the global handler use it.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["correlationId"] = CorrelationId.For(ctx.HttpContext);
        ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Prometheus metrics (SPEC §5.5 NFR-R3): a per-app registry, exposed ungated at /metrics.
var metricsRegistry = builder.Services.AddSignalAtlasMetrics();

// Fixed-window rate limiting (SPEC §9): 100 req/s per client → 429 problem-details on rejection.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
            }));
    options.OnRejected = async (context, _) =>
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Type = "https://httpstatuses.io/429",
                Detail = "Request rate exceeded. Retry after a short delay.",
            },
        });
    };
});

// Enforce a max request body size (SPEC §9 limits). Kestrel is the production server; TestServer
// ignores this, which is harmless for the contract suite.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1 * 1024 * 1024); // 1 MiB

// Decode & Device-ID pipeline (SPEC §8.4): all protocol decoders behind the registry,
// OUI/LAA lookup, and the device resolver. The in-memory repos consume IDeviceResolver /
// IAnomalyEngine to seed, so these engine registrations must stay ahead of persistence.
builder.Services.AddSingleton<IProtocolDecoder, AdsBDecoder>();
builder.Services.AddSingleton<IProtocolDecoder, FmRdsDecoder>();
builder.Services.AddSingleton<IProtocolDecoder, WifiBeaconDecoder>();
builder.Services.AddSingleton<IProtocolDecoder, BleAdvDecoder>();
builder.Services.AddSingleton<IProtocolDecoder, LoRaHeaderDecoder>();
builder.Services.AddSingleton<IProtocolDecoder, ZigbeeMacDecoder>();
builder.Services.AddSingleton<IDecoderRegistry, DecoderRegistry>();
builder.Services.AddSingleton<IOuiLookup, OuiLookup>();
builder.Services.AddSingleton<IDeviceResolver, DeviceResolver>();

// IQ→frame demodulators feeding the live decode stage (SPEC §8.4). ADS-B is the first.
builder.Services.AddTransient<IDemodulator, AdsBDemodulator>();
builder.Services.AddSingleton<ICprPositionResolver, CprPositionResolver>();

// Edge signal-processing / classification / correlation / anomaly engines (SPEC §8.2/§8.3/§8.5/§8.8).
// Registered here (the composition root) so the live ingestion pipeline can resolve them.
builder.Services.AddSingleton<ISignalProcessor, SignalAtlas.Processing.SignalProcessor>();

// Hot-swappable classifier (SPEC §8.9, G6): the rule scorer (default) and the ML softmax model both
// implement IClassifier, so the engine is selected purely by config — Classification:Engine = "rules"
// (default) | "ml". Both keep the ingestion/classification pipeline green.
var classificationEngine = builder.Configuration["Classification:Engine"] ?? "rules";
if (string.Equals(classificationEngine, "ml", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IClassifier>(_ => new SignalAtlas.Ml.MlClassifier());
else
    builder.Services.AddSingleton<IClassifier, SignalAtlas.Classification.RuleBasedClassifier>();
builder.Services.AddSingleton<ICorrelationEngine, SignalAtlas.Correlation.WeightedCorrelationEngine>();
builder.Services.AddSingleton<IAnomalyEngine, SignalAtlas.Anomaly.AnomalyEngine>();

// Docker-OPTIONAL persistence (SPEC §4.3): Postgres/TimescaleDB when a "SignalAtlas" connection
// string is configured, otherwise the offline-first in-memory seeded repos.
builder.Services.AddSignalAtlasPersistence(builder.Configuration);

// NL Spectrum Analyst (SPEC §8.12, M12) — dual-mode, NO offline LLM. The offline engine uses the
// deterministic intent classifier + shared retrieval/tool + citation layer over the repos; the cloud
// engine delegates only PHRASING to Claude via IClaudeClient while carrying the same cited records.
// Offline is the safe default (NFR-R4): AlwaysOfflineConnectivity + StubClaudeClient mean no network
// and no key are needed. The live Claude HTTP client (M13) reads the key via ISecretProvider (§4.6).
builder.Services.AddSingleton<IIntentClassifier, IntentClassifier>();
builder.Services.AddSingleton<IConnectivity, AlwaysOfflineConnectivity>();
builder.Services.AddSingleton<IClaudeClient, StubClaudeClient>();
builder.Services.AddSingleton<IAnalystRetrieval>(sp => new AnalystRetrieval(
    sp.GetRequiredService<ISignalRepository>(),
    sp.GetRequiredService<IDeviceRepository>(),
    sp.GetRequiredService<IEmitterRepository>(),
    sp.GetRequiredService<IAlertRepository>(),
    sp.GetRequiredService<ISpectrumBuffer>(),
    sp.GetService<IClock>() ?? new SignalAtlas.Pipeline.HostClock()));
builder.Services.AddSingleton<OfflineAnalyst>();
builder.Services.AddSingleton<CloudAnalyst>();
builder.Services.AddSingleton<IAnalystEngine>(sp =>
{
    bool cloudEnabled = string.Equals(
        sp.GetRequiredService<IConfiguration>()["Analyst:CloudEnabled"], "true", StringComparison.OrdinalIgnoreCase);
    bool keyPresent = sp.GetRequiredService<ClaudeApiKeyAccessor>().IsConfigured;
    return new AnalystEngineSelector(
        sp.GetRequiredService<OfflineAnalyst>(),
        sp.GetRequiredService<CloudAnalyst>(),
        sp.GetRequiredService<IConnectivity>(),
        cloudEnabled,
        keyPresent);
});

// Optional Claude Enhancement Pass (SPEC §8.13, M13) — an OPTIONAL, operator-chosen data-enhancement
// processor that is NEVER on the critical path (AC-DA0). It reuses IClaudeClient + IConnectivity from
// the analyst wiring, so offline (the default) the run is queued (AC-DA5) and Stub Claude is used. It
// writes attributed, cited enrichments (AC-DA1) as an advisory overlay that never mutates edge rows
// (AC-DA2), handing Claude only the structured egress payload (AC-DA3).
builder.Services.AddSingleton<DeferredClaudeAnalyzer>(sp => new DeferredClaudeAnalyzer(
    sp.GetRequiredService<ISignalRepository>(),
    sp.GetRequiredService<IEmitterRepository>(),
    sp.GetRequiredService<IAnalysisRunRepository>(),
    sp.GetRequiredService<IEnrichmentRepository>(),
    sp.GetRequiredService<IClaudeClient>(),
    sp.GetRequiredService<IConnectivity>(),
    sp.GetService<IClock>() ?? new SignalAtlas.Pipeline.HostClock()));
builder.Services.AddSingleton<IDeferredAnalyzer>(sp => sp.GetRequiredService<DeferredClaudeAnalyzer>());

// Real HackRF over USB (SPEC §4.1) is registered as the ISampleSource ONLY when a device is
// present; with no HackRF the live pipeline falls back to a file/synthetic source.
builder.Services.AddHackRfCollectorIfAvailable();

// Real-time live push (SPEC §9.3 /hub/live): SignalR is built into ASP.NET Core. The pipeline calls
// ILiveNotifier as it produces artifacts; SignalRLiveNotifier fans them out to all hub clients.
builder.Services.AddSignalR();
builder.Services.AddSingleton<ILiveNotifier, SignalRLiveNotifier>();

// Live edge ingestion (SPEC §4.10). Gated OFF unless Ingestion:Enabled == "true", so the contract
// tests (WebApplicationFactory, no ingestion config) never trigger it.
builder.Services.AddHostedService<PipelineHostedService>();

var app = builder.Build();

// Outermost: convert unhandled exceptions to problem-details (SPEC §9.4). CorrelationId middleware
// runs INSIDE it so the id is already on HttpContext.Items when the handler formats the response.
app.UseExceptionHandler();
// 404s (and other status-only responses) become problem-details too (SPEC §9.4).
app.UseStatusCodePages();

app.UseRouting();
// WebSockets middleware for the browser HackRF IQ ingress (/ingest/iq).
app.UseWebSockets();
app.UseRateLimiter();
// Conservative response security headers on every response (SPEC §4.6/§4.7 safe defaults).
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();

// Prometheus /metrics (ungated) + request/repo metrics (SPEC §5.5 NFR-R3).
app.UseSignalAtlasMetrics(metricsRegistry);

// Every /api/v1 route is behind the authorization gate (SPEC §4.7) and carries the marker
// metadata that the route-gated contract test verifies.
var api = app.MapGroup("/api/v1")
    .AddEndpointFilter<AuthorizationGateFilter>()
    .WithMetadata(new AuthGateMarker());

api.MapGet("/signals", IResult (ISignalRepository repo, HttpContext ctx) =>
{
    if (!Pagination.TryResolve(ctx, out var page, out var error))
        return error!;

    var items = repo.GetSignals(page.Take).Skip(page.Offset).Take(page.Limit).ToList();
    return Results.Ok(new ApiEnvelope<IReadOnlyList<Signal>>(
        ApiEnvelope<IReadOnlyList<Signal>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        items));
});

api.MapGet("/devices", IResult (IDeviceRepository repo, IAuditLog audit, HttpContext ctx) =>
{
    if (!Pagination.TryResolve(ctx, out var page, out var error))
        return error!;

    // NFR-S2: every identifier/location read is audit-logged (probes are not).
    audit.Record("local-operator", "read:devices", ctx.Request.QueryString.Value ?? string.Empty);

    var items = repo.GetDevices(page.Take).Skip(page.Offset).Take(page.Limit).ToList();
    return Results.Ok(new ApiEnvelope<IReadOnlyList<Device>>(
        ApiEnvelope<IReadOnlyList<Device>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        items));
});

api.MapGet("/alerts", IResult (IAlertRepository repo, HttpContext ctx) =>
{
    if (!Pagination.TryResolve(ctx, out var page, out var error))
        return error!;

    var items = repo.GetAlerts(page.Take).Skip(page.Offset).Take(page.Limit).ToList();
    return Results.Ok(new ApiEnvelope<IReadOnlyList<Alert>>(
        ApiEnvelope<IReadOnlyList<Alert>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        items));
});

// The MVP "what changed / how many" answer (SPEC §9.2 GET /summary, MVP criterion 7 text form).
api.MapGet("/summary", (ISignalRepository signals, IDeviceRepository devices, IAlertRepository alerts, HttpContext ctx) =>
{
    var summary = new
    {
        signalCount = signals.Count(),
        deviceCount = devices.Count(),
        alertCount = alerts.Count(),
    };
    return Results.Ok(new ApiEnvelope<object>(
        ApiEnvelope<object>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        summary));
});

// NL Spectrum Analyst Q&A (SPEC §9.2 POST /analyst/query, §8.12). Enveloped answer carries the text,
// the citations for every record used (P6 — non-empty unless nothing matched), the mode
// (offline|cloud), and the resolved query type. Empty text → 400 RFC 7807 problem-details (§9.4).
api.MapPost("/analyst/query", IResult (AnalystQueryRequest? req, IAnalystEngine engine, HttpContext ctx) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Text))
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Query text is required.",
            detail: "Provide a non-empty 'text' field.");

    var answer = engine.Answer(new AnalystQuery(req.Text));
    return Results.Ok(new ApiEnvelope<AnalystAnswer>(
        ApiEnvelope<AnalystAnswer>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        answer));
});

// ─── Optional Claude Enhancement Pass (SPEC §9.2, §8.13, M13) — gated + enveloped ───────────────────

// Collection sessions (SPEC §9.2 GET /sessions).
api.MapGet("/sessions", (ISessionRepository repo, HttpContext ctx) =>
    Results.Ok(new ApiEnvelope<IReadOnlyList<Session>>(
        ApiEnvelope<IReadOnlyList<Session>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        repo.All())));

// Enhancement-candidate count to inform run/skip (SPEC §9.2, AC-DA7): the residual Unknown/low-confidence
// signals + unresolved devices, computed from edge data alone — NO Claude is invoked.
api.MapGet("/sessions/{id}/enhancement-candidates",
    (string id, ISignalRepository signals, IEmitterRepository emitters, HttpContext ctx) =>
    {
        var report = EnhancementCandidates.Count(signals.GetSignals(int.MaxValue), emitters.All());
        return Results.Ok(new ApiEnvelope<EnhancementCandidateReport>(
            ApiEnvelope<EnhancementCandidateReport>.CurrentSchemaVersion,
            CorrelationId.For(ctx),
            report));
    });

// Start an optional enhancement run over a session/range (SPEC §9.2 POST /analysis/runs, §8.13). Offline
// (the default posture) → the run is QUEUED, not failed (AC-DA5). Returns the run.
api.MapPost("/analysis/runs", (AnalysisRunRequest? req, IDeferredAnalyzer analyzer, HttpContext ctx) =>
{
    var model = string.IsNullOrWhiteSpace(req?.Model) ? "claude-sonnet-4-6" : req!.Model!;
    var run = analyzer.Run(new AnalysisRequest(req?.SessionId, req?.From, req?.To, model));
    return Results.Ok(new ApiEnvelope<AnalysisRun>(
        ApiEnvelope<AnalysisRun>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        run));
});

// A single analysis run's status + report (SPEC §9.2 GET /analysis/runs/{id}). 404 when missing.
api.MapGet("/analysis/runs/{id:guid}", IResult (Guid id, IAnalysisRunRepository repo, HttpContext ctx) =>
{
    var run = repo.Get(id);
    if (run is null)
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Analysis run not found.",
            detail: $"No analysis run with id '{id}'.");

    return Results.Ok(new ApiEnvelope<AnalysisRun>(
        ApiEnvelope<AnalysisRun>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        run));
});

// The advisory, cited enrichment overlay (SPEC §9.2 GET /enrichments?run=&target=). Optional filters.
api.MapGet("/enrichments", (IEnrichmentRepository repo, HttpContext ctx) =>
{
    Guid? runId = Guid.TryParse(ctx.Request.Query["run"], out var r) ? r : null;
    string? target = ctx.Request.Query.TryGetValue("target", out var t) && !string.IsNullOrWhiteSpace(t)
        ? t.ToString() : null;

    return Results.Ok(new ApiEnvelope<IReadOnlyList<Enrichment>>(
        ApiEnvelope<IReadOnlyList<Enrichment>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        repo.Query(runId, target)));
});

// Accept an enrichment (SPEC §9.2 POST /enrichments/{id}/accept, AC-DA4). 404 when missing.
api.MapPost("/enrichments/{id:guid}/accept", IResult (Guid id, IEnrichmentRepository repo, HttpContext ctx) =>
    SetEnrichmentStatus(id, Enrichment.Accepted, repo, ctx));

// Reject an enrichment (SPEC §9.2 POST /enrichments/{id}/reject, AC-DA4). 404 when missing.
api.MapPost("/enrichments/{id:guid}/reject", IResult (Guid id, IEnrichmentRepository repo, HttpContext ctx) =>
    SetEnrichmentStatus(id, Enrichment.Rejected, repo, ctx));

static IResult SetEnrichmentStatus(Guid id, string status, IEnrichmentRepository repo, HttpContext ctx)
{
    if (repo.Get(id) is null)
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Enrichment not found.",
            detail: $"No enrichment with id '{id}'.");

    repo.SetStatus(id, status);
    return Results.Ok(new ApiEnvelope<Enrichment>(
        ApiEnvelope<Enrichment>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        repo.Get(id)!));
}

// Emitters (SPEC §9.2 GET /emitters): enveloped + paginated like /signals, backed by
// IEmitterRepository. Each emitter carries the location estimate + uncertainty + protocol +
// identifiers + evidence the RF Map needs to plot markers and uncertainty circles (SPEC §8.6).
api.MapGet("/emitters", IResult (IEmitterRepository repo, HttpContext ctx) =>
{
    if (!Pagination.TryResolve(ctx, out var page, out var error))
        return error!;

    var items = repo.All().Skip(page.Offset).Take(page.Limit).ToList();
    return Results.Ok(new ApiEnvelope<IReadOnlyList<Emitter>>(
        ApiEnvelope<IReadOnlyList<Emitter>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        items));
});

// A single emitter by id (SPEC §9.2 GET /emitters/{id}): 404 RFC 7807 problem-details when missing.
api.MapGet("/emitters/{id}", IResult (string id, IEmitterRepository repo, HttpContext ctx) =>
{
    var emitter = repo.All().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
    if (emitter is null)
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Emitter not found.",
            detail: $"No emitter with id '{id}'.");

    return Results.Ok(new ApiEnvelope<Emitter>(
        ApiEnvelope<Emitter>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        emitter));
});

// Recent PSD frames for the waterfall (SPEC §9.2 /spectrum/frames, §8.2). count defaults to 64,
// capped at 256; each frame's bins are downsampled to <=256 to bound the payload.
api.MapGet("/spectrum/frames", (ISpectrumBuffer buffer, HttpContext ctx) =>
{
    const int defaultCount = 64;
    int count = defaultCount;
    if (ctx.Request.Query.TryGetValue("count", out var cv) && int.TryParse(cv, out var parsed) && parsed > 0)
        count = parsed;
    count = Math.Min(count, InMemorySpectrumBuffer.DefaultCapacity);

    var frames = buffer.Recent(count)
        .Select(f => new SpectrumFrameDto(
            f.Time, f.CenterFreqHz, f.SampleRateHz, SpectrumSupport.Downsample(f.PowerDbfs)))
        .ToList();

    return Results.Ok(new ApiEnvelope<IReadOnlyList<SpectrumFrameDto>>(
        ApiEnvelope<IReadOnlyList<SpectrumFrameDto>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        frames));
});

// Occupancy-vs-frequency from the most recent frame (SPEC §9.2 /spectrum/occupancy, §8.2).
api.MapGet("/spectrum/occupancy", (ISpectrumBuffer buffer, HttpContext ctx) =>
{
    var latest = buffer.Recent(1).FirstOrDefault();
    OccupancyDto payload;
    if (latest is null)
    {
        payload = new OccupancyDto(0, 0, [], [], 0, 0);
    }
    else
    {
        var bins = SpectrumSupport.Downsample(latest.PowerDbfs);
        var freq = new long[bins.Length];
        for (int i = 0; i < bins.Length; i++)
            freq[i] = SpectrumSupport.BinFreqHz(latest.CenterFreqHz, latest.SampleRateHz, bins.Length, i);

        double threshold = SpectrumSupport.Median(bins) + SpectrumSupport.OccupancyMarginDb;
        int occupied = bins.Count(b => b >= threshold);
        double fraction = bins.Length == 0 ? 0.0 : (double)occupied / bins.Length;

        payload = new OccupancyDto(latest.CenterFreqHz, latest.SampleRateHz, freq, bins, threshold, fraction);
    }

    return Results.Ok(new ApiEnvelope<OccupancyDto>(
        ApiEnvelope<OccupancyDto>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        payload));
});

// Per-band coverage / last-seen indicator (SPEC §9.2 /spectrum/coverage, §4.4). Last-seen is the
// most recent signal OR frame whose center frequency falls in the band; age is relative to now.
api.MapGet("/spectrum/coverage", (ISpectrumBuffer buffer, ISignalRepository signals, HttpContext ctx) =>
{
    var now = DateTimeOffset.UtcNow;

    // (time, centerFreqHz) samples from recent signals + buffered frames.
    var samples = signals.GetSignals(int.MaxValue).Select(s => (s.Time, Freq: s.CenterFreqHz))
        .Concat(buffer.Recent(int.MaxValue).Select(f => (f.Time, Freq: f.CenterFreqHz)))
        .ToList();

    var bands = SpectrumSupport.Bands.Select(b =>
    {
        DateTimeOffset? lastSeen = samples
            .Where(x => b.Contains(x.Freq))
            .Select(x => (DateTimeOffset?)x.Time)
            .DefaultIfEmpty(null)
            .Max();

        double? age = lastSeen is null ? null : (now - lastSeen.Value).TotalSeconds;
        return new CoverageBandDto(b.Key, b.Label, b.LowHz, b.HighHz, lastSeen, age, lastSeen is not null);
    }).ToList();

    return Results.Ok(new ApiEnvelope<IReadOnlyList<CoverageBandDto>>(
        ApiEnvelope<IReadOnlyList<CoverageBandDto>>.CurrentSchemaVersion,
        CorrelationId.For(ctx),
        bands));
});

// Real-time hub (SPEC §9.3 /hub/live). Mapped OUTSIDE /api/v1, so it is NOT behind the
// AuthorizationGateFilter — hub auth follows the single-operator posture (SPEC §4.7, loopback bind)
// and is an upgrade point (add token auth when the gate is upgraded to real RBAC).
app.MapHub<LiveHub>("/hub/live");

// Binary WebSocket IQ ingress for a browser-owned HackRF (SPEC §4.10). Un-gated, like /hub/live.
app.MapIqIngress();

// Liveness probe (SPEC §5.5 NFR-R2): 200 whenever the process is up. Ungated.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness probe (SPEC §5.5 NFR-R2): verifies the persistence layer is reachable. In-memory is
// always ready; the EF/DB path does a lightweight CanConnect. 503 problem-details when not ready. Ungated.
app.MapGet("/ready", IResult (IServiceProvider sp) =>
{
    var db = sp.GetService<SignalAtlasDbContext>();
    var ready = db is null || db.Database.CanConnect();
    return ready
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service not ready.",
            detail: "A required dependency (persistence) is unavailable.");
});

app.Run();

public partial class Program;

/// <summary>Request body for POST /analyst/query (SPEC §9.2). Text is validated non-empty (400 otherwise).</summary>
public sealed record AnalystQueryRequest(string? Text);

/// <summary>Request body for POST /analysis/runs (SPEC §9.2, §8.13). All optional; model defaults to Sonnet.</summary>
public sealed record AnalysisRunRequest(string? SessionId, DateTimeOffset? From, DateTimeOffset? To, string? Model);
