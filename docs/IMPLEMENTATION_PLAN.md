# Signal Atlas — Implementation Plan

**Derives from:** [SPEC.md](SPEC.md) v3.4 (contract)
**Method:** TDD Red → Green → Refactor. No production code without a failing test first.
**Status:** MVP (M0–M7) built; AI phases (M9/M12/M13) built with seamed stubs; hardware/sync ops (M8) partial. See **§7 Build Status (as-built)** below for the honest per-milestone record and the "next to add" list. The authoritative as-built matrix lives in [SPEC.md §19](SPEC.md#19-implementation-status-as-built).

> **Per-feature working docs.** Incremental features built after M0 (WebUSB HackRF ingress,
> ADS-B demodulator, band presets, ESRI basemap, NOAA APT phases, RF audio player, NL analyst)
> each have a brainstorm spec + implementation plan under `docs/superpowers/` — these are kept
> **locally only** (git-ignored working notes), not part of the committed contract. SPEC.md and
> this file are canonical.

---

## 0. Toolchain (verified 2026-07-07)

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.301 | Backend + all C# tests |
| Node | 18.20.8 | Frontend (Vite/React) |
| npm | 10.8.2 | |
| git | 2.54.0 | |
| **Docker** | **absent** | **Blocks Testcontainers** — Postgres/Timescale integration, encryption-at-rest, and DB round-trip tests are written but marked `[Trait("Category","NeedsDocker")]` and skipped in CI until a Docker host exists (see §Deviations). |

---

## 1. Architecture Decisions (implementation-level, refine SPEC §16 ADRs)

- **Language/runtime:** C# / .NET 10 for the entire pipeline (Collector → Processing → Classification → Decode → Correlation → Geospatial → Behavior → Anomaly → Api). Addresses review risk **R5/A1**: DSP performance (NFR-T2, 20 MS/s) is validated by a benchmark spike in M1 **before** the DSP architecture is locked; if managed code misses the budget, the `ISignalProcessor` seam allows a native (VOLK/FFTW P/Invoke) implementation behind the same contract.
- **Determinism (P5):** all randomness injected via an `IRandomSource`; no `DateTime.Now`/`Guid.NewGuid()` in the deterministic core — time comes from `IClock`, IDs from deterministic content hashing where SPEC §7.8 requires.
- **Hardware seam (SPEC §4.1):** `ISampleSource` / `IPositionSource`. Real impls (`HackRfSampleSource`, `UbloxPositionSource`) land in M8; `FileSampleSource` + `SyntheticSampleSource` from M0 give zero-hardware CI.
- **Persistence:** EF Core (forward-only migrations, SPEC §15) over PostgreSQL + TimescaleDB. Repository interfaces in Domain; an in-memory repo backs unit/contract tests so the pipeline runs without a DB.
- **Auth (SPEC §4.7):** `IAuthorizationGate`, default `SingleOperatorPassThroughGate`. A contract test asserts **every** mapped route passes through it (review G-J / route-gated CI gate).
- **API:** ASP.NET Core Minimal API `/api/v1`, RFC 7807 problem-details, SignalR hub `/hub/live`. gRPC deferred until protos are specified (closes review R11 by explicit deferral, not a dangling reference).

## 2. Solution Layout (SPEC §12.1)

```
SignalAtlas.sln
src/
  SignalAtlas.Domain          # entities, value objects, interfaces, evidence — no deps
  SignalAtlas.Collector       # scan scheduler, ISampleSource consumers
  SignalAtlas.Processing      # DSP: FFT, occupancy, features
  SignalAtlas.Classification  # IClassifier rule scorer
  SignalAtlas.Decode          # IProtocolDecoder registry, IDeviceResolver
  SignalAtlas.Correlation     # emitter/device assignment
  SignalAtlas.Geospatial      # centroid + uncertainty
  SignalAtlas.Behavior        # behavior profiles
  SignalAtlas.Anomaly         # rule detectors
  SignalAtlas.Persistence     # EF Core, repositories, migrations
  SignalAtlas.Api             # Minimal API, SignalR, auth gate
web/                          # React + TS + Vite + MUI + MapLibre
tests/
  SignalAtlas.Tests.Unit
  SignalAtlas.Tests.Contract
  SignalAtlas.Tests.Integration   # NeedsDocker
  SignalAtlas.Tests.Golden
  SignalAtlas.Tests.E2E           # Playwright (web)
```

## 3. Milestone → ticket → test map

Authoritative ticket source is **SPEC §11**. Each ticket = one RED test (or a small triangulated set) → GREEN → REFACTOR. Milestones ship in order M0→M13; M0–M7 = MVP, M8 hardware+ops, M9–M13 = AI phases.

**M0 (walking skeleton) — active:**

| Ticket | Test (RED) | Runnable now? |
|---|---|---|
| M0-T1 FileSampleSource | deterministic replay: same bytes → identical ordered blocks | yes (unit) |
| M0-T2 Collector/dwell | one Observation per dwell from synthetic source; UTC + monotonic seq | yes (unit) |
| M0-T3 receive-only | transmit path never initialized (spy on ISampleSource) | yes (unit) |
| M0-T4 auth gate | every mapped API route passes through IAuthorizationGate | yes (contract) |
| M0-T5 GET /signals | golden JSON shape, envelope `{schemaVersion,correlationId,payload}` | yes (contract) |
| M0-T6 persist/read-back | encrypted ephemeral Timescale round-trip | **NeedsDocker (skip)** |
| M0-T7 cold-copy | data files reveal no plaintext identifiers (NFR-S1) | **NeedsDocker (skip)** |
| M0-T8 React list | Playwright renders signal list | scaffold; E2E deferred |
| M0-T9 CI | pipeline fails on a red test | yes (workflow) |

Milestones M1–M13 expand from SPEC §8 component test lists + §11 backlog; detailed tickets are pulled into this table as each milestone opens.

## 4. Delegation model (yeschef brigade)

- **scout** — locate/trace when a change spans files.
- **line-cook** — implement one well-specified ticket (RED test already named) in parallel; returns compact diff.
- **expeditor** — run full suite + coverage gate before a milestone is declared done.

Parallelism rule: only dispatch independent tickets concurrently (no shared file/contract). M0-T1/T2/T3 (Collector+Domain) are one cohesive slice built together; M0-T4/T5 (Api) are a second slice; they can run in parallel after the solution scaffold exists.

## 5. Deviations from SPEC (tracked)

1. **Docker-dependent tests skipped locally.** M0-T6/T7 and all `SignalAtlas.Tests.Integration` carry `Category=NeedsDocker` and are excluded from the local run and the fast CI lane; a Docker CI lane runs them. This preserves the SPEC §12.4 gate intent without a Docker host on the dev machine.
2. **gRPC deferred** until protos are specified (review R11) — not in M0 scope; REST+SignalR only.
3. **Frontend E2E deferred** past M0 scaffold (Playwright browser download + web build) — the React list view is scaffolded and unit-checkable; the Playwright E2E gate opens at M0 close on a machine with browsers.

## 6. Definition of Done per milestone (SPEC §14)

All Red-Green items checked; CI green incl. new tests; coverage gate met on `/Processing /Classification /Decode /Correlation`; demoable outcome runs from clean checkout; no verdict lacks evidence (P4/P6 contract green); relevant NFRs measured. M0–M12 DoD never depends on Claude/M13.

---

## 7. Build Status (as-built) — what's done, what's next

Honest snapshot of the milestone roadmap (SPEC §10/§11) against the code on `main`. The
full component-level matrix is [SPEC.md §19](SPEC.md#19-implementation-status-as-built); this
is the milestone summary. Legend: ✅ built · ◑ partial (seam present, hardware/live-only piece
deferred) · ○ not started.

| M | Outcome | State | Notes |
|---|---|---|---|
| M0 | Walking skeleton: `.iq`→Collector→store→`GET /signals`→React list; receive-only + route-gated tests | ✅ | Encryption-at-rest test is Docker-lane only. |
| M1 | PSD + features; occupancy + waterfall; coverage indicator | ✅ | Live Spectrum view + `/spectrum/*` endpoints. |
| M2 | Protocol + confidence + evidence | ✅ | Rule scorer (`SignalAtlas.Classification`). |
| M3 | All-protocol decode → devices w/ vendor | ◑ | Decoders operate on **frame bytes**; per-protocol IQ→bits demod deferred except **ADS-B** (live `AdsBDemodulator`) and **NOAA APT** (`AptDecoder`). |
| M4 | Emitters via decoded IDs (+RF fallback) | ✅ | Correlation runs RF-only for non-demodulated live protocols. |
| M5 | RF Map: heatmap + honest uncertainty | ✅ | Map + uncertainty + ADS-B contacts + NOAA weather overlay; **`GET /map/heatmap`** exposed (GeolocationEngine wired, audit #14) **and consumed** by the RF Map Heatmap toggle — the overlay now renders server observation-density cells, not the old emitter-only client heatmap. |
| M6 | Behavior profiles; timeline replay | ◑ | `BehaviorEngine` built + tested but **not DI-wired** (staged seam, audit #14 — needs per-emitter sighting history); `/replay` endpoint deferred. |
| M7 | Alerts (incl. new_device) + `/summary` | ✅ | Alerts view + `/summary`. |
| M8 | Real HackRF/GPS; retention; sync; backup/export; NFR soak; degrade | ◑ | **Browser WebUSB HackRF** ingress built (`/ingest/iq`); native SoapySDR source, `/sync/*`, `/export`, soak/NFR suites deferred (need device + Docker host). |
| M9 | ML classification ≥95% w/ attribution + drift | ◑ | Pure-C# softmax classifier (`SignalAtlas.Ml`) behind `IClassifier`; ~0.97 on **synthetic** data. ONNX/GBM/CNN upgrade is the documented production step. |
| M10 | Hardware re-ID + spoof detection | ◑ | `SignalAtlas.Fingerprint` seam present; reference-gated accuracy deferred (needs stable ref + field IQ). |
| M11 | Activity forecasts + predicted-anomaly | ◑ | `BehaviorPredictor` / `PredictedAnomalyDetector` built + tested but **not DI-wired** (staged seam, audit #14) — same sighting-history dependency as M6. |
| M12 | Grounded NL analyst (offline + Claude uplift) | ✅ | `OfflineAnalyst` + `/analyst/query` + **Analyst page** built. `CloudAnalyst` uses the **live `AnthropicClaudeClient`** (official Anthropic C# SDK, async, Sonnet 5 default) when `Analyst:CloudEnabled=true` + a key is present, else `StubClaudeClient`. |
| M13 | Optional Claude enhancement (sessions/runs/enrichments) | ◑ | Backend built: `/sessions`, `/sessions/{id}/enhancement-candidates`, `/analysis/runs`, `/enrichments` + accept/reject. **Live `AnthropicClaudeClient` wired** (async, per-run model); **no enrichment UI yet**. |

### Built beyond the original roadmap (incremental features)
- **NOAA APT weather-satellite pipeline** — image decode (`AptDecoder`, in-memory only, invariant-#3 carve-out) + **Phase 2 georeference** (Vallado-validated near-Earth SGP4, Celestrak TLEs, `AptGeoReferencer`) → `GET /devices/{id}/geo` → weather-image quad overlay on the RF Map.
- **RF Audio Player** — server-side demod (`AudioDemodulator`: WBFM/NBFM/AM/USB/LSB/CW, true phasing/Hilbert SSB) streamed over the ungated `/audio` WebSocket; replaces the Tune card on Live Spectrum.
- **Context-aware RF Map** — Weather basemap (NOAA-gated) + ESRI Satellite/Street/Topo switcher; nav item disabled off mapping bands (`isMappingBand`).
- **Live ADS-B** — real 8 MS/s streaming Mode S demod + CPR position → aircraft on the map.
- **Demo-data gating** — all seed data behind `SeedDemoData` (default off): honest empty states, not synthetic filler.

### Next to add (prioritized backlog)
1. **Enrichment accept/reject UI** — surface the M13 `/enrichments` lifecycle in the web app; now that the live client is wired, an operator with a key can run a real enhancement pass and the overlay becomes usable. *(Live `IClaudeClient` HTTP impl — **done**: `AnthropicClaudeClient`, official Anthropic C# SDK, async, Sonnet 5 default; enable with `Analyst:CloudEnabled=true` + a key.)*
2. **Export/replay/sync endpoints** — `/replay`, `/export` (GeoJSON/KML/CSV), `/sync/push` + `/sync/pull` (SPEC §9.2 gaps). *(`/map/heatmap` — **done**, audit #14.)*
3. **Native SoapySDR/HackRF source + per-protocol demodulators** — needs the physical device + field `.iq` captures (SPEC §4.1, landmine).
4. **Docker-lane CI** — Postgres/Timescale round-trips, encryption-at-rest, retention (needs a Docker host).
5. **ML production upgrade** — ONNX Runtime / GBM / CNN behind `IClassifier` to hit NFR-A2 on real signals.
