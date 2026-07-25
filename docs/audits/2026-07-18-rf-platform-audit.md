# Signal Atlas — RF Receiver & Identification Platform Audit

**Date:** 2026-07-18
**Scope:** Whole platform — HackRF One capability utilization, DSP/decode correctness, backend
architecture & performance, code health, one reported UI bug. Read-only audit; nothing was changed.
**Method:** Direct read of the RF/collector core + three parallel focused auditors (decode/DSP,
backend architecture, HackRF capability). Every headline finding below was re-verified against the
source before inclusion.

Findings are ordered **most critical to least**. Each carries a severity, exact location, the
defect, the real-world impact, and a concrete fix direction. A remediation roadmap for the explicit
ask — "use the HackRF One to its fullest" — is at the end.

---

## Remediation status (updated 2026-07-25)

The findings below were verified against the current code; **most are now fixed**. Summary:

- **Fixed (22):** #1, #2, #3, #4, #5, #7, #8, #9, #10, #11, #12, #13, #15, #16, #17, #18, #19, #22, #23, #25, #26 — including both P0 device-identity collapses (#1/#2), the HackRF capability contract (#3/#4/#5, gain-stage struct + bias-tee + baseband BW + config binding + validation), determinism (#7), and **DB scalability (#8 — closed 2026-07-25:** metrics gauges now use server-side `Count()`; `/spectrum/coverage`, the analyst reads, and `/devices/{id}/geo` are bounded/keyed via `GetSignals(cap)` / `GetDevices(cap)` / new `IDeviceRepository.Get(id)` + `IAlertRepository.GetSince`).
- **Partial (1):** #20 (CPR `Evict` still runs an O(n) stale-sweep under the lock at cap — minor; the per-victim `Aggregate` was already removed).
- **Deferred by design (1):** #6 (`SoapyHackRfDevice.IsAvailable => false` — the native HackRF path needs the physical device + SoapySDR P/Invoke; the browser WebUSB path is the working RX today).
- **Decision item:** #14 (`GeolocationEngine` + the `SignalAtlas.Behavior` project are built but not DI-wired — activate in the pipeline, or mark as staged seams in SPEC).

The per-finding sections below are the **original 2026-07-18 audit text**, kept as the point-in-time record.

---

## P0 — Critical: the platform's core function (device identification) is silently collapsing

### 1. Zigbee: every node on a PAN collapses into a single Device
- **Where:** [DeviceResolver.cs:77](src/SignalAtlas.Decode/DeviceResolver.cs#L77) + [IngestionPipeline.cs:240](src/SignalAtlas.Pipeline/IngestionPipeline.cs#L240) vs [ZigbeeMacDecoder.cs:90-97](src/SignalAtlas.Decode/Decoders/ZigbeeMacDecoder.cs#L90-L97)
- **Defect:** the decoder emits the node identity under key **`src_addr`**, but the resolver's Zigbee
  primary keys are `{ext_addr, short_addr, pan_id}` and the pipeline's `PrimaryKeyOrder` never lists
  `src_addr`. Both layers fall through to **`pan_id`**.
- **Impact:** `DeterministicGuid` is derived from the PAN, so **every distinct Zigbee node on one
  network merges into one Device**, with the last frame's fields winning. The resolver's own unit
  test masks this by feeding a hand-made `mac` key that the real decoder never produces.
- **Fix:** add `src_addr` as the leading Zigbee primary key in both `Classify` and `PrimaryKeyOrder`
  (ahead of `pan_id`). Add a pipeline test that runs a *real* `ZigbeeMacDecoder` output through
  grouping and asserts two nodes on one PAN → two devices.

### 2. BLE: every advertiser in a block collapses into a single Device
- **Where:** [IngestionPipeline.cs:240](src/SignalAtlas.Pipeline/IngestionPipeline.cs#L240) + [DeviceResolver.cs:72-73](src/SignalAtlas.Decode/DeviceResolver.cs#L72-L73) vs [BleAdvDecoder.cs:64-70](src/SignalAtlas.Decode/Decoders/BleAdvDecoder.cs#L64-L70)
- **Defect:** the decoder emits the advertiser address under key **`adva`**, but `PrimaryKeyOrder`
  and BLE's `Classify` primary keys `{mac}` never include `adva`. Grouping falls through to
  `proto:BLE`.
- **Impact:** **all BLE frames in a block group into one bucket** → a single merged device with a
  null primary id and no vendor (device-level vendor/LAA lookup also keys on `{bssid, mac}`, missing
  `adva` — finding #19). Per-advertiser identity is lost.
- **Fix:** add `adva` to `PrimaryKeyOrder` and to BLE's `Classify` primary keys (and to the
  vendor/LAA `FirstPresent` set at [DeviceResolver.cs:48](src/SignalAtlas.Decode/DeviceResolver.cs#L48)).

> These two are the highest priority because device identification is the product. The RF/classify
> stages work; the identity keys are mismatched at the seam between decoders and the resolver.

---

## P1 — High

### 3. HackRF's three independent RX gain stages are collapsed into one scalar (the core capability gap)
- **Where:** [IHackRfDevice.cs:15](src/SignalAtlas.Collector/IHackRfDevice.cs#L15) (`OpenReceive(centerFreqHz, sampleRateHz, double gainDb)`), inherited by [HackRfSampleSource.cs:35](src/SignalAtlas.Collector/HackRfSampleSource.cs#L35) and [ReconnectingHackRfSampleSource.cs:147](src/SignalAtlas.Collector/ReconnectingHackRfSampleSource.cs#L147).
- **Defect:** the HackRF One has **three** separable RX gain stages — RF amp (0/14 dB switchable),
  LNA/IF (0–40 dB / 8 dB steps), VGA/baseband (0–62 dB / 2 dB steps). The interface reduces ~102 dB
  of independently tunable range to a single `double`, and a dB scalar can't even express the amp's
  binary enable.
- **Impact:** the backend receive path can never set an optimal gain distribution (e.g. low LNA +
  high VGA to avoid front-end overload near strong emitters). This is the central "not using the
  HackRF to its fullest" defect.
- **Fix:** replace `gainDb` with a `RxGain { bool AmpEnable; int LnaDb; int VgaDb; }` (see roadmap).

### 4. Bias-tee and baseband-filter bandwidth are absent from the entire platform
- **Where:** [IHackRfDevice.cs](src/SignalAtlas.Collector/IHackRfDevice.cs) (no params) and the WebUSB driver [web/src/sdr/hackrf.ts](web/src/sdr/hackrf.ts) (bias-tee request 23 never defined).
- **Defect:** no antenna-port bias-tee (+3.3 V) control anywhere → cannot power a mast-mounted RX
  LNA (forfeits real sensitivity/range). Baseband anti-alias filter bandwidth (1.75–28 MHz,
  selectable) has no operator control on either path; on the WebUSB path it's auto-derived from
  sample rate ([SdrProvider.tsx:110](web/src/sdr/SdrProvider.tsx#L110)), so the operator can't narrow
  the IF to reject adjacent-channel interferers.
- **Fix:** add `biasTee` + `basebandBwHz` to the device contract and both drivers; surface as
  operator controls.

### 5. Live C# HackRF source ignores operator config — frozen at 915 MHz / 2 MS/s / gain 32
- **Where:** [CollectorServiceCollectionExtensions.cs:16-38](src/SignalAtlas.Collector/CollectorServiceCollectionExtensions.cs#L16-L38) + [PipelineHostedService.cs:86-107](src/SignalAtlas.Api/PipelineHostedService.cs#L86-L107).
- **Defect:** `AddHackRfCollectorIfAvailable` builds the source from **hardcoded constants** and
  reads nothing from configuration; `ResolveSource` returns that hardware source **before** any
  `Ingestion:CenterFreqHz`/`SampleRateHz` is consulted. Gain has **no config key at all** anywhere in
  live mode.
- **Impact:** the moment the real Soapy device lands, a connected HackRF is permanently pinned to
  915 MHz regardless of what the operator sets — a receiver tuned to the wrong band receives nothing.
  (Latent today only because `SoapyHackRfDevice.IsAvailable` is a hardwired `false` stub — finding
  #6 — so the working path is currently the browser WebUSB driver, which *does* honor its config.)
- **Fix:** bind `Ingestion:*` (freq, rate, gain stages, bias-tee, bandwidth, block size) into the
  hardware source construction; validate before `OpenReceive`.

### 6. The only backend hardware path is a permanent stub
- **Where:** [SoapyHackRfDevice.cs:18](src/SignalAtlas.Collector/SoapyHackRfDevice.cs#L18) (`IsAvailable => false`, all methods throw).
- **Impact:** the entire C# `IHackRfDevice` receive path is dead in production; the sole functioning
  RX path is the browser WebUSB driver. This is a known deferred seam (needs SoapySDR P/Invoke +
  hardware), noted here so the capability gaps above are fixed in the interface *before* someone
  implements against it.

### 7. Determinism violation: physical-layer fingerprint stamped with wall-clock → non-deterministic spoof-alert IDs
- **Where:** [PhyFingerprinter.cs:44](src/SignalAtlas.Fingerprint/PhyFingerprinter.cs#L44) & :127 (`DateTimeOffset.UtcNow`), consumed by [SpoofDetector.cs:43](src/SignalAtlas.Fingerprint/SpoofDetector.cs#L43).
- **Defect:** `UpdatedAt` uses `UtcNow` inside the deterministic core (violates invariant P5); the
  spoof-alert GUID is seeded from `UpdatedAt:o`.
- **Impact:** identical IQ produces different fingerprint timestamps and **different spoof-alert
  GUIDs run-to-run** — breaks reproducibility and dedupe. Inject `IClock` (the pattern already used
  throughout the pipeline).

### 8. Full-table scans on every read (DB mode will OOM / time out at scale)
- **Where:** [EfRepositories.cs:20,26,81,145](src/SignalAtlas.Persistence/EfRepositories.cs#L20) — `db.Signals.AsEnumerable().OrderByDescending(...).Take(n)` materializes and client-sorts the *whole* table; plus `GetSignals(int.MaxValue)` / `GetDevices(int.MaxValue)` callers: [Program.cs:249 `/summary`](src/SignalAtlas.Api/Program.cs#L249), [Program.cs:450 `/spectrum/coverage`](src/SignalAtlas.Api/Program.cs#L450), [AnalystRetrieval.cs:51](src/SignalAtlas.Analyst/AnalystRetrieval.cs#L51), [DeferredClaudeAnalyzer.cs:91](src/SignalAtlas.Enhancement/DeferredClaudeAnalyzer.cs#L91).
- **Impact:** against a Timescale `signals` hypertable with millions of rows, a plain
  `/signals?limit=100`, `/summary`, or a coverage poll pulls every row into app memory and sorts
  client-side. Linear-in-retained-volume; the platform is unusable at field data volumes.
- **Fix:** push `OrderBy`/`Where`/`Take`/`Count` into `IQueryable` (server-side); replace
  `int.MaxValue` count pulls with `CountAsync`, and range-bound the analyst/enhancement/coverage
  queries by time window.

### 9. No frequency / sample-rate range validation
- **Where:** [hackrf.ts setFrequency ~L175 / setSampleRate ~L183](web/src/sdr/hackrf.ts), UI fields in [HackRfConnect.tsx:87-93](web/src/components/HackRfConnect.tsx#L87-L93), and `Ingestion:*` config.
- **Defect:** center frequency isn't checked against 1 MHz–6 GHz, sample rate isn't checked against
  2–20 MS/s. (LNA/VGA are masked/clamped; freq and rate are not.)
- **Impact:** out-of-range tunes are silently clamped/rejected by the device with no operator
  feedback; sub-2 MS/s or >20 MS/s risks USB overruns. Add guarded setters + UI validation.

---

## P2 — Medium

### 10. Thread-safety races in singleton in-memory stores
- **Where:** [InMemoryAuditLog.cs:11](src/SignalAtlas.Persistence/InMemoryAuditLog.cs#L11) mutates its `List` and `_next` with **no lock** while every sibling repo locks — and it's written on every `/devices` read from concurrent HTTP handlers. Same non-synchronized-singleton hazard in [InMemoryEnhancementRepositories.cs:8,21,38](src/SignalAtlas.Persistence/InMemoryEnhancementRepositories.cs#L8) and the [DeferredClaudeAnalyzer queue:42](src/SignalAtlas.Enhancement/DeferredClaudeAnalyzer.cs#L42).
- **Impact:** parallel requests can tear `_next`, corrupt the list, or throw `IndexOutOfRange`. The
  audit log also has **no capacity cap** (unlike the other stores) → unbounded growth over uptime.
- **Fix:** lock (or `ConcurrentQueue`/`ConcurrentDictionary`); add an eviction cap to the audit log.

### 11. Per-segment allocations on the PSD hot path
- **Where:** [SignalProcessor.cs:40-41](src/SignalAtlas.Processing/SignalProcessor.cs#L40-L41) allocates `new double[4096]` ×2 per Welch segment.
- **Impact:** a ~1 M-sample block at 50 % overlap ≈ 500 segments ≈ 32 MB of short-lived `double[]`
  per block — sustained GC pressure on a real-time streaming receiver. Hoist and refill the buffers.

### 12. FM RDS station name retains NUL padding
- **Where:** [FmRdsDecoder.cs:87](src/SignalAtlas.Decode/Decoders/FmRdsDecoder.cs#L87) — `new string(psChars).TrimEnd()` can't strip `'\0'` (TrimEnd removes only whitespace).
- **Impact:** the common partial-reception case persists `ps = "AB\0\0\0\0\0\0"` as an identifier and
  evidence. `TrimEnd('\0', ' ')`.

### 13. Receiver configuration is not recorded as provenance
- **Where:** [ScanCollector.cs:48](src/SignalAtlas.Collector/ScanCollector.cs) sets `Observation.BandwidthHz = SampleRateHz` (not the true filter passband); [IqIngressEndpoint.cs:22](src/SignalAtlas.Api/IqIngressEndpoint.cs#L22) `IqConfig` never carries gain/filter state.
- **Impact:** for an explainable-only platform, the record omits the gain/filter settings that shaped
  every sample, and mislabels RX bandwidth whenever filter ≠ sample rate. Capture gain stages +
  filter BW into observation provenance.

### 14. Dead / unwired subsystems
- **Where:** [GeolocationEngine.cs](src/SignalAtlas.Geospatial/GeolocationEngine.cs) is never DI-registered or passed to the pipeline (test-only); the entire [SignalAtlas.Behavior](src/SignalAtlas.Behavior/) project (`BehaviorEngine`, `BehaviorPredictor`, `PredictedAnomalyDetector`) has no DI registration and no runtime consumer.
- **Impact:** advertised geolocation/behavior capability is dormant. Either wire them into the
  pipeline or mark them explicitly as staged seams in SPEC to avoid "green build ≠ shipped feature".

### 15. UI: right-hand detail drawers render behind the fixed header (your reported bug)
- **Where:** [AppShell.tsx:68](src/../web/src/components/AppShell.tsx#L68) pins the `AppBar` at
  `zIndex: drawer + 1`; the right-anchored detail drawers in
  [Devices.tsx:103](web/src/views/Devices.tsx#L103), [Emitters.tsx:126](web/src/views/Emitters.tsx#L126),
  [RfMap.tsx:329](web/src/views/RfMap.tsx#L329), [Alerts.tsx:114](web/src/views/Alerts.tsx#L114) have
  **no `<Toolbar />` spacer** (the left nav drawer has one at [AppShell.tsx:102](web/src/components/AppShell.tsx#L102)).
- **Impact:** the top rows of the slide-out (Protocol / Vendor / Primary ID) tuck under the header —
  exactly the screenshot.
- **Fix:** add a `<Toolbar />` spacer at the top of each right drawer's content (consistent with the
  nav drawer), or raise those drawers' `zIndex` above the app bar.

---

## P3 — Low / robustness

- **16.** [EfRepositories.cs:48,66](src/SignalAtlas.Persistence/EfRepositories.cs#L48) — `Upsert` is
  check-then-act (`Find`→`Add`); two concurrent upserts of the same id can both `Add` → PK violation.
- **17.** [AdsBDemodulator.cs:47](src/SignalAtlas.Decode/AdsBDemodulator.cs#L47) — frame scan has no
  inter-block carry-over; a Mode S burst straddling a block boundary is silently dropped on live
  streaming.
- **18.** [DeterministicRng.cs:31](src/SignalAtlas.Ml/DeterministicRng.cs#L31) — `NextInt(k, k)`
  divides by zero; `% span` also biases large ranges.
- **19.** [MlClassifier.cs:50](src/SignalAtlas.Ml/MlClassifier.cs#L50) / DriftMonitor.cs:48 — a
  deserialized `MlModel` with a 0 std yields `Infinity`/`NaN` logits → NaN softmax → garbage argmax
  (the live trainer clamps std, but `FromJson` doesn't).
- **20.** [CprPositionResolver.cs:87](src/SignalAtlas.Decode/CprPositionResolver.cs#L87) — `Evict`
  runs an O(n) LINQ + `Aggregate` over the whole cache inside the lock on every `Accept` past the cap.
- **21.** [DeferredClaudeAnalyzer.cs:46,121](src/SignalAtlas.Enhancement/DeferredClaudeAnalyzer.cs#L46)
  — `Guid.NewGuid()` in a `src/` project (borderline P5; defensible as the optional Claude overlay).
- **22.** [DeviceResolver.cs:48](src/SignalAtlas.Decode/DeviceResolver.cs#L48) — device-level
  vendor/LAA lookup misses BLE's `adva` (rides along with fix #2).
- **23.** [Pagination.cs:61](src/SignalAtlas.Api/Pagination.cs#L61) — `EncodeCursor` is never called;
  cursor pagination is decode-only/half-wired (no endpoint returns `nextCursor`).
- **24.** [CorrelationOptions.cs:15,19](src/SignalAtlas.Correlation/CorrelationOptions.cs#L15) —
  `BandwidthWeight`/`TemporalWeight` are dormant tunables (documented; need an `Emitter`
  last-seen/bandwidth field).
- **25.** [EnvelopeAnalyzer.cs:9](src/SignalAtlas.Processing/EnvelopeAnalyzer.cs#L9) —
  `BurstDurationMs` is test-only dead code (pipeline derives duration from sample count).
- **26.** Band/channel presets ([bandPresets.ts:26](web/src/sdr/bandPresets.ts#L26)) cover only FM /
  ADS-B / 2.4 GHz / sub-GHz ISM — most of 1 MHz–6 GHz has no quick-tune.

---

## Verified correct (not everything is broken)

The auditors read these closely and found them sound: the **ADS-B chain** (PPM demod
preamble/bit-slice, CRC-24 over 88 bits, DF/TC extraction, 25-ft Q-bit altitude, and the global
even/odd CPR math incl. the NL latitude-zone check), the **FFT / Hann-window / dBFS Welch
normalization**, the **softmax trainer**, and the protocol **CRC routines** (BLE, Wi-Fi, Zigbee,
RDS). The receive-only, offline-first, and auth-gating invariants hold.

---

## Recommended remediation order

1. **Fix device-identity collapse (#1, #2)** — one-day change at the decoder↔resolver seam; restores
   the platform's core function. Add real-decoder-output pipeline tests.
2. **Correct the HackRF device contract before Soapy is implemented (#3, #4)** — expand
   `OpenReceive` so the interface an eventual impl inherits is capability-complete:
   ```csharp
   void OpenReceive(long centerFreqHz, int sampleRateHz, RxGain gain, int basebandBwHz, bool biasTee);
   public readonly record struct RxGain(bool AmpEnable, int LnaDb, int VgaDb);
   ```
3. **Plumb + validate config into the hardware source (#5, #9)** — bind `Ingestion:*` to the live
   source; validate freq (1 MHz–6 GHz), rate (2–20 MS/s), LNA (0–40/8), VGA (0–62/2) before tuning.
4. **Determinism (#7)** and **DB scalability (#8)** — inject `IClock` into the fingerprinter; move
   repo sorting/counting server-side.
5. **Thread-safety (#10)**, then the medium/low list as capacity allows. **Your drawer bug (#15)** is
   a quick, isolated fix and can go in immediately.
