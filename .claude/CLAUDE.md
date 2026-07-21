# CLAUDE.md — Signal Atlas

Working notes for AI agents and engineers. This captures what you **cannot infer from the
code**: invariants that must never break, deliberate seams that look unfinished but aren't,
and concrete landmines we already hit. Read [SPEC.md](../docs/SPEC.md) for the contract,
[README.md](../README.md) to run it, [OPERATIONS.md](../docs/OPERATIONS.md) for ops.

Signal Atlas is a **receive-only RF intelligence platform**: SDR → classify → decode device
identity → correlate emitters → geolocate → behavior/anomaly → map/UI. .NET 10 backend
(15 projects), React/TS frontend. Built test-first, offline-first.

---

## Prime invariants — NEVER violate these

1. **Receive-only (L1).** No transmit path may ever be initialized or exposed. `ISampleSource`
   and `IHackRfDevice` deliberately expose **no** transmit member; reflection tests assert it.
   Never add one.
2. **Explainable-only (P4/P6).** Every classification / decode / correlation / alert / analyst
   answer carries **non-empty evidence or citations**. The DB enforces it on `signals` and
   `enrichments` (CHECK); repos enforce it on write; contract tests re-assert it. A verdict with
   empty evidence is a bug.
3. **Metadata, not content (L2/L3).** Decoders parse only cleartext identity/control fields
   (BSSID, ICAO, DevAddr, PAN…). Never parse/persist encrypted payload or personal content.
   The M13 egress guard (`EgressGuard`) asserts no raw IQ / cleartext leaves the edge.
4. **Deterministic core (P5).** Same bytes → identical output. In `src/` domain/pipeline code do
   **not** use `DateTime.Now`, `Guid.NewGuid()`, or unseeded `Random`. Use the injected `IClock`,
   `DeterministicGuid.From(seed)`, and a **seeded** RNG (`SignalAtlas.Ml.DeterministicRng`).
   The API/HTTP layer may use per-request GUIDs — that's outside the deterministic core.
5. **Every `/api/v1` route is auth-gated.** Routes go through the `/api/v1` group which carries
   `AuthGateMarker` + `AuthorizationGateFilter`. A contract test enumerates endpoints and fails if
   any is ungated. `/health`, `/ready`, `/metrics`, and `/hub/live` are intentionally ungated.
6. **Wire contract = camelCase DTOs.** REST envelope is `{schemaVersion, correlationId, payload}`;
   paginated list endpoints (`/signals`, `/devices`, `/alerts`, `/emitters`) also carry an optional
   top-level `nextCursor` (base64 forward cursor, null on the last page). SignalR hub events and the
   frontend `web/src/api.ts` types **must match the real JSON payloads exactly** (see landmine #1 below).

---

## Build / run / test (commands that actually work)

- **No `global.json`** — requires the **.NET 10 SDK** on PATH. `.slnx` is the solution (XML format;
  needs VS 2022 17.10+ to open in Visual Studio).
- Backend: `dotnet run --project src/SignalAtlas.Api` → **http://localhost:5285** (the Vite proxy
  targets 5285 — keep them in sync if you change it).
- Frontend: `cd web && npm install && npm run dev` → http://localhost:5173.
- Live pipeline: set env `Ingestion__Enabled=true` (optionally `Ingestion__IqFile=...`).
- Tests (everywhere): `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"`.
- Coverage gate (mirrors CI): **Git-Bash needs `-p:` not `/p:`** (`/p:` is read as a path), and
  **commas inside `-p:Include` must be escaped as `%2c`**. See `.github/workflows/ci.yml` for the
  exact invocation.
- Test categories: default (runs anywhere) · `Category=NeedsDocker` (Postgres/Timescale/encryption —
  needs a Docker host) · `Category=Nightly` (slow eval gates).

---

## Deliberate seams that look unfinished (they're intentional)

- **IQ→bits demodulation is DEFERRED** behind `IDemodulator` for **most** protocols — decoders
  (`SignalAtlas.Decode`) operate on already-demodulated **frame bytes**, not raw IQ; the per-protocol
  DSP front-end needs a physical HackRF + field `.iq` fixtures. **Exception: ADS-B** has a real
  streaming demodulator (`AdsBDemodulator`) turning 1090 MHz IQ into Mode S frames (it carries frames
  across IQ-block boundaries — see landmine #10). Other live protocols still classify but do **not**
  determine devices yet (correlation runs RF-only). This is by design, not a TODO to "finish" blindly.
- **Live Claude client is STUBBED.** `IClaudeClient`'s real HTTP impl (reads the key via
  `ISecretProvider` → `SignalAtlas:ClaudeApiKey`) is not built; `StubClaudeClient` is used everywhere.
  The analyst cloud mode and the M13 enhancement pass are fully wired around it. Claude is **optional
  and never on the critical path** (AC-DA0): the platform is complete with it disabled.
- **ML is a pure-C# softmax classifier**, not a heavy model. ONNX Runtime / GBM / CNN is the
  documented production upgrade behind the same `IClassifier` seam. Its ~0.97 accuracy is on
  **synthetic** data; NFR-A2 (95% on real signals) is gated on decode-self-labeled field data (§12.2).
- **Persistence is Docker-OPTIONAL.** `AddSignalAtlasPersistence(config)` selects EF Core + Postgres
  when `ConnectionStrings:SignalAtlas` is set, else **in-memory seeded** repos. The seed (one LoRa
  signal, one ADS-B "Aircraft" device, 3 located emitters, spectrum frames) is why contract tests and
  the offline UI have data with no DB. Don't "fix" empty data by adding DB requirements.
- **Map basemap is offline-by-default** (graticule only). A real vector basemap drops in via
  `VITE_BASEMAP_STYLE` + a bundled `web/public/basemap.pmtiles` — the `pmtiles` seam exists.

---

## Landmines (concrete — don't repeat these)

1. **Frontend/API type drift is the #1 recurring bug.** `web/src/api.ts` types are hand-written and
   have silently diverged from real payloads (occupancy, coverage, Device, Emitter all drifted once).
   TypeScript **cannot** catch this — it's runtime JSON. **Before wiring a view, curl the running
   endpoint and match the type to the actual payload.**
2. **`namespace SignalAtlas.Classification` shadows the Domain type `Classification`.** Inside that
   project (and its tests) write the result type fully-qualified: `SignalAtlas.Domain.Classification`.
3. **MapLibre `circle-radius` with `zoom`** must be a **top-level** `interpolate`/`step` — you cannot
   wrap it in `max`/arithmetic. Real-meter radius = `rBase · 2^zoom` via `["interpolate",
   ["exponential",2],["zoom"], 0, rBase, 24, rBase*2^24]`, floor applied *inside* the stop outputs.
4. **`ReadOnlyMemory<byte>.Span` cannot cross a `yield`.** In iterators, grab `.Span` inside the loop
   before the `yield`, never hold it across.
5. **EF Core version conflict (MSB3277).** Npgsql pulls EF Relational 10.0.4 while the projects use
   10.0.9 → pin `Microsoft.EntityFrameworkCore.Relational` 10.0.9 explicitly in `Api` + `Tests.Integration`.
6. **SQLite is TEST-ONLY.** `Microsoft.EntityFrameworkCore.Sqlite` must **never** be referenced by the
   production `Persistence`/`Api` projects — it drags a vulnerable native lib (NU1903) into the
   shipped artifact. It lives only in `tests/SignalAtlas.Tests.Persistence`. Postgres is production.
7. **Provider-split schema.** `SignalAtlasDbContext` uses composite `(time,id)` PKs on **Npgsql**
   (Timescale hypertables need the partition column in the PK) but single-column surrogate keys on
   **SQLite**. JSON members (evidence/identifiers/features/citations/**receiver_config**) are stored as
   **text via a ValueConverter** (a record stored this way also needs a `ValueComparer` for EF change
   tracking) so the same model works on both providers. Note: `Observation.BandwidthHz` is the
   **analog filter passband** (from the receiver config when known), **not** the ADC sample rate.
8. **`Emitter` has no bandwidth or last_seen field**, so the correlation engine's temporal &
   bandwidth scoring terms are **dormant** (reserved in `CorrelationOptions`). Adding them requires
   persisting emitter state over time — a tracked follow-up, not a quick fix.
9. **Background servers via `&`**: the Bash tool's cwd can reset between calls; start the web dev
   server with an explicit `cd web`. Kill stray listeners by port before re-running.
10. **Stateful demodulators must be `AddTransient`, not `AddSingleton`.** `AdsBDemodulator` carries an
    inter-block sample tail (frames straddling IQ-block boundaries) as instance state, so it is
    registered **`AddTransient<IDemodulator, …>`** — each stream/connection gets its own instance
    (both pipeline build sites resolve `sp.GetServices<IDemodulator>()` fresh per pipeline, and one
    `Run()` feeds that instance blocks sequentially). Reverting it to a singleton interleaves carry-over
    across concurrent `/ingest/iq` streams and corrupts them. The demod is also **eager** (returns a
    `List`, not a `yield` iterator) so the carry can't desync on partial enumeration.

---

## Working method

- **TDD is the law**: RED (a failing test that fails on an *assertion*, not a missing scaffold) →
  GREEN (minimum code) → REFACTOR. Tests are the spec; when a test and the code disagree, resolve it
  deliberately. Commit per milestone.
- **Parallel subagents cause integration drift** (see landmine #1). When work is split across agents,
  **verify the integration** (curl endpoints, run the app, screenshot the UI) — a green build is not
  proof the pieces fit. "Render it and look at it" for any UI change.
- Milestones map to SPEC §11 (M0 walking skeleton → M13). Dependency order is roughly the `src/`
  project list; decode (M3) precedes ML (M9) because decoded IDs are the ML label source (§12.2).

---

## Known residuals (need external resources, not code cleverness)

- Real SoapySDR/HackRF USB streaming + the per-protocol demodulators (need the device + `.iq` captures).
- Live `IClaudeClient` HTTP impl (needs an API key + network).
- Docker-lane tests (Postgres/Timescale/encryption-at-rest) — need a Docker host.
- BLE CRC-24 & LoRa MIC are self-consistent but not verified against real captures.
- Single-node only; multi-sensor fusion + RBAC are seamed but deferred (ADR-9).
