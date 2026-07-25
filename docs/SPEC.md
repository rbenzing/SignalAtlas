# Signal Atlas — Engineering Specification

**Version:** 3.4
**Status:** Buildable Spec — full platform, all gaps resolved, **claims independently verified (§18)**; **as-built status tracked in §19**
**Methodology:** Test-Driven Development — Red / Green / Refactor
**Audience:** AI implementation agents and human engineers

> **v3.4:** Adds **§19 Implementation Status (as-built)** — an honest per-component record of what is
> built on `main` vs. seamed/deferred, plus the prioritized "next to add" list. Records incremental
> features shipped beyond the original roadmap: the **NOAA APT** decode + georeference (Phase 1 & 2,
> §8.4), the **RF Audio Player** (server-side WBFM/NBFM/AM/USB/LSB/CW demod, §8.14), and the built-out
> **NL analyst** (§8.12) with its web page. The contract in §1–§18 is unchanged; §19 is the reality check.
>
> **v3.0:** Every gap in the vision is now *resolved with a decision* (§3, §16 ADR log), not
> left open. Resolutions added since v2: hybrid edge-first deployment, sensing/scan
> strategy with honest detection-probability limits, power-calibration model, data-at-rest
> security, offline-first sync, and a labeled-data strategy. Decode covers the **full
> protocol set in parallel**. The NL analyst is **dual-mode**: offline pattern/ML intent matching with templated grounded answers (no local LLM), Claude API uplift when online.
>
> **v3.1:** Adds the **collect-now / analyze-later** workflow — real-time edge collection in the
> field, then a deferred **Claude-assisted batch analysis** pass (when online) that enriches
> classification, device determination, and mapping as an advisory, cited overlay (§4.10, §8.13).
>
> **v3.3:** Every checkable technical assumption was **independently verified against authoritative
> sources** (§18 Claim Verification Log). Corrections applied: HackRF stock clock is a **~20 ppm
> crystal, not a TCXO** (§4.5); upstream **PostgreSQL has no native TDE** — volume encryption is the
> baseline (§4.6); **BLE/Wi-Fi decode on HackRF is constrained** (single-channel BLE, legacy-20 MHz
> Wi-Fi only — §8.4); legal basis cited incl. *Joffe v. Google* (§4.2). No assumption is left
> unverified or overstated.
>
> **v3.2:** Reframes Claude as an **optional data-enhancement processor**. The on-device ML is the
> complete, authoritative result; the operator *chooses* to run the Claude pass after a session — or
> skips it entirely if the ML was good enough. Claude is never required and never on the critical path.

---

## 0. How to read this document

This document is the *contract*. Every feature is
expressed as **testable behavior first**: no production code until a failing test (RED)
describes the behavior; then minimum code to pass (GREEN); then improve design without
changing behavior (REFACTOR). The test-list blocks are the authoritative work order.

Implementer reading order: §3 Gap Resolutions → §6 Principles & TDD → §10 Roadmap → §11
Red-Green Backlog → component specs (§8) + data model (§7) as reference.

---

## 1. Document Control

| Field | Value |
|---|---|
| Build source of truth | `SPEC.md` (this file) |
| Decision log | §16 ADR log (all prior open questions now decided) |
| Change policy | Spec changes require a corresponding test change; no behavior ships untested |

---

## 2. Scope

### 2.1 In scope — fully specified
- **Decode & Device ID** (M3): **all vision protocols in parallel** — Wi-Fi, BLE, LoRa, Zigbee, ADS-B, FM/RDS — demodulated to determine concrete devices.
- **AI Phase 2 ML classification** (M9); **Phase 3 RF fingerprinting** (M10); **Phase 4 behavior prediction** (M11); **Phase 5 NL spectrum analyst** (M12).
- **Deferred Claude analysis** (M13): collect RF in the field, then process a session **later with the Claude API** to better analyze and map — advisory, cited enrichments over the deterministic results.
- Deployment, sync, security, observability, TDD workflow, and CI gates for all of the above.

### 2.2 Hard invariants (always true, tested)
- **Receive-only.** Transmit path never initialized (§4.2 L1).
- **Decode for device determination only**, never decryption of protected content (§4.2 L2–L4).
- **Explainable only.** No verdict without machine-readable evidence/citation (§6.1 P3).
- **Offline-first.** The edge node is fully operational with zero connectivity (§4.3).
- **ML-complete; Claude optional.** The edge (DSP + rules/ML + decode + correlation + map) produces a
  complete, usable result on its own. The Claude enhancement pass (§4.10, §8.13) is **optional and
  operator-chosen** — never required, never on the critical path. Disabling it changes nothing about
  the platform's core functionality (tested: AC-DA0).

### 2.3 Out of scope (this version)
- Active RF; defeating encryption; mobile apps; multi-tenant SaaS. (Multi-sensor fusion and
  multi-user RBAC are architecturally seamed-in but not built — see §16 ADR-2, ADR-9.)

---

## 3. Gap Resolutions — every vision gap, decided

The vision is directionally complete but not buildable as-is. Each gap below is **resolved
with a concrete decision** and a spec home. There are **no open questions** (§16 records the
rationale for the forking ones).

|---|---|---|---|
| G1 | No concrete schemas | Full DDL + message contracts defined | §7 |
| G2 | No quantified NFRs | Numeric thresholds + verifying test type for each | §5 |
| G3 | Hardware undefined | HackRF + u-blox GPS reference rig behind `ISampleSource`/`IPositionSource`; SoapySDR adapter for multi-SDR future | §4.1 |
| G4 | Legal/decode posture | Receive-only; decode identity/control metadata for device ID; no decryption; jurisdiction profile | §4.2 |
| G5 | DSP params unspecified | FFT 4096/Hann/50%; median noise floor; +6 dB occupancy threshold; defined feature set | §8.2 |
| G6 | Classification rules absent | Transparent weighted-rule scorer + reference rules; ML behind same contract (M9) | §8.3, §8.9 |
| G7 | Device Layer had no mechanism | **Decode & Device-ID engine** + `devices` table | §8.4 |
| G8 | Correlation undefined | Decoded-ID-primary + weighted RF-feature fallback, thresholds tested | §8.5 |
| G9 | Geolocation impossible claims | Power-weighted centroid + honest uncertainty radius; never false point fixes | §8.6 |
| G10 | No API contracts | Versioned REST + SignalR + gRPC, RFC 7807 errors, consumer-driven contracts | §9 |
| G11 | No milestone sequencing/DoD | M0–M12 roadmap + per-milestone DoD | §10, §14 |
| G12 | No test strategy (the ask) | Full TDD method, pyramid, golden harness, CI gates | §6, §11, §12 |
| G13 | GPS-denied undefined | Emit null coords + `position_q='none'`; never block/fabricate | §8.1 |
| G14 | No clock model | UTC from GPS→NTP→host, `time_source` enum, per-collector monotonic seq | §5.4 |
| G15 | No retention policy | Tiered retention + Timescale continuous aggregates | §7.6 |
| G16 | No error/observability model | Problem-details taxonomy; `/health`,`/ready`; correlationId tracing; Prometheus metrics | §9.4, §5.5 |
| G17 | No deterministic replay format | `.iq` + `.meta.json` fixtures; deterministic core; golden harness | §12 |
| G18 | AI Phases 2–5 unspecified | Each a fully-specified milestone with test lists | §8.9–§8.12 |
| **G19** | **Sweep blind-time:** HackRF sees ~20 MHz instantaneous of a 1 MHz–6 GHz range | Priority-driven scan scheduler with per-band dwell/revisit; **detection probability documented + bounded by NFR-T8**; transient-miss risk made explicit, not hidden | §4.4, §8.1 |
| **G20** | **Uncalibrated power:** HackRF dBm is not absolute | Power stored as **relative (dBFS) by default** with a `calibrated` flag; optional per-gain calibration table; thresholds are relative, not absolute | §4.5, §7.2 |
| **G21** | **Fingerprint clock limits:** CFO/transient features need a stable reference | M10 features chosen drift-robust; optional GPS-disciplined reference; NFR-A5 gated on reference quality; accuracy ceiling documented | §4.5, §8.10 |
| **G22** | **Surveillance-adjacent data** (device IDs + locations) | **Encrypted at rest**, single-operator auth seam, query audit log, optional identifier pseudonymization, retention caps | §4.6, §4.7 |
| **G23** | **Labeled-IQ scarcity** vs 85/95% accuracy targets | 3-source data strategy incl. **decode-as-label-factory** (decoded ICAO/BSSID auto-labels signals); accuracy targets phased to dataset maturity | §12.2 |
| **G24** | **No deployment/packaging** | Dockerized edge bundle (Compose), offline install, documented update path | §4.3, §15 |
| **G25** | **No backup/export** of identity stores (the product's memory) | Backup/restore of devices/emitters/fingerprints; GeoJSON/KML/CSV export | §7.7, §9.2 |
| **G26** | **Field-host resource budget** (CPU/thermal/battery; edge ML, no local LLM) | Documented budget + graceful-degrade policy (shed scan bands, pause background ML/cloud uplift under load) | §4.8, §5.3 |
| **G27** | **Inter-stage backpressure** undefined | Bounded queues (Redis Streams / channels) with drop-with-metric policy tied to NFR-T1 | §4.9 |
| **G28** | **Hybrid sync/conflict model** undefined | Append-only event sync, deterministic IDs, idempotent upsert, documented merge rules | §7.8 |

---

## 4. System Constraints

### 4.1 Hardware baseline (reference rig)
| Component | Reference | Software assumptions |
|---|---|---|
| SDR | HackRF One (via SoapySDR adapter) | 1 MHz–6 GHz tuning; ≤20 MS/s 8-bit I/Q; **~20 MHz instantaneous** (drives §4.4); half-duplex; receive-only in software |
| Antenna | Wideband discone + band-specific | Gain only; not modeled |
| GPS | u-blox-class USB, NMEA 0183 | 1 Hz fix; UTC + lat/lon/alt + fix quality; optional 1PPS for §4.5 |
| Host | Linux x64, ≥4 cores, ≥16 GB RAM, SSD; optional GPU | DSP/decode on CPU; GPU optional for M9/M10 training; **edge inference must run CPU-only** |

`FileSampleSource` (replays `.iq`) and `SyntheticSampleSource` (generates known signals)
let the entire pipeline + CI run with **zero hardware**.

### 4.2 Responsible-use, decode & privacy posture (tested invariants)
- **L1 Receive only.** Transmit path never initialized (§11 M0 spy test).
- **L2 Decode for identity & determination.** Extract device-identifying/control-plane
  metadata (Wi-Fi BSSID/SSID, BLE advertiser MAC/name/mfr-data, Zigbee PAN/short addr,
  ADS-B ICAO/callsign, FM RDS station ID) — "what device, doing what," not personal content.
- **L3 No defeating encryption.** Encrypted payloads stay opaque; only cleartext
  identity/management metadata is parsed.
- **L4 Content minimization.** Persist identifiers + signal metadata; incidental cleartext
  not retained past the determination window (§7.6). Raw IQ only in a bounded host ring buffer.
- **L5 Jurisdiction config.** `RegulatoryProfile` declares band plan + per-decoder policy;
  out-of-policy bands/decoders disabled. Default conservative.
- **L6 Operator acknowledgement.** First-run confirmation of receive-only, responsible, lawful use; logged.
- **L7 Controlled egress.** Deferred Claude analysis (§4.10) is operator-initiated and sends only
  **structured signal/emitter metadata** to the Claude API — never raw IQ, never cleartext personal
  content; honors pseudonymization mode (§4.6) and the jurisdiction profile.

**Legal basis (US; verified §18 — software constraints, not legal advice).** The metadata-not-payload
line above is the legally-defensible one: the Wiretap Act (18 USC 2511) and Communications Act §605
(47 USC 605) restrict intercepting/divulging the *contents* of communications, while 18 USC
2511(2)(g) exempts radio communications "readily accessible to the general public." **Decoding
publicly-broadcast identifiers/beacons (ADS-B, Wi-Fi BSSID, BLE advertising) is materially different
from capturing communication payload** — and *Joffe v. Google* (9th Cir. 2013) is exactly why L3 is a
hard line: it held that capturing *payload* off an unencrypted Wi-Fi network can still violate the
Wiretap Act, so "unencrypted" never implies "lawful to capture content." Caveats: circuit-dependent;
some U.S. states (CA, FL, …) have stricter two-party laws; non-US jurisdictions differ → §4.2 L5
`RegulatoryProfile` + operator responsibility.

### 4.3 Deployment topology — hybrid edge-first (ADR-1)
- **Edge node** (the field laptop) is the unit of operation: Collector, Processing,
  Classification, Decode, Correlation, Geospatial, Behavior, Anomaly, local Postgres+Timescale,
  Redis, API, local web UI, and the **local NL analyst** — all run on-device.
- **Fully operational offline.** Connectivity is never required for collection, analysis, or
  the analyst (offline pattern/ML, no LLM). (NFR-R4.)
- **Opportunistic sync** to an optional central backend when connected (§7.8): pushes
  append-only events upstream and pulls config/model updates. Sync never blocks the edge.
- **Analyst, offline by default.** Offline, the NL analyst uses **ML / pattern-matching intent
  recognition** over the stores and renders **templated, grounded** answers — **no local LLM**.
- **Cloud uplift, optional.** When online + enabled, the analyst calls the **Claude API** for
  free-form phrasing/reasoning, still grounded and cited. Same `IAnalystEngine` (§8.12).

### 4.4 Sensing / scan strategy & detection honesty (G19)
HackRF observes only ~20 MHz at a time; the band of interest spans GHz. We therefore **never
claim continuous coverage**. The Collector runs a **priority scan scheduler**:
- A `ScanPlan` lists bands with `{centerHz, sampleRateHz, dwellMs, priority, revisitMaxS}`.
  Reference priority bands: 1090 MHz (ADS-B), 2.400–2.485 GHz (Wi-Fi/BLE/Zigbee), 902–928 &
  433 MHz ISM (LoRa/ISM), 88–108 MHz (FM), plus a configurable wideband survey sweep.
- The scheduler guarantees each band is revisited within `revisitMaxS` (NFR-T8). Between
  revisits, signals in that band can be missed; this **detection-probability limit is surfaced
  in the UI and API** (per-band "last seen / coverage" indicator), never hidden.
- Graceful degrade (§4.8): under load, low-priority bands are shed first and logged.

### 4.5 Power calibration & frequency reference (G20, G21)
- **Power is relative by default.** HackRF is uncalibrated; `power_dbm` is recorded as
  **dBFS-relative** with a `power_ref` field (`relative`\|`calibrated`). Optional per-gain
  calibration table converts to approximate dBm and flips `power_ref='calibrated'`.
  Classification/anomaly thresholds operate on **relative power and SNR**, never assuming absolute dBm.
- **Frequency reference.** The HackRF One's **stock reference is a plain crystal oscillator
  (~20 ppm, not a TCXO** — verified §18); its drift bounds fingerprint precision (M10). Fingerprint
  features are chosen drift-robust (transient shape, relative I/Q imbalance); for tighter
  carrier-frequency-offset features, feed an external 10 MHz **CLKIN** (square wave) or an
  aftermarket TCXO / GPS-disciplined reference (1PPS). NFR-A5 is gated on reference quality and the
  achievable ceiling is documented, not over-promised.

### 4.6 Security & data-at-rest (G22)
- **Encrypted at rest.** Identity stores (`devices`, `emitters`, `device_fingerprints`) and the
  database live on an **encrypted volume (filesystem/LUKS) — the baseline mechanism**, with
  `pgcrypto` for column-level encryption of identifier columns. **Upstream PostgreSQL has no native
  TDE** (verified §18); native TDE exists only via forks/extensions (Percona `pg_tde`, EDB,
  Cybertec) — not assumed here. Tested: a cold copy of the data files yields no plaintext identifiers.
- **Audit log.** Every query that reads device identifiers/locations is recorded
  (who/when/what filter) in an append-only audit table.
- **Pseudonymization mode (optional).** A config flag hashes identifiers at rest (keyed),
  for sensitivity-conscious operation; device-type/behavior still function.
- **Secrets.** Claude API key (when online uplift enabled) stored in OS secret store, never in repo/DB.

### 4.7 Operator / auth model — single-operator, auth-ready (ADR-2)
- One operator; **no login friction** by default. All API routes pass through an
  `IAuthorizationGate` whose default impl is a single-operator pass-through.
- The seam is real: adding token auth + RBAC later requires implementing the gate, not
  reworking controllers. Contract test asserts every route is gated.

### 4.8 Field-host resource budget & graceful degrade (G26)
- Documented budget: DSP+decode ≤ ~60% CPU steady-state on reference host; edge ML (the M9
  classifier + the analyst intent model) is **CPU-only, quantized**; **no generative LLM runs on the edge**.
- Degrade policy (tested): under sustained CPU/thermal pressure → (1) shed low-priority scan
  bands, (2) pause background ML retraining / cloud uplift, (3) widen rollup granularity. Each step logged + metric'd.

### 4.9 Pipeline concurrency & backpressure (G27)
- Stages connect via **bounded queues** (Redis Streams on the edge node, or in-process
  channels). On saturation: apply backpressure to the producer; if the SDR can't be slowed,
  **drop oldest with a counted metric** (`drops_total`) — never silent loss, never unbounded growth (NFR-T1, NFR-C3).

### 4.10 Collection + **optional** Claude enhancement (ADR-10)
Signal Atlas is **complete with the edge alone**; Claude is an optional enhancement step the operator
turns on per session, or never.
- **Real-time edge processing (always, offline) — the authoritative result:** the full edge pipeline
  (DSP → rules/ML classify → decode → correlate → map → behavior → anomaly) runs live during a
  **collection session**, so the operator walks/drives and gets protocols, devices, emitters, and a
  complete map in the field with no connectivity. **This is the product.** If the ML was good enough,
  you stop here — nothing else is required.
- **Optional Claude enhancement (later, only if you choose):** when online, the operator *may* run a
  batch **data-enhancement** pass over a session. It targets the **residual hard cases** — `Unknown`
  and low-confidence signals, unresolved devices — and hands only the session's *structured RF
  intelligence* (signals, features, decoded frames, emitters, evidence — never raw IQ or personal
  content, §4.2 L7) to the **Claude API** to reclassify, refine device determinations, narrate
  emitters, improve mapping, find cross-session patterns, and write a session report.
- **Informed choice, never automatic:** each session exposes an **enhancement-candidate count**
  (share of `Unknown`/low-confidence signals + unresolved devices) so the operator can decide
  "ML resolved 96% confidently — skip" vs "lots of unknowns — enhance." The pass is **never auto-run**.
- **Enhancement output is an attributed, advisory overlay** (`enrichments`, §7.9) — versioned, cited,
  additive; it never overwrites the deterministic edge results, and the operator accepts/rejects each
  proposal. The deterministic core stays reproducible (P5); Claude's non-determinism is quarantined to the overlay.

---

## 5. Non-Functional Requirements (quantified, testable)

### 5.1 Throughput & latency
| ID | Requirement | Threshold | Verified by |
|---|---|---|---|
| NFR-T1 | Sustained ingest | ≥50 obs/s, drops counted not silent | Load test |
| NFR-T2 | DSP per FFT frame | ≤5 ms p95 | Benchmark |
| NFR-T3 | Capture → signal in API | ≤2 s p95 | Integration timing |
| NFR-T4 | Classification decision | ≤20 ms p95 | Benchmark |
| NFR-T5 | Decode of a captured identity frame | ≤50 ms p95 | Benchmark |
| NFR-T6 | Map/timeline query (1 h, ≤10k) | ≤500 ms p95 | API perf |
| NFR-T7 | Analyst answer (offline pattern/ML / online Claude) | ≤1 s / ≤6 s p95 to grounded answer | E2E timing |
| **NFR-T8** | **Priority-band revisit interval** | each priority band revisited ≤ its `revisitMaxS` | Scheduler test |

### 5.2 Accuracy
| ID | Requirement | Threshold | Verified by |
|---|---|---|---|
| NFR-A1 | Rule-based classification (golden) | ≥85% top-1 | Eval |
| NFR-A2 | ML classification (M9) | ≥95% top-1; ≥0.90 macro-F1 | Eval, nightly |
| NFR-A3 | Decode field-parse correctness (decodable frames) | ≥98% | Decode golden eval |
| NFR-A4 | Correlation false-new (steady state) | ≤5% rule / ≤2% with decoded IDs | Eval |
| NFR-A5 | Fingerprint re-ID (M10), reference-gated | ≥90% true-match @ ≤5% false-match | Eval |
| NFR-A6 | Evidence/citation present on every verdict | 100% | Contract |

### 5.3 Capacity, storage & resources
| ID | Requirement | Threshold | Verified by |
|---|---|---|---|
| NFR-C1 | Signals at full resolution | ≥30 days | Retention test |
| NFR-C2 | Storage growth after downsampling | ≤2 GB/day | Soak test |
| NFR-C3 | Raw IQ buffer | bounded ring ≤N s | Buffer test |
| NFR-C4 | Edge ML inference (classifier + analyst intent model) | CPU-only, within budget, ≤200 ms p95; **no generative LLM on edge** | Resource test |

### 5.4 Time & clock
UTC from GPS→NTP→host with `time_source` enum; per-collector monotonic `seq`. (G14.)

### 5.5 Reliability, security & observability
- NFR-R1: Survives SDR USB disconnect; auto-reconnect + health event; loses ≤ in-flight block.
- NFR-R2: `/health`+`/ready`; correlationId follows an observation through the pipeline.
- NFR-R3: Prometheus metrics (ingest, queue depth, drops, classify/decode rate, fingerprint match, scan coverage).
- **NFR-R4: Edge node fully operational offline; sync resumes on reconnect with zero data loss (append-only).**
- **NFR-S1: Data encrypted at rest — cold file copy reveals no plaintext identifiers.**
- **NFR-S2: Every identifier/location read is audit-logged.**

---

## 6. Engineering Principles & the TDD Method

### 6.1 Principles (enforceable)
1. **Objects, not energy** — Observation → Signal → Device/Emitter → Behavior. (P2)
2. **Everything historical** — no destructive updates; corrections are new rows. (P3)
3. **Explainable only** — no classification/decode/correlation/alert/answer without non-empty evidence/citation. (P4)
4. **Hardware behind interfaces** — pure, testable pipeline. (§4.1)
5. **Deterministic core** — same bytes → identical output; randomness injected.
6. **Grounded AI** — analyst answers only from stored evidence and cites it. (P6)
7. **Honest limits** — coverage gaps (§4.4), uncalibrated power (§4.5), single-Rx geolocation (§8.6), and fingerprint ceilings (§4.5) are surfaced, never masked.

### 6.2 Red-Green-Refactor (the law)
```
RED   → smallest failing test naming the next behavior; fails for the RIGHT reason (assertion).
GREEN → minimum code to pass; no untested capability.
REFACTOR → remove duplication/clarify under green; no new behavior. → next test → RED
```
Rules: test list first; one reason to fail; triangulate (≥2 cases); tests are the spec
(disagreements resolved deliberately + logged); refactor only under green.

### 6.3 Worked micro-example (occupancy)
```csharp
// RED 1: empty spectrum 0% occupied
[Fact] public void Occupancy_IsZero_WhenAllBelowThreshold() {
    Assert.Equal(0.0, new OccupancyCalculator(-90).Fraction(Db(-100,-98,-101)), 3); }
// GREEN: return 0.0;
// RED 2: bins above threshold count
[Fact] public void Occupancy_CountsBinsAboveThreshold() {
    Assert.Equal(0.5, new OccupancyCalculator(-90).Fraction(Db(-100,-50,-101,-40)), 3); }
// GREEN: count >= threshold / length.  REFACTOR: extract compare, name magic.
```

### 6.4 Test taxonomy & pyramid
| Layer | Covers | Tooling | Budget |
|---|---|---|---|
| Unit | DSP math, classifier rules, decoder field parsing, correlation scoring, geometry, scan scheduler | xUnit / Vitest | <50 ms |
| Contract | JSON & gRPC shapes; evidence-non-empty; API I/O; auth-gated routes | xUnit + JSON schema; consumer-driven | <100 ms |
| Integration | Pipeline w/ `FileSampleSource`; DB round-trips on ephemeral Postgres/Timescale; sync round-trip | Testcontainers | seconds |
| Golden | DSP/decode/classifier output vs checked-in expected | golden harness (§12.3) | seconds |
| E2E | 7 MVP criteria + device-ID + analyst Q&A | Playwright + API driver | minutes |
| NFR | perf, accuracy, retention, fingerprint, offline-resilience, encryption-at-rest | BenchmarkDotNet, eval harness | nightly |
~70% unit/contract, ~20% integration/golden, ~10% E2E/NFR.

---

## 7. Data Model (G1, G15, G20, G22, G25, G28)

PostgreSQL + TimescaleDB on the edge node, **on an encrypted volume**. `observations`,
`signals`, `decoded_frames` are hypertables; `emitters`, `devices`, `device_fingerprints`,
`behavior_profiles`, `alerts`, `audit_log`, `sync_outbox` are regular tables. Redis = cache/queue.

### 7.1 Entity overview
```
Observation ─classify→ Signal ─decode→ DecodedFrame ─determine→ Device
                          │                                       │
                          └────────────── correlate ──────────────┤
                                                                   ▼
                                                Emitter ── BehaviorProfile / DeviceFingerprint(M10)
                                                   └── Alert
```

### 7.2 Tables (authoritative field list; migrations own exact types)
```sql
CREATE TABLE observations (
  id BIGINT GENERATED ALWAYS AS IDENTITY, time TIMESTAMPTZ NOT NULL,
  time_source TEXT NOT NULL, collector_id TEXT NOT NULL, seq BIGINT NOT NULL,
  frequency_hz BIGINT NOT NULL, bandwidth_hz INTEGER NOT NULL,   -- analog filter passband (from receiver_config when known), not the ADC sample rate
  power REAL NOT NULL, power_ref TEXT NOT NULL,          -- 'relative'|'calibrated' (G20)
  snr_db REAL, latitude DOUBLE PRECISION, longitude DOUBLE PRECISION,
  position_q TEXT NOT NULL, iq_ref TEXT, correlation_id UUID NOT NULL,
  receiver_config JSONB,                                 -- RX provenance (§8.1): gain stages/baseband filter/bias-tee; null for file/synthetic sources
  PRIMARY KEY (time, id));

CREATE TABLE signals (
  id BIGINT GENERATED ALWAYS AS IDENTITY, time TIMESTAMPTZ NOT NULL,
  observation_id BIGINT NOT NULL, emitter_id TEXT, device_id TEXT,
  protocol TEXT NOT NULL, confidence REAL NOT NULL CHECK (confidence BETWEEN 0 AND 1),
  classifier TEXT NOT NULL, evidence JSONB NOT NULL,
  center_freq_hz BIGINT NOT NULL, bandwidth_hz INTEGER NOT NULL, duration_ms INTEGER,
  features JSONB NOT NULL, PRIMARY KEY (time, id),
  CONSTRAINT evidence_nonempty CHECK (jsonb_array_length(evidence) > 0));

CREATE TABLE decoded_frames (
  id BIGINT GENERATED ALWAYS AS IDENTITY, time TIMESTAMPTZ NOT NULL,
  signal_id BIGINT NOT NULL, protocol TEXT NOT NULL, frame_type TEXT NOT NULL,
  identifiers JSONB NOT NULL, decode_quality REAL NOT NULL, evidence JSONB NOT NULL,
  PRIMARY KEY (time, id));

CREATE TABLE devices (
  id TEXT PRIMARY KEY, device_type TEXT NOT NULL, primary_identifier TEXT,
  identifiers JSONB NOT NULL, vendor TEXT, protocol TEXT NOT NULL,
  first_seen TIMESTAMPTZ NOT NULL, last_seen TIMESTAMPTZ NOT NULL,
  confidence REAL NOT NULL, evidence JSONB NOT NULL);

CREATE TABLE emitters (
  id TEXT PRIMARY KEY, device_id TEXT REFERENCES devices(id),
  first_seen TIMESTAMPTZ NOT NULL, last_seen TIMESTAMPTZ NOT NULL, protocol TEXT NOT NULL,
  freq_center_hz BIGINT NOT NULL, freq_stability_hz INTEGER NOT NULL,
  est_latitude DOUBLE PRECISION, est_longitude DOUBLE PRECISION,
  est_uncertainty_m DOUBLE PRECISION, signal_count BIGINT NOT NULL DEFAULT 0,
  confidence REAL NOT NULL);

CREATE TABLE device_fingerprints (
  emitter_id TEXT PRIMARY KEY REFERENCES emitters(id), feature_vector JSONB NOT NULL,
  embedding BYTEA, stability REAL NOT NULL, ref_quality TEXT NOT NULL,  -- (G21)
  updated_at TIMESTAMPTZ NOT NULL);

CREATE TABLE behavior_profiles (
  emitter_id TEXT PRIMARY KEY REFERENCES emitters(id), pattern TEXT NOT NULL,
  period_s REAL, mobility TEXT NOT NULL, duty_cycle REAL, forecast JSONB,
  evidence JSONB NOT NULL, updated_at TIMESTAMPTZ NOT NULL);

CREATE TABLE alerts (
  id UUID PRIMARY KEY, time TIMESTAMPTZ NOT NULL, emitter_id TEXT, device_id TEXT,
  kind TEXT NOT NULL, severity TEXT NOT NULL, summary TEXT NOT NULL,
  evidence JSONB NOT NULL, acknowledged BOOLEAN NOT NULL DEFAULT FALSE);

CREATE TABLE audit_log (                                  -- (G22 / NFR-S2)
  id BIGINT GENERATED ALWAYS AS IDENTITY, time TIMESTAMPTZ NOT NULL,
  actor TEXT NOT NULL, action TEXT NOT NULL, query JSONB NOT NULL);

CREATE TABLE sync_outbox (                                -- (G28) append-only events to push
  id BIGINT GENERATED ALWAYS AS IDENTITY, time TIMESTAMPTZ NOT NULL,
  entity TEXT NOT NULL, entity_id TEXT NOT NULL, op TEXT NOT NULL,
  payload JSONB NOT NULL, synced BOOLEAN NOT NULL DEFAULT FALSE);
```

### 7.3 Evidence object — `[{feature,value,weight}]`, non-empty; decoded frames add parsed field + CRC pass. Rendered verbatim by the UI (P4).
### 7.4 Identifier object — protocol-specific `{bssid,ssid,mac,icao,callsign,pan_id,...}`; `vendor` from OUI lookup.
### 7.5 Message contracts — versioned envelopes `{schemaVersion,correlationId,payload}` (paginated list responses add an optional top-level `nextCursor`, §9); gRPC in §9.3; backward-compatible within a major; contract tests pin shapes.
### 7.6 Retention — observations full 7d→1-min rollups→drop raw 7d; signals/decoded_frames full 30d→5-min rollups kept 1y; devices/emitters/fingerprints persistent; raw IQ ring buffer only; incidental cleartext not persisted past determination. Timescale policies, tested (G15).
### 7.7 Backup & export (G25) — scheduled backup of identity stores; restore tested; export devices→GeoJSON/KML (map) + CSV/JSON; signals→JSON.
### 7.8 Sync & conflict model (G28) — `sync_outbox` is append-only; edge pushes events to backend when online; **deterministic IDs** (emitter/device IDs derived from stable content/identifiers) make upserts idempotent; merge rule: union of observations, max(last_seen), identity reconciled by primary_identifier then fingerprint; sync never blocks the edge (NFR-R4).

### 7.9 Sessions, analysis runs & enrichments (collect-now / analyze-later, ADR-10)
```sql
CREATE TABLE sessions (                                   -- a field collection recording
  id TEXT PRIMARY KEY, started TIMESTAMPTZ NOT NULL, ended TIMESTAMPTZ,
  scan_plan JSONB NOT NULL, bbox JSONB, notes TEXT);

CREATE TABLE analysis_runs (                              -- a deferred Claude pass over a session/range
  id UUID PRIMARY KEY, session_id TEXT, from_time TIMESTAMPTZ, to_time TIMESTAMPTZ,
  engine TEXT NOT NULL,                                   -- 'claude'
  model TEXT NOT NULL,                                    -- 'claude-sonnet-5' | 'claude-opus-5'
  status TEXT NOT NULL,                                   -- 'queued'|'running'|'done'|'failed'
  started TIMESTAMPTZ, finished TIMESTAMPTZ, report JSONB, tokens_used BIGINT);

CREATE TABLE enrichments (                                -- additive, attributed, advisory overlay
  id UUID PRIMARY KEY, run_id UUID NOT NULL REFERENCES analysis_runs(id),
  target_entity TEXT NOT NULL,                            -- 'signal'|'device'|'emitter'|'map_area'
  target_id TEXT NOT NULL, kind TEXT NOT NULL,            -- 'reclassification'|'device_determination'|'narrative'|'location_refinement'|'investigation'
  proposal JSONB NOT NULL, citations JSONB NOT NULL,      -- non-empty: the records the proposal is grounded on
  status TEXT NOT NULL DEFAULT 'proposed',               -- 'proposed'|'accepted'|'rejected'
  created TIMESTAMPTZ NOT NULL,
  CONSTRAINT enrichment_cited CHECK (jsonb_array_length(citations) > 0));
```
A session's signals/features/decoded frames are retained at full resolution long enough to
re-analyze later (§7.6; sessions can be pinned/exported, §7.7). Enrichments are persistent and
**never mutate the rows they annotate** — they overlay them with provenance (`run_id`, model).

---

## 8. Component Specifications
Each: **Responsibility → Interface → Algorithm/Rules → Acceptance → Starter Test List.** Implement in dependency order (§10).

### 8.1 SDR Collector (G3, G13, G19, G20)
**Responsibility:** run the **priority scan scheduler** (§4.4) over `ISampleSource`+`IPositionSource`; emit `Observation` rows with relative power + `power_ref`; preserve IQ slices for decode-eligible signals.
**Rules:** revisit each priority band ≤ `revisitMaxS` (NFR-T8); GPS-denied → null coords + `position_q='none'`; USB disconnect → reconnect + health event; transmit path never initialized; degrade by shedding low-priority bands under load (§4.8).
**Acceptance:** AC-C1 deterministic ordered stream from `.iq`; AC-C2 no-GPS handling; AC-C3 reconnect within backoff; AC-C4 scheduler honors revisit budget; AC-C5 power recorded relative with `power_ref`.
**Test list:** [ ] one obs per dwell (synthetic) · [ ] UTC + monotonic seq · [ ] GPS fix/stale/none mapping · [ ] transmit API never called (spy) · [ ] disconnect→reconnect+health · [ ] scheduler revisits priority band within budget · [ ] low-priority band shed under simulated load (logged) · [ ] IQ slice retained for decode-eligible signal · [ ] power_ref='relative' by default; 'calibrated' when table loaded

### 8.2 Signal Processing Engine (G5)
**Responsibility:** IQ → PSD frames + feature vector. **DSP:** FFT 4096/Hann/50%; noise floor = rolling per-bin median; occupancy threshold = noise+6 dB; features: center_freq, bandwidth(-3/-20 dB), peak_power(relative), snr, duration_ms, duty_cycle, modulation_hint. Deterministic (P5).
**Acceptance:** AC-P1 tone peak ±1 bin; AC-P2 occupancy 0/0.5; AC-P3 125 kHz BW ±10%; AC-P4 noise floor robust to single burst.
**Test list:** [ ] FFT golden sinusoid · [ ] Hann scalloping in tolerance · [ ] occupancy 0/0.5/1.0 · [ ] median noise floor robust to spike · [ ] -3 dB BW of chirp ±10% · [ ] burst duration from envelope

### 8.3 Classification Engine — rule-based (G6)
**Responsibility:** features → protocol + confidence + evidence, or `Unknown`. Transparent weighted-rule scorer behind `IClassifier` (ML M9 implements same contract). Reference rules: LoRa/BLE/Wi-Fi/ADS-B/FM as in §plan; below floor → `Unknown`.
**Acceptance:** AC-CL1 evidence non-empty + confidence∈[0,1]; AC-CL2 golden ≥85%; AC-CL3 no-match → `Unknown`.
**Test list:** [ ] LoRa rule → 'LoRa'+evidence · [ ] confidence∈[0,1] · [ ] evidence never empty · [ ] ambiguous → 'Unknown' · [ ] stub IClassifier keeps pipeline green · [ ] golden ≥85% (nightly)

### 8.4 Decode & Device Identification Engine (G7) — **all protocols in parallel**
**Responsibility:** demodulate identified protocols → `DecodedFrame` (identity/control metadata, §4.2) → determine `Device`.
**Decode-feasibility tiers (verified §18 — honest about HackRF limits):**
- **Tier A, fully practical on HackRF:** **ADS-B** (1090 MHz, ~2 MHz, dump1090-style — ICAO + callsign, public); **FM RDS** (57 kHz subcarrier — PI/PS station ID); **LoRa** PHY (CSS 125/250/500 kHz — DevAddr in header; LoRaWAN payload is AES-128, stays opaque); **Zigbee/802.15.4** MAC header (channels ~2 MHz occupied, **spaced 5 MHz**; PAN ID + 16/64-bit addr cleartext).
- **Tier B, constrained:** **Wi-Fi** — **legacy 20 MHz channels only** (802.11a/g/p, gr-ieee802-11 style; beacon BSSID/SSID cleartext). 40/80 MHz and modern HT/VHT/HE PHYs **exceed HackRF and are out of scope**. **BLE advertising** (adv ch 37/38/39, advertiser MAC/name/mfr-data) — the three channels span **>20 MHz, so HackRF sees one at a time** and cannot reliably follow data-channel hopping; capture is **single-channel best-effort**. A dedicated companion radio (Ubertooth/nRF sniffer) is the robust path and is the recommended add-on if BLE coverage must be complete.
- **Tier C, out:** wideband user payload; any encrypted content (L3, never decrypted).
**Interface:** `IProtocolDecoder { bool CanDecode(string); DecodeResult Decode(IqSlice, FeatureVector); }`, `IDeviceResolver { Device Resolve(IReadOnlyList<DecodedFrame>); }`. CRC/FCS must pass for a frame to count; cleartext only; OUI→vendor **only when the MAC's locally-administered bit is clear** (a randomized/LAA MAC has no meaningful OUI — flag it, don't vendor-map it).
**Acceptance:** AC-D1 ADS-B golden→ICAO/callsign FCS-pass; AC-D2 Wi-Fi beacon→BSSID/SSID+vendor; AC-D3 BLE adv→MAC/name, random-MAC flagged; AC-D4 encrypted→zero content; AC-D5 every frame/device has evidence; AC-D6 CRC fail→no device.
**Test list:** [ ] ADS-B golden · [ ] Wi-Fi beacon golden · [ ] BLE adv golden · [ ] LoRa PHY-hdr golden · [ ] Zigbee MAC-hdr golden · [ ] FM RDS golden · [ ] OUI map (unknown→null) · [ ] random/static MAC flag · [ ] CRC fail→no device · [ ] decoder registry: unknown proto no-ops · [ ] L3 encrypted→zero content extracted · [ ] resolver: identifiers→type+vendor · [ ] Device layer renders "type near place" (E2E)

**NOAA APT (Phase 1) — a separate, parallel pass-decoder** (`ISatelliteImageDecoder`/`AptDecoder`, 137 MHz FM): decodes public broadcast weather-satellite imagery to a `Device` (protocol `NOAA-APT`) alongside the frame-based decode above; the PNG lives only in the in-memory `IAptImageStore` (bounded, singleton) — never persisted, never egressed (invariant-#3 carve-out).

### 8.5 Emitter Correlation Engine (G8)
**Responsibility:** assign signal to existing or new emitter/device. **Decoded identifier is primary key** (matching BSSID/ICAO → near-certain, NFR-A4 ≤2%); else weighted RF scoring (freq proximity, BW, protocol, space, time) ≥ threshold.
**Acceptance:** AC-CR1 same ID→same device; AC-CR2 diff ID→new; AC-CR3 no-ID repeat→match via scoring; AC-CR4 false-new ≤ threshold; AC-CR5 evidence present.
**Test list:** [ ] matching BSSID→same device despite drift · [ ] diff ICAO→distinct · [ ] no-ID repeat→existing via scoring · [ ] threshold tie-break documented · [ ] last_seen/count updated + evidence · [ ] false-new ≤NFR-A4 (nightly)

### 8.6 Geospatial Engine (G9)
**Responsibility:** map products + honest estimates. Single obs → estimate=position w/ large uncertainty; multiple → power-weighted centroid + uncertainty; never false point fixes; `est_uncertainty_m` always set + shown.
**Test list:** [ ] one obs→estimate=it, uncertainty≥floor · [ ] symmetric cluster→centroid · [ ] power-weight toward strong · [ ] no positioned obs→null (not 0,0) · [ ] heatmap aggregation · [ ] RF Map renders uncertainty circles (E2E)

### 8.7 Behavior Engine — descriptive
Inter-arrival → periodic/always_on/burst; position variance → mobile. Emits `BehaviorProfile`+evidence.
**Test list:** [ ] evenly spaced→periodic w/ period · [ ] jitter in tolerance still periodic · [ ] continuous→always_on; sparse→burst · [ ] moving→mobile

### 8.8 Anomaly Engine — rule-based
Detectors: new_emitter, **new_device**, protocol_change, location_change, power_change (relative), occupancy_spike. Severity+evidence, deduped.
**Test list:** [ ] new emitter→one alert; repeat→none · [ ] new device→new_device · [ ] protocol flip→protocol_change+evidence · [ ] relative power jump→power_change · [ ] occupancy spike vs baseline→alert

### 8.9 ML Classification — Phase 2 (M9) (G18)
Same `IClassifier`; trained model (GBM and/or spectrogram CNN) with **attribution evidence** (SHAP/saliency → evidence schema). Reproducible seeded training; eval gate ≥95%/0.90-F1; **drift monitor**; provenance in `signals.classifier`. **CPU-only quantized inference on edge** (NFR-C4). Trained partly on **decode-self-labeled data** (§12.2).
**Test list:** [ ] reproducible training from seed · [ ] held-out ≥95%/F1≥0.90 (nightly) · [ ] attribution evidence per prediction · [ ] rules↔ML hot-swap keeps pipeline green · [ ] model version per signal · [ ] drift monitor fires on shifted stream · [ ] CPU-only inference within budget

### 8.10 RF / PHY Fingerprinting — Phase 3 (M10) (G18, G21)
Identify hardware from PHY imperfections (transient, CFO, I/Q imbalance, phase noise) → stable embedding; strengthens ID-less/rotating correlation; flags spoofing. **Drift-robust features; reference-gated accuracy (§4.5); `ref_quality` stored; ceiling documented.**
**Test list:** [ ] same-emitter→high similarity · [ ] distinct→low · [ ] rotated-MAC→one fingerprint · [ ] fingerprint/ID mismatch→spoof alert · [ ] re-ID meets NFR-A5 at stated ref_quality (nightly)

### 8.11 Behavior Prediction — Phase 4 (M11) (G18)
Per-emitter interval forecasting → `forecast`; large prediction error → `predicted_anomaly`.
**Test list:** [ ] periodic next-event within tolerance · [ ] forecast carries uncertainty · [ ] missed expected transmit→predicted_anomaly · [ ] forecast-vs-actual logged (nightly)

### 8.12 NL Spectrum Analyst — Phase 5 (M12) — **dual-mode, no offline LLM** (G18, ADR-1)
**Responsibility:** grounded, cited Q&A over the stores. Behind `IAnalystEngine` with two impls
sharing one **deterministic retrieval/tool + citation layer**:
- **OfflineAnalyst (no LLM):** an **ML / pattern-matching intent classifier + slot extraction**
  maps a query to a *supported query type* + parameters (e.g. "what changed today", "devices
  near X", "unknown emitters in band Y") → deterministic retrieval → a **template-rendered**,
  grounded answer with citations. Fast, fully offline, grounded by construction. Unsupported
  phrasing → an honest fallback that lists what it can answer (never a guess).
- **CloudAnalyst (Claude):** when online + enabled, Claude — **Sonnet 5** default, **Opus 5**
  for hard multi-step queries — handles free-form phrasing/reasoning, still grounded + cited
  through the same tool layer.

Auto-selects cloud when online + enabled, else offline. **No generative model runs on the edge**
(ADR-8). No cited record → no claim (P6).
**Test list (deterministic layer first):** [ ] intent classifier maps known queries → correct type+slots (unit, no LLM) · [ ] retrieval correct for time/space/protocol query · [ ] template answer cites every record used (contract) · [ ] empty result→"none found", no fabrication · [ ] unsupported query → honest capability fallback · [ ] engine selects offline when disconnected / cloud when online+enabled · [ ] cloud grounded-Q&A eval passes citation check (nightly) · [ ] NFR-T7 latency both modes

### 8.13 Optional Claude Enhancement Pass — a data-enhancement processor (M13, ADR-10)
**Responsibility:** an **optional, operator-chosen** batch pass (run after the fact, or never) that
*enhances* a `session`'s already-complete edge results using the **Claude API** — focused on the
residual `Unknown`/low-confidence cases — writing **attributed, cited `enrichments`** (§7.9) that
never overwrite edge results. **Not on the critical path:** the platform is fully functional, mapped,
and shippable with this engine disabled (AC-DA0). The system also computes a per-session
**enhancement-candidate count** to inform the operator's run/skip choice — without running Claude.
**What it does (each proposal cited; egress per §4.2 L7):**
- **Reclassify** low-confidence/`Unknown` signals with richer reasoning over the feature/evidence record.
- **Determine & merge devices**; propose vendor/type and emitter↔device links.
- **Narrate emitters** ("periodic 915 MHz near Oak St → likely weather station") with evidence.
- **Refine mapping** — reason about emitter location/movement; annotate map areas.
- **Cross-session investigations** — patterns/changes across multiple sessions.
- **Session report** — a grounded narrative summary stored on the run.
**Interface:** `IDeferredAnalyzer { Task<AnalysisRun> RunAsync(AnalysisRequest req, CancellationToken ct); }`;
uses the **same grounded retrieval/tool + citation layer** as §8.12; Claude model per difficulty (Sonnet 5
default, Opus 5 hard). The tool layer is deterministic and unit-tested **without** Claude.
**Guardrails:** operator-initiated; sends only structured metadata (no raw IQ / personal content);
every enrichment carries non-empty citations (DB-enforced); proposals are advisory until accepted.
**Acceptance:** **AC-DA0 the full platform E2E (collect → classify → decode → correlate → map →
report) passes with the enhancement engine disabled (ML-only)** — Claude is never required; AC-DA1
each enrichment has ≥1 citation; AC-DA2 no enrichment overwrites a deterministic row (overlay only);
AC-DA3 egress contains no raw IQ / cleartext (payload assertion); AC-DA4 accept/reject lifecycle
persisted; AC-DA5 offline → run queued (not failed), executes on reconnect; AC-DA6 grounded
enrichment eval passes citation check; AC-DA7 enhancement-candidate count computed without invoking Claude.
**Test list (deterministic first):** [ ] **full E2E green with enhancement disabled (ML-only)** · [ ] enhancement-candidate count from edge data alone (no Claude) · [ ] tool layer assembles a session's intelligence correctly (no Claude) · [ ] egress payload excludes IQ + cleartext (assert) · [ ] empty-citation enrichment rejected (DB constraint) · [ ] enrichment never mutates target row (overlay) · [ ] accept/reject lifecycle · [ ] offline → queued, runs on reconnect · [ ] reclassification grounded in cited signal record · [ ] session report cites its sources (nightly eval)

### 8.14 RF Audio Player — live listening (server-side demod)
**Responsibility:** demodulate the live IQ stream to PCM audio for the operator to *listen* to the
tuned signal (broadcast FM, ham, airband, SSB, CW). A monitoring aid — **receive-only** (§4.2 L1),
raw IQ never persisted or egressed (the audio tap sits behind the same egress guard as the rest of
the pipeline). **Metadata-not-content** posture is unchanged: this is analog listening for the
operator at the edge, not payload capture/persistence.
**Interface:** `AudioDemodulator` with `enum AudioMode { Wbfm, Nbfm, Am, Usb, Lsb, Cw }`; the
browser opens the **ungated `/audio` WebSocket** (posture as `/ingest/iq`, §Operations) with a
`?mode=` selector; PCM frames stream back. Demod is deterministic per block; SSB uses a **129-tap
Blackman FIR Hilbert (phasing method)** so USB/LSB genuinely reject the opposite sideband (~50 dB),
CW adds a ~700 Hz BFO.
**Acceptance:** AC-AU1 each mode produces audio at the expected tone/sign (Goertzel spectral-peak
tests); AC-AU2 USB and LSB reject the opposite sideband (not identical output); AC-AU3 no transmit
path; AC-AU4 no IQ persisted/egressed.

---

## 9. API & Interface Contracts (G10, G16)
REST/JSON `/api/v1` over HTTPS; SignalR push; gRPC internal; RFC 7807 errors; cursor pagination; UTC `from`/`to`; **all routes through `IAuthorizationGate`** (§4.7).

### 9.2 REST endpoints
`GET /signals` · `GET /devices` · `GET /devices/{id}` · `GET /emitters[/{id}][/signals]` ·
`GET /emitters/{id}/fingerprint` · `GET /alerts` + `POST /alerts/{id}/ack` ·
`GET /map/heatmap` · `GET /spectrum/occupancy` · `GET /spectrum/coverage` (per-band last-seen, §4.4) ·
`GET /summary` · `GET /replay?from=&to=` · `POST /analyst/query` (answer + cited refs + mode) ·
`GET /export?entity=&format=` (GeoJSON/KML/CSV/JSON, §7.7) · `POST /sync/push` `GET /sync/pull` (§7.8) ·
`GET /sessions` · `GET /sessions/{id}/enhancement-candidates` (Unknown/low-conf counts to inform run/skip) ·
`POST /analysis/runs` (optional Claude enhancement over a session/range) ·
`GET /analysis/runs/{id}` (status + report) · `GET /enrichments?run=&target=` ·
`POST /enrichments/{id}/accept` `POST /enrichments/{id}/reject` ·
`GET /health` `GET /ready`.

### 9.3 Real-time (`/hub/live`) — `signal.created`, `device.determined`, `emitter.updated`, `alert.raised`, `spectrum.frame`, `coverage.updated`. Payloads match REST schemas.
### 9.4 Errors/health — problem-details (`type/title/detail/correlationId`); 5xx leak nothing; taxonomy contract-tested.
### 9.5 Contract tests — per endpoint: request-validation + success-golden + error-shape; **route-is-gated** test; consumer-driven frontend↔API contracts.

---

## 10. Phased Roadmap (G11) — walking skeleton → full platform
| M | Demoable outcome | Components |
|---|---|---|
| **M0** | `.iq`→Collector→store→`GET /signals`→React list; CI green; **receive-only + route-gated + encrypted-volume** tests pass | Collector(file), DB(enc), min API/UI(auth seam), CI |
| **M1** | PSD+features; occupancy + Spectrum waterfall; coverage indicator | §8.2, §4.4 |
| **M2** | Protocol + confidence + evidence; Protocol layer | §8.3 |
| **M3** | **All-protocol decode → determined devices** w/ vendor; Device layer | §8.4 |
| **M4** | Emitters via decoded IDs (+RF fallback); Emitter detail/history | §8.5 |
| **M5** | RF Map: heatmap + honest uncertainty | §8.6 |
| **M6** | Behavior profiles; Timeline replay | §8.7 |
| **M7** | Alerts (incl. new_device) + `/summary`; **7 MVP criteria E2E** | §8.8, §9 |
| **M8** | Real HackRF/GPS; retention; **sync; backup/export; offline-resilience; NFR perf/soak/encryption**; observability; degrade policy | Collector(hw), ops, §4.3/§4.6/§4.8/§7.7/§7.8 |
| **M9** | ML classification ≥95% w/ attribution + drift; CPU-edge inference | §8.9 |
| **M10** | Hardware re-ID + spoof detection (reference-gated) | §8.10 |
| **M11** | Activity forecasts + predicted-anomaly | §8.11 |
| **M12** | **Grounded NL analyst** — offline pattern/ML intent + templated answers; Claude uplift online | §8.12 |
| **M13** | **Optional Claude enhancement** — operator *chooses* to enhance a session (or skips if ML was good enough): reclassify, determine devices, narrate emitters, refine map, session report (advisory, cited overlay; accept/reject). Platform fully works without it (AC-DA0). | §8.13 |

---

## 11. Per-Milestone Red-Green Backlog (the ticket list)
`[ ]` = not started; pull from the top. (Component test lists in §8 are the per-item detail.)

**M0:** [ ] `FileSampleSource` deterministic replay · [ ] Collector emits Observation/dwell · [ ] persist/read-back on encrypted ephemeral Timescale · [ ] `GET /signals` golden shape · [ ] React list (Playwright) · [ ] transmit-never-called spy · [ ] every route through auth gate (contract) · [ ] cold-copy reveals no plaintext (NFR-S1) · [ ] CI blocks on red.
**M1:** [ ] FFT golden · [ ] occupancy boundaries · [ ] median noise floor · [ ] BW ±10% · [ ] feature schema contract · [ ] occupancy+coverage endpoints + waterfall (E2E) · [ ] NFR-T2 (nightly) · [ ] scheduler revisit ≤ budget (NFR-T8).
**M2:** [ ] LoRa rule+evidence · [ ] confidence∈[0,1] · [ ] evidence non-empty · [ ] ambiguous→Unknown · [ ] IClassifier stub green · [ ] golden ≥85% (nightly) · [ ] Protocol layer evidence (E2E).
**M3:** [ ] ADS-B · [ ] Wi-Fi beacon · [ ] BLE adv · [ ] LoRa hdr · [ ] Zigbee hdr · [ ] FM RDS goldens · [ ] OUI lookup · [ ] MAC-random flag · [ ] CRC-fail→no device · [ ] registry no-op · [ ] L3 zero-content · [ ] resolver type+vendor · [ ] Device layer (E2E).
**M4:** [ ] BSSID→same device · [ ] diff ICAO→distinct · [ ] no-ID scoring match · [ ] tie-break · [ ] last_seen/count+evidence · [ ] false-new ≤NFR-A4 (nightly) · [ ] Emitter detail/history (E2E).
**M5:** [ ] one-obs estimate · [ ] cluster centroid · [ ] power-weight · [ ] null when unpositioned · [ ] heatmap aggregation · [ ] RF Map uncertainty (E2E).
**M6:** [ ] periodic/always_on/burst/mobile · [ ] `/replay` + Timeline scrub (E2E) · [ ] retention/downsampling (NFR-C1).
**M7:** [ ] new emitter dedup; new_device · [ ] protocol/power/location/occupancy detectors+evidence · [ ] `/summary` counts · [ ] **7 MVP criteria E2E** incl. "what changed today?".
**M8:** [ ] real source passes same Collector suite (Liskov) · [ ] USB disconnect fault-injection (NFR-R1) · [ ] **offline→reconnect sync, zero loss (NFR-R4)** · [ ] backup/restore round-trip · [ ] export GeoJSON/KML/CSV golden · [ ] encryption-at-rest (NFR-S1) + audit-log (NFR-S2) · [ ] degrade policy sheds bands under load · [ ] NFR perf/soak/storage suite · [ ] metrics + correlationId tracing.
**M9:** [ ] reproducible training · [ ] ≥95%/F1≥0.90 (nightly) · [ ] attribution evidence · [ ] rules↔ML hot-swap green · [ ] model version per signal · [ ] drift monitor · [ ] CPU-edge inference budget.
**M10:** [ ] same-emitter high sim · [ ] distinct low sim · [ ] rotated-MAC→one fingerprint · [ ] mismatch→spoof alert · [ ] re-ID NFR-A5 at ref_quality (nightly).
**M11:** [ ] next-event tolerance · [ ] forecast uncertainty · [ ] missed transmit→predicted_anomaly · [ ] forecast-vs-actual (nightly).
**M12:** [ ] intent classifier maps known queries (no LLM) · [ ] retrieval correctness · [ ] template-answer citation contract · [ ] empty→"none found" · [ ] unsupported→capability fallback · [ ] offline/cloud auto-select · [ ] cloud grounded-Q&A eval (nightly) · [ ] NFR-T7 both modes.
**M13:** [ ] tool layer assembles session intelligence (no Claude) · [ ] egress excludes IQ+cleartext (assert) · [ ] empty-citation enrichment rejected (DB) · [ ] enrichment never mutates target (overlay) · [ ] accept/reject lifecycle · [ ] offline→queued, runs on reconnect · [ ] reclassification grounded in cited record · [ ] session report cites sources (nightly).

---

## 12. TDD Infrastructure & Fixtures (G17, G23)

### 12.1 Layout
```
/src /Collector /Processing /Classification /Decode /Correlation /Geospatial /Behavior
     /Anomaly /Fingerprint /Prediction /Analyst /Api /Sync /Security /Contracts
/web   /ml(training+eval, edge-quantized artifacts)
/tests /unit /contract /integration /golden /e2e /nfr
/fixtures /iq(.iq + .meta.json) /golden(expected outputs) /maps(offline MBTiles, ADR-5)
```

### 12.2 Labeled-data strategy (G23) — three sources, with **decode-as-label-factory**
1. **Synthetic generators** (primary for unit/golden): known tones/chirps/bursts + craftable
   valid protocol frames (ADS-B/Wi-Fi/BLE with known fields) → exact analytic ground truth.
2. **Decode-self-labeling** (primary for ML training, M9): the Decode engine's CRC-validated
   output *is* the label — a decoded ICAO/BSSID/MAC labels the captured signal automatically.
   This turns ordinary field operation into a growing, accurately-labeled training set with no
   manual annotation, and is why M3 (decode) precedes M9 (ML).
3. **Public datasets** where licensing permits, for breadth/validation.
**Accuracy phasing:** NFR-A1 (85%) is achievable from synthetic+early field data; NFR-A2 (95%)
is gated on decode-self-labeled volume — documented as a data-maturity dependency, not assumed day one.
Each fixture has `.meta.json`: `{sampleRate, centerFreq, label, expectedIdentifiers, source, license}`.

### 12.3 Golden-file harness — feed fixture `.iq` through DSP/classifier/decoder, compare to checked-in expected within tolerance; updates are explicit + reviewed (`--update-golden`), never automatic. Determinism (P5) keeps goldens stable.
### 12.4 CI gates (merge blockers) — all unit/contract/integration/golden green; ≥85% coverage on `/Processing /Classification /Decode /Correlation`; receive-only test; route-gated test; encryption-at-rest test; frontend↔API contracts. Nightly: NFR perf/accuracy/fingerprint/drift/soak/offline-resilience.
### 12.5 ML/AI testing rules — models tested via frozen datasets + eval gates; deterministic layers around models (features, retrieval, tool-calls, evidence rendering) unit-tested and green without the model; analyst grounding contract-tested (no cited record ⇒ failure).
### 12.6 "RED for the right reason" — new test fails on an assertion, not a missing-scaffold compile error.

---

## 13. (removed) Open Questions
All prior open questions are now resolved — see **§16 ADR log**. No open decisions remain that
block implementation. Residual items are *field assumptions to validate during M8*, not blockers
(§16 notes).

---

## 14. Definition of Done (every milestone)
1. All Red-Green-Refactor items checked off; tests committed before/with code.
2. CI green incl. new tests; coverage gate met.
3. Demoable outcome (§10) runs end-to-end from a clean checkout (offline for the edge).
4. No classification/decode/correlation/alert/answer lacks evidence/citation (P3/P6 contract green).
5. Relevant NFRs measured + within threshold.
6. Spec updated if behavior diverged; no test and doc disagree silently.

> **M0–M12 DoD never depends on Claude/M13.** M13 is purely additive enhancement; the platform is
> complete, mapped, and shippable with the enhancement engine disabled (AC-DA0). Running Claude is the
> operator's choice, not a build requirement.

---

## 15. Packaging & Operations (G24)
- **Edge bundle:** Docker Compose stacking API+services+Postgres/Timescale+Redis+web+offline-analyst (ML/pattern, no LLM), on an encrypted volume; one-command offline install; documented resource budget (§4.8).
- **Updates:** versioned images; offline update via image bundle; DB migrations via EF Core, forward-only, tested on a copy before apply.
- **Backups:** scheduled identity-store backups (§7.7); restore drill in M8.
- **Offline maps:** bundled MBTiles served locally to MapLibre (ADR-5) — no internet tile dependency in the field.

---

## 16. ADR Log — resolved forking decisions (replaces Open Questions)
| ADR | Decision | Rationale |
|---|---|---|
| **ADR-1** | **Hybrid edge-first deployment**; offline-first edge node; opportunistic sync; analyst offline via **pattern/ML intent + templated grounded answers (no local LLM)**, Claude API uplift when online | Matches "walk/drive with a HackRF" + the platform vision; never depends on connectivity in the field; keeps the edge light (no LLM) while giving best phrasing/reasoning when online |
| **ADR-2** | **Single-operator, auth-ready** (`IAuthorizationGate` pass-through default + encrypted-at-rest) | No login friction now; multi-user/RBAC later without controller rework |
| **ADR-3** | **All protocols decoded in parallel** (Wi-Fi/BLE/LoRa/Zigbee/ADS-B/FM-RDS) | Maximum device coverage; enables decode-self-labeling across the full set |
| **ADR-4** | Jurisdiction handled by configurable conservative `RegulatoryProfile`, not a hardcoded region | Operator-portable; receive-only always; decoders gated by policy |
| **ADR-5** | Offline-bundled MBTiles for maps | Edge-first requires field operation without internet tiles |
| **ADR-6** | Power recorded **relative (dBFS)** by default, optional calibration table | HackRF is uncalibrated; absolute dBm would be dishonest |
| **ADR-7** | Fingerprint features **drift-robust**, reference-gated; optional external 10 MHz CLKIN / GPS-disciplined ref | HackRF's **stock ~20 ppm crystal** (not a TCXO) drift bounds CFO precision; accuracy ceiling stated, not over-promised |
| **ADR-8** | CPU-first; GPU optional for M9/M10 *training*; **edge inference CPU-only quantized; no generative LLM on the edge** (offline analyst uses ML/pattern matching) | Field laptop may lack a GPU; keeps the edge node light and self-sufficient |
| **ADR-9** | Multi-sensor fusion + multi-user RBAC seamed-in but **deferred** beyond M13 | Keeps MVP→platform path clear without building unrequested scale |
| **ADR-10** | **Claude is an optional data-enhancement processor, not the analyzer:** the edge ML produces the complete authoritative result; the operator *chooses* to run a deferred Claude enhancement pass after a session — or skips it if ML was good enough. Advisory, cited overlay (never overwrites edge results); only structured metadata egresses; never on the critical path (AC-DA0) | Lets you walk/drive and collect with a self-sufficient device, then optionally spend Claude only on the residual hard cases — without ever making the product depend on connectivity or an API |

> **Field assumptions to validate during M8 (non-blocking):** real-world scan revisit budgets
> vs detection needs (§4.4); calibration-table accuracy (§4.5); achievable fingerprint
> `ref_quality` ceiling (§4.5/§8.10); offline-analyst intent coverage vs the real operator query set (§8.12).

---

## 17. Traceability
### 17.1 MVP criteria → milestone → test
1 Walk/drive→M8 (M0 file) · 2 Collect→M0 · 3 Map→M5 · 4 Protocols→M2 · 5 Emitter history→M4 · 6 New activity→M7 · 7 "What changed?"→M7 (text) / M12 (conversational).
### 17.2 Vision layers → spec
L1 Spectrum→§8.2 · L2 Protocol→§8.3/§8.9 · **L3 Device→§8.4 + `devices`** · L4 Emitter→§8.5 · L5 Activity→§8.8 · L6 Temporal→§7.6+`/replay` · L7 Behavior→§8.7/§8.11 · L8 Geographic→§8.6 · L9 Semantic→§8.12+`/summary`.
### 17.3 AI phases → milestone
P1 rules→M2 · P2 ML→M9 · P3 fingerprinting→M10 · P4 prediction→M11 · P5 NL analyst→M12 · Deferred Claude analysis→M13 (extends P5).

---

## 18. Claim Verification Log

Every checkable technical assumption in this spec was verified against authoritative sources
(datasheets, IEEE/standards-derived docs, peer-reviewed papers, statutes/case law) on **2026-06-24**.
Verdicts: **✅ Confirmed** · **✏️ Corrected** (spec edited) · **⚠️ Confirmed-with-caveat** (honesty
added). This log is the audit trail; the inline spec already reflects every correction.

### 18.1 Hardware (§4.1, §4.5)
| Claim | Verdict | Source |
|---|---|---|
| HackRF One tunes 1 MHz–6 GHz | ✅ | GSG HackRF docs (hackrf.readthedocs.io/hackrf_one) |
| ≤20 MS/s, 8-bit I/Q, half-duplex | ✅ | GSG docs (same) |
| ~20 MHz instantaneous bandwidth → sweeps must step | ✅ (ADC-bounded; MAX2837 analog filter reaches 28 MHz) | GSG sampling_rate docs |
| Power is dBFS-relative, not calibrated dBm | ✅ | GSG setting_gain docs; RFAnalyzer calibration notes |
| **Stock reference is a TCXO** | ✏️ **Corrected → plain crystal, ~20 ppm**; external 10 MHz CLKIN / aftermarket TCXO / 1PPS available | GSG external_clock_interface docs; ab9il.net |
| u-blox GPS: 1 Hz NMEA 0183, UTC + lat/lon, 1PPS | ✅ | u-blox 8/M8 protocol spec; u-blox timing app note |
| SoapySDR = HAL enabling multi-SDR portability | ✅ | SoapySDR / SoapyHackRF wikis |

### 18.2 Protocol decode feasibility (§8.4)
| Claim | Verdict | Source |
|---|---|---|
| ADS-B 1090 MHz, ~2 MHz, ICAO+callsign, public/unencrypted, dump1090-decodable | ✅ | RTCA DO-260B; the-1090mhz-riddle |
| FM/RDS — 57 kHz subcarrier, 1187.5 bps, PI/PS station ID | ✅ | RDS standard; rds.org.uk |
| LoRa CSS 125/250/500 kHz; DevAddr in header; LoRaWAN payload AES-128 | ✅ | TheThingsNetwork LoRaWAN docs |
| **Wi-Fi beacons decodable on HackRF** | ⚠️ **legacy 20 MHz only** (gr-ieee802-11); 40/80 MHz + HT/VHT/HE out of scope | gr-ieee802-11 issue #248 |
| **BLE advertising decodable in parallel** | ⚠️ **single-channel best-effort** — 3 adv channels span >20 MHz, hopping hard; Ubertooth/nRF recommended | lexfo BLE-SDR; JiaoXianjun/BTLE |
| **Zigbee channels "2 MHz wide"** | ✏️ **~2 MHz occupied, spaced 5 MHz**; MAC hdr cleartext | Digi 802.15.4 whitepaper |
| MAC randomization is real/widespread | ✅ (caveat: *reduces, not eliminates* — side channels remain) | Android wifi-mac-randomization docs |
| OUI → vendor via IEEE registry | ✅ (caveat: meaningless for locally-administered/randomized MACs → §8.4 LAA check) | IEEE Registration Authority; oui.is |

### 18.3 DSP / fingerprinting / geolocation (§8.2, §8.6, §8.10)
| Claim | Verdict | Source |
|---|---|---|
| FFT + Hann + 50% overlap is standard (Welch) | ✅ (4096 is a chosen resolution/latency tradeoff) | SciPy `welch` docs |
| Median noise-floor estimate is robust to bursts | ✅ | LIGO median-noise-floor-tracker |
| RF/PHY fingerprinting (CFO, I/Q imbalance, transient) IDs hardware past spoofed MACs | ⚠️ Confirmed — **but reported 89–99% are lab/controlled; cross-day/cross-RX/low-SNR is the weak point** → NFR-A5 stays reference-gated + field-validated | MDPI Sensors 25/3/648; QUB TIFS RFFI |
| Receiver clock stability matters for CFO features | ✅ (reinforces the crystal correction) | MDPI Sensors 24/17/5670 |
| Single moving RX can't triangulate; centroid + uncertainty is the honest output | ✅ (multilateration needs ≥3 synced sensors / TDOA) | MDPI Electronics 11/2/289 |
| Inter-arrival behavior classification is standard | ✅ | ScienceDirect S2590005621000291 |
| Spectrogram-CNN classification + SHAP/saliency attribution | ✅ (**accuracy is SNR-dependent**; NFR-A2 95% is dataset/SNR-bounded) | MDPI Sensors 23/23/9467; Interpretable-ML-Book |

### 18.4 Infrastructure (§7, §4.6)
| Claim | Verdict | Source |
|---|---|---|
| TimescaleDB hypertables partition by time | ✅ | Timescale hypertables docs |
| Continuous aggregates auto-maintain rollups | ✅ | Timescale continuous-aggregates docs |
| Retention policies drop old chunks on schedule | ✅ (**caveat: retention does NOT cascade to continuous aggregates — set separately**, §7.6) | Timescale data-retention docs |
| **Postgres TDE / pgcrypto at rest** | ✏️ **Corrected — no native TDE in upstream Postgres**; volume encryption is baseline + pgcrypto column-level; TDE only via forks (Percona `pg_tde`, EDB, Cybertec) | PostgreSQL wiki TDE; Percona pg_tde |

### 18.5 Legal / regulatory (§4.2) — US; software constraints, not legal advice
| Claim | Verdict | Source |
|---|---|---|
| Intercepting *contents* of comms can be unlawful (Wiretap Act) | ✅ | 18 USC 2511 (Cornell LII) |
| 47 USC 605 restricts unauthorized reception + divulgence | ✅ | 47 USC 605 (Cornell LII); DOJ CRM 1066 |
| Carve-out for radio "readily accessible to the general public" | ✅ (2511(2)(g); scrambled/encrypted still penalized) | 18 USC 2511(2)(g) |
| Decoding public identifiers/beacons ≠ intercepting private payload | ⚠️ **Defensible but bounded** — *Joffe v. Google* (9th Cir. 2013): unencrypted Wi-Fi **payload** is NOT "readily accessible," so "unencrypted ≠ lawful to capture content." Validates L3. Circuit/state-law dependent | Joffe v. Google 9th Cir.; EFF analysis |
| ADS-B is explicitly public/unencrypted | ✅ | FAA/Honeywell ADS-B materials |

### 18.6 Claude models (§8.9 / §8.12 / §8.13) — verified against the authoritative Claude API reference
| Claim | Verdict |
|---|---|
| `claude-sonnet-5` is a real, current model — analyst/enhancement default (balanced tier) | ✅ |
| `claude-opus-5` is a real, current model — hard-query/analysis tier | ✅ |
| Model IDs take **no date suffix** | ✅ |

> **v3.5 update (live client):** the M13 live `IClaudeClient` is now built (`AnthropicClaudeClient`, official
> `Anthropic` C# SDK). The analyst/enhancement seam is **async** (`CompleteAsync`); the default models moved
> to the current **Claude 5** family (Sonnet 5 balanced default, Opus 5 hard) — the earlier `claude-sonnet-4-6`
> / `claude-opus-4-8` remain valid but are superseded. The live client is registered only when
> `Analyst:CloudEnabled=true` + a key is present; offline/tests keep `StubClaudeClient`, so AC-DA0 holds.

### 18.7 Residual unknowns (validate in the field, not on paper — already flagged §16)
- Achievable RF-fingerprint `ref_quality` ceiling on this rig (lab numbers ≠ field). 
- Real BLE coverage on HackRF alone vs. with an Ubertooth companion.
- Calibration-table accuracy for relative→dBm conversion.
- Scan revisit budgets vs. real detection needs.

---

## 19. Implementation Status (as-built)

§1–§18 are the **contract** (the target). This section is the **reality check**: what is actually
built on `main`, what is a deliberate seam, and what is next. It is descriptive, not aspirational —
when it disagrees with the roadmap checkboxes in §10/§11, this section is the current truth.
Legend: **✅ built** · **◑ partial** (seam present; hardware/live-only/cloud piece deferred) · **○ not started**.

### 19.1 Components (§8) — as-built
| Component | State | As-built notes |
|---|---|---|
| §8.1 Collector | ◑ | Scan/dwell + `Observation` emission built; **browser WebUSB HackRF** live source (`/ingest/iq`) built; native SoapySDR/HackRF source deferred (needs device). |
| §8.2 Processing (DSP) | ✅ | FFT/occupancy/features; deterministic. |
| §8.3 Classification (rules) | ✅ | Weighted-rule scorer behind `IClassifier`. |
| §8.4 Decode & Device ID | ◑ | Decoders parse **frame bytes**; per-protocol IQ→bits demod deferred **except ADS-B** (live `AdsBDemodulator`) and **NOAA APT** (`AptDecoder`, in-memory image, invariant-#3 carve-out). Georeference (Phase 2) built: SGP4 + `AptGeoReferencer` → `/devices/{id}/geo`. |
| §8.5 Correlation | ✅ | Decoded-ID-primary + RF fallback. Emitter temporal/BW scoring dormant (no persisted emitter state — see CLAUDE.md landmine #8). |
| §8.6 Geospatial | ✅ | Centroid + honest uncertainty; RF Map renders it. `/map/heatmap` endpoint not yet exposed. |
| §8.7 Behavior | ✅ | Descriptive profiles. |
| §8.8 Anomaly | ✅ | Rule detectors incl. new_device. |
| §8.9 ML classification (M9) | ◑ | Pure-C# softmax (`SignalAtlas.Ml`) behind `IClassifier`; ~0.97 on **synthetic** data. ONNX/GBM/CNN is the documented production upgrade for NFR-A2 on real signals. |
| §8.10 Fingerprinting (M10) | ◑ | `SignalAtlas.Fingerprint` seam; reference-gated accuracy deferred (needs stable ref + field IQ). |
| §8.11 Prediction (M11) | ◑ | Forecast seam reserved on the behavior engine. |
| §8.12 NL Analyst (M12) | ✅ | `OfflineAnalyst` (intent + templated cited answers, no LLM) + `POST /analyst/query` + **Analyst web page**. `CloudAnalyst` uses the **live `AnthropicClaudeClient`** when `Analyst:CloudEnabled=true` + a key is present (Sonnet 5 default), else `StubClaudeClient`; degrades to offline if the live call fails. |
| §8.13 Claude enhancement (M13) | ◑ | Backend built: `/sessions`, `/sessions/{id}/enhancement-candidates`, `/analysis/runs`, `/enrichments` + accept/reject. **Live `AnthropicClaudeClient` wired** (async, per-run model); still **no enrichment UI**, and a live pass needs a key + `Analyst:CloudEnabled=true`. Platform complete with it disabled (AC-DA0 holds). |
| §8.14 RF Audio Player | ✅ | Server-side WBFM/NBFM/AM/USB/LSB/CW demod (true phasing SSB) over `/audio` WebSocket; Live Spectrum page. |

### 19.2 API endpoints (§9.2) — as-built
- **Built & gated (`/api/v1`):** `/signals` · `/devices` · `/devices/{id}/image` · `/devices/{id}/geo` ·
  `/emitters` · `/emitters/{id}` · `/alerts` · `/summary` · `/spectrum/frames` · `/spectrum/occupancy` ·
  `/spectrum/coverage` · `/analyst/query` · `/sessions` · `/sessions/{id}/enhancement-candidates` ·
  `/analysis/runs` (+ `/{id}`) · `/enrichments` (+ accept/reject).
- **Built & intentionally ungated:** `/health` · `/ready` · `/metrics` · `/hub/live` · `/ingest/iq` · `/audio`.
- **Not yet exposed (SPEC §9.2 gaps):** `/emitters/{id}/fingerprint` · `/map/heatmap` · `/replay` ·
  `/export` (GeoJSON/KML/CSV/JSON) · `/sync/push` · `/sync/pull` · `POST /alerts/{id}/ack`.

### 19.3 Persistence & platform posture
- **Persistence is Docker-optional:** EF Core + Postgres/Timescale when `ConnectionStrings:SignalAtlas`
  is set, else **in-memory** repos. Demo/seed rows are gated behind `SeedDemoData` (default **off** →
  honest empty states). Encryption-at-rest, hypertable behavior, and DB round-trips are verified only
  in the **Docker CI lane** (`Category=NeedsDocker`).
- **Prime invariants all enforced & tested:** receive-only (L1), explainable-only (P4/P6),
  metadata-not-content (L2/L3, with the documented NOAA-APT in-memory carve-out), deterministic core
  (P5), every `/api/v1` route auth-gated. See CLAUDE.md for the operative invariant list and landmines.

### 19.4 Next to add (prioritized)
1. **Enrichment accept/reject UI** — surface the §8.13 lifecycle in the web app (the live client is now wired; this makes the overlay usable). ~~Live `IClaudeClient` HTTP impl~~ — **done** (`AnthropicClaudeClient`).
3. **Map/export/sync/ack endpoints** — the §9.2 gaps in §19.2.
4. **Native SoapySDR/HackRF source + per-protocol demodulators** — needs the device + field `.iq` captures.
5. **Docker-lane CI** — Postgres/Timescale, encryption-at-rest, retention (needs a Docker host).
6. **ML production upgrade** — ONNX/GBM/CNN behind `IClassifier` toward NFR-A2 on real signals.

---

*End of specification. Every vision gap is resolved (§3, §16) and every checkable assumption is
verified (§18); §19 tracks what is built vs. next. Entry point for new work: §19.4, then the relevant
component spec — write the RED test, watch it fail, make it GREEN, refactor, repeat.*
