# Signal Atlas — CI Failure & Correctness/Code-Quality Audit

**Date:** 2026-09-16
**Scope:** The failing CI pipeline, plus a correctness and code-quality pass over the backend
(15 `src/` projects), the test suite, the frontend, and the repo's own stated invariants — and a
**UX/accessibility pass against the running app in Chrome** (see that section; it found a functional
bug and a set of real a11y gaps that no amount of source reading surfaced).
**Method:** Reproduced every CI lane locally on Windows *and* inside a Linux
`mcr.microsoft.com/dotnet/sdk:10.0` container matching `ubuntu-latest`; queried the GitHub Actions
API for the real run history and check annotations; curled every live REST endpoint against a
running API to check wire-contract drift; read the code behind each finding. Read-only — no
production code was changed.

Findings are ordered **most severe first**. Each carries severity, location, the defect, impact,
and a concrete fix direction. Findings are labelled **CONFIRMED** (directly observed) or
**SUSPECTED** (strong evidence, not yet caught in the act).

---

## Executive summary

**The CI failure is one flaky test, now reproduced and identified:**
`IqIngressEndpointTests.Streaming_RetuneToDifferentCenter_ClearsTransientData_ButKeepsDevices`
fails roughly **1 run in 12** under CI-like parallelism because it sequences a WebSocket server
handler with a fixed `Task.Delay(150)`. The build is sound and the other 512 tests are sound.

| Lane | Windows (local) | Linux container (CI-equivalent) |
|---|---|---|
| `dotnet build -c Release` | pass (0 errors, 4 warnings) | pass |
| Non-Docker tests (513) | 513 pass | **11 of 12 runs pass; 1 fails** (finding #1) |
| Coverage gate (>=85% line) | pass — total **95.5%** | pass — total **95.5%** (identical) |
| Docker lane (`Category=NeedsDocker`) | pass — 2 passed, 1 skipped | pass (on CI) |
| Frontend `npm run build` | pass | not run by CI |
| Frontend `npm test` (115 tests) | 115 pass | not run by CI |

Note the first row: a single deterministic run proves nothing here — the defect only appears across
repeated runs at the right parallelism, which is why it survived this long.

The failing GitHub run is [`30231481804`](https://github.com/rbenzing/SignalAtlas/actions/runs/30231481804)
on `9c83ab1`; the previous run on `bcb9cae` was green. The failing step is **`test` → "Test
(excluding Docker-dependent)"**; the coverage-gate step after it was *skipped* (never reached), and
the whole `test-docker` job **succeeded**. The only failure annotation on the run is the generic
`Process completed with exit code 1` — there are **no test-failure annotations**.

### Root cause — REPRODUCED and CONFIRMED

The lane is **flaky, and the flake has been caught**. In a Docker container reproducing the CI job
faithfully — `ubuntu:24.04`, .NET SDK installed via `dotnet-install.sh --channel 10.0` (what
`setup-dotnet`'s `10.0.x` resolves to; **SDK 10.0.401**), `--cpus=4` to match `ubuntu-latest`, and
the **exact** CI command including `--collect:"XPlat Code Coverage"` — the failure reproduced on
**run 2 of 12**:

```
SignalAtlas.Tests.Integration.IqIngressEndpointTests
    .Streaming_RetuneToDifferentCenter_ClearsTransientData_ButKeepsDevices [FAIL]
  Assert.True() Failure
  Expected: True
  Actual:   False
  at ...IqIngressEndpointTests.cs:line 219
Failed! - Failed: 1, Passed: 27, Skipped: 0, Total: 28 - SignalAtlas.Tests.Integration.dll
```

That is finding #1, at
[IqIngressEndpointTests.cs:219](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L219) —
`Assert.True(await GetPayloadCount(client, "/api/v1/emitters") > 0)`. The mechanism is the
*second* of the two race directions predicted in that finding, and the test's own comment names the
hazard it then fails to actually prevent:

1. The test sends the first config frame and sleeps 150 ms, expecting the server to have run its
   **one-time** `ILiveSession.OnDeviceStreamStarted()` by then.
2. The test seeds emitters/signals/alerts/spectrum/devices directly into the shared singleton repos.
3. Line 219 asserts the emitter is there.

Under 4-way parallelism the handler has often **not reached** `OnDeviceStreamStarted()` within
150 ms. It then fires *after* step 2, and `ClearDemoSeed()` — which is
`lock (_sync) _emitters.Clear()`, i.e. it wipes **everything**, not merely demo rows — deletes the
data the test just seeded. Line 219 sees 0 and fails.

**This is a test defect, not a product defect.** In production the ordering is safe by construction:
[IqIngressEndpoint.cs:76-79](src/SignalAtlas.Api/IqIngressEndpoint.cs#L76) calls
`OnDeviceStreamStarted()` *before* the pipeline is built and started, so the clear always precedes
any data that connection produces, and `LiveSession` latches exactly-once via `Interlocked`. Only
the test injects rows into those singletons in the window between socket-accept and the clear.

**Why it hid from the first 14 runs (methodology worth keeping):** this repo pins no xunit
parallelism (`xunit.runner.json` absent, no `CollectionBehavior` attribute), so `MaxParallelThreads`
defaults to `Environment.ProcessorCount` — and .NET **honours the cgroup CPU quota** (measured:
`--cpus=1` yields `ProcessorCount = 1` while `nproc` still reports 12). The earlier `--cpus=1`
stress therefore *serialised* the suite and the `--cpus=2` runs allowed only 2-way parallelism —
both **reduced** the contention the race needs. Matching CI's 4 CPUs is what exposed it. When
hunting a timing race in .NET tests, set the container CPU quota to the **CI runner's core count**;
throttling lower is counterproductive.

---

## P0 — Blocking: this is the CI failure, reproduced

### 1. WebSocket ingress tests assert on server state after a fixed sleep — CONFIRMED (reproduced)

> **Severity: P0.** Reproduced in a faithful Docker replica of the CI job at **1 failure in 12
> runs**, failing at exactly
> [IqIngressEndpointTests.cs:219](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L219)
> with `Assert.True() Failure — Expected: True, Actual: False`. This is the cause of run
> `30231481804`. Fixing it fixes CI. See "Root cause" in the executive summary for the mechanism.

- **Where:** [IqIngressEndpointTests.cs:141](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L141),
  [:185](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L185),
  [:225](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L225),
  [:235](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L235),
  [:100](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L100)
- **Defect:** the tests `await ws.SendAsync(configFrame)` then `await Task.Delay(150)` and
  immediately assert the server has already processed that frame — e.g. after a retune they assert
  `Assert.Equal(0, await GetPayloadCount(client, "/api/v1/emitters"))`. `SendAsync` only completes
  the *client-side write*. The handler at
  [IqIngressEndpoint.cs:91-110](src/SignalAtlas.Api/IqIngressEndpoint.cs#L91-L110) consumes config
  frames in an independent `await ReceiveAsync(...)` loop, so there is **no happens-before edge**
  between the send and the assertion — only a 150 ms hope.
- **Why it bites CI and not a workstation:** `ubuntu-latest` gives 2-4 vCPUs and `dotnet test`
  runs all four assemblies **in parallel**, with 379 unit tests and an integration host competing
  for the same cores. The 150 ms budget is not a bound, it is a guess.
- **Both directions are racy:** the first `Task.Delay(150)` is also load-bearing in the opposite
  sense — if the server's one-time `OnDeviceStreamStarted()` demo-seed clear lands *after* the test
  seeds its data, the later `Assert.True(count > 0)` fails instead.
- **Impact:** CI fails randomly on unchanged code, which destroys trust in the signal and makes a
  real regression indistinguishable from noise. This is the single highest-value thing to fix.
- **Fix:** replace every load-bearing sleep with **condition-based waiting** — poll the endpoint (or
  await a signal the handler sets after processing a config frame) until the expected state holds,
  with a generous ceiling (5 s) and a clear timeout message. The file already uses this pattern
  correctly for the notifier (`Task.WhenAny(notifier.First.Task, Task.Delay(5s))` at
  [:57](tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs#L57)) — apply it to the config
  frames too. Do **not** simply raise 150 ms to 500 ms; that trades a frequent flake for a rare one.
- **Observed failure rate:** 1 in 12 at `--cpus=4`. It did **not** reproduce in 14 earlier runs at
  `--cpus=2` and `--cpus=1` — see the methodology note in the executive summary: lowering the CPU
  quota lowers `Environment.ProcessorCount`, which lowers xunit's `MaxParallelThreads`, which
  *reduces* the contention the race depends on. Reproduce at the CI runner's core count, not below.
- **The failing assertion is the "seed then assert" direction**, not the "retune then assert zero"
  direction — but both windows in this test are unsound and both should be fixed. The confirmed one
  (line 219) races the test's manual seeding against the server's one-time demo-seed clear; the
  `Assert.Equal(0, ...)` block at lines 237-240 races the retune clear in the opposite direction and
  is equally unsynchronised.

---

## P3 — Downgraded after testing (kept in place so the retractions stay findable)

### 2. A generated coverage artifact is committed to source control — CONFIRMED (hygiene only)

> **Severity: P3, twice downgraded.** An earlier draft of this audit called this a confirmed
> high-severity gate corruption. **Two controlled experiments refuted that.** The wrong claim and the
> experiments that killed it are kept below, because knowing what was already ruled out is worth
> more than a tidy report.

- **What is verifiable and still true:**
  - `tests/SignalAtlas.Tests.Unit/coverage.json` — a **generated artifact** — is tracked in git
    (added in `3007d0a`). Confirm with `git ls-files | grep coverage.json`.
  - It slips past `.gitignore` because line 29 is **`*.coverage.json`**, a glob requiring a prefix,
    so it **does not match a plain `coverage.json`**. Lines 28-31 catch every other coverage
    artifact; this one filename falls through the crack.
  - The committed copy is badly stale — only **10 modules**, missing `Analyst`, `Enhancement` and
    `Fingerprint`, with line counts that no longer resemble the code (`Geospatial` 49 vs **571**).
    It is dead weight in every checkout and produces noisy diffs whenever anyone runs the gate
    locally (a single run rewrote it by +16,109 / -6,711 lines).
- **Impact:** repo hygiene and diff noise. **Not** a CI risk.
- **Fix:** `git rm --cached tests/SignalAtlas.Tests.Unit/coverage.json`, and add a bare
  **`coverage.json`** line to `.gitignore` (keep the existing `*.coverage.json`).

#### Two claims this audit made and then disproved — do not re-litigate

1. **"The stale committed baseline corrupts the gate by merging."** *Disproved.* A controlled A/B in
   one container — two identical fresh checkouts, the only difference being whether that file was
   deleted first, then the exact CI gate command on each — produced **byte-identical tables**
   (`Behavior` 98.59%, Total 95.5% in both). Separately, a gate run was observed to **overwrite**
   the file wholesale rather than merge into it. coverlet is not merging a structurally stale file.
2. **"The gate misreports `Behavior` as 0%, and the numbers are platform-dependent."** *Disproved.*
   The 0% reading was observed **once**, early in this audit, on Windows. It **does not reproduce**:
   a later Windows run of the identical command reported `Behavior` **98.59%** and Total **95.5%** —
   exactly matching Linux. There is no platform variance and no misreporting. The single 0% was
   almost certainly a stale or mid-build assembly in the test output directory during that first
   run — an artifact of local state, not a property of the code or CI.

   **The gate is sound.** `Behavior` is genuinely ~98.6% covered (142 lines, 140 hit; 10 tests in
   [BehaviorTests.cs](tests/SignalAtlas.Tests.Unit/BehaviorTests.cs) exercise `BehaviorEngine`), and
   the real total is 95.5%, comfortably above the 85% threshold.

> **Method note worth carrying forward:** a single observation of a bad number is not a defect. Both
> retracted claims came from trusting one local run. The CI race (finding #1) went the other way —
> it needed *twelve* runs at the right parallelism to appear. Repeat before concluding, in both
> directions.

## P1 — High

### 3. CI never builds or tests the frontend at all — CONFIRMED

- **Where:** [ci.yml](.github/workflows/ci.yml) — the workflow defines exactly two jobs, `test` and
  `test-docker`, both pure .NET.
- **Defect:** `web/` is entirely outside CI. The production bundle (`npm run build`), the
  **115 passing Vitest tests** across 16 files, and the Playwright e2e spec are never executed by
  the pipeline.
- **Impact:** this is the CI hole that maps directly onto the project's **own stated #1 recurring
  bug** — frontend/API type drift (CLAUDE.md landmine #1). The one class of bug the team knows keeps
  recurring is the one class CI cannot catch. A broken `web/` merges green.
- **Fix:** add a `web` job (`node-version: 20`, `npm ci`, `npm run build`, `npm test`). Wire the
  Playwright spec behind a service-started API, or mark it explicitly out-of-CI so its status is
  honest rather than ambiguous.

### 4. Two known high-severity vulnerable packages warn on every build — CONFIRMED

- **Where:** restore/build output (NU1903).
- **Defect:** `SSH.NET 2025.1.0` ([GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284))
  arrives transitively via `Testcontainers.PostgreSql 4.13.0` in
  `tests/SignalAtlas.Tests.Integration`; `SQLitePCLRaw.lib.e_sqlite3 2.1.11`
  ([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)) via the SQLite provider
  in `tests/SignalAtlas.Tests.Persistence`.
- **Impact:** **contained** — both are test-only and never reach the shipped artifact (landmine #6
  holds: `Microsoft.EntityFrameworkCore.Sqlite` is confined to the Persistence *test* project, and
  no `src/` project references either). The real cost is that two permanent high-severity warnings
  train everyone to ignore NU1903 output, so a *shipped* vulnerability would blend in.
- **Fix:** bump `Testcontainers.PostgreSql`, and pin a patched `SQLitePCLRaw` bundle in the test
  project. If neither has a fix yet, suppress **these two advisory IDs explicitly with a dated
  comment** (`NoWarn` scoped to those projects) so any *new* NU1903 is loud again.

---

## P2 — Medium

### 5. There is no lint setup for the frontend at all — CONFIRMED

- **Where:** [web/package.json](web/package.json) — no `lint` script; no ESLint config file exists
  anywhere in `web/`; ESLint is not a dependency.
- **Impact:** the repo's standing working rule is to run **build and lint** after writing code. The
  lint half is currently impossible to satisfy for `web/`, and no unused-variable, exhaustive-deps,
  or floating-promise class of bug is ever caught. For a React codebase with hand-rolled `useEffect`
  map/WebSocket lifecycles ([RfMap.tsx](web/src/views/RfMap.tsx)), the missing
  `react-hooks/exhaustive-deps` rule is a real correctness risk, not a style nit.
- **Fix:** add `eslint` + `typescript-eslint` + `eslint-plugin-react-hooks`, a flat
  `eslint.config.js`, and an `npm run lint` script; run it in the new `web` CI job from finding #3.

### 6. Nothing turns warnings into errors, and there is no shared build configuration — CONFIRMED

- **Where:** no `Directory.Build.props` / `Directory.Packages.props` in the repo; each `.csproj`
  repeats `TargetFramework`/`Nullable`/`ImplicitUsings`; no `TreatWarningsAsErrors`, no
  `EnableNETAnalyzers`, no `AnalysisLevel` anywhere.
- **Impact:** the build is green with 4 warnings today, including two real xUnit analyzer findings
  that CI surfaces as annotations on every run —
  [EnhancementToolTests.cs:67](tests/SignalAtlas.Tests.Unit/EnhancementToolTests.cs#L67) (xUnit2013)
  and [EnhancementDisabledE2ETests.cs:49](tests/SignalAtlas.Tests.Unit/EnhancementDisabledE2ETests.cs#L49)
  (xUnit2018). Warning count only ratchets upward, and version pinning is duplicated across 20
  project files (the EF 10.0.9 pin of landmine #5 has to be repeated by hand).
- **Fix:** add a `Directory.Build.props` with the shared TFM/nullable/analyzer settings and
  `TreatWarningsAsErrors` (at minimum in CI via `-warnaserror`), plus `Directory.Packages.props` for
  central package management. Fix the two analyzer warnings first so the switch starts clean.

### 7. NFR-S1 (encryption at rest) has no executable verification anywhere — CONFIRMED

- **Where:** [PersistenceTests.cs:70](tests/SignalAtlas.Tests.Integration/PersistenceTests.cs#L70) —
  `[Fact(Skip = NativeTdeUnavailable)]`, the **only** skipped test in the repo.
- **Defect:** the skip reason is **legitimate and well-argued** (upstream PostgreSQL has no TDE; the
  baseline is LUKS/filesystem encryption, an ops concern). But the consequence is that the
  "cold copy reveals no plaintext identifiers" guarantee is asserted **nowhere** — it is an
  unconditional skip, so it never runs in any lane, on any platform.
- **Impact:** a security NFR that reads as covered (there is a test named for it) is in fact
  unverified. This is a documentation-honesty risk more than a code defect.
- **Fix:** either verify the *deployed* control where it actually lives (an ops check that the data
  volume is LUKS-encrypted, asserted in [OPERATIONS.md](docs/OPERATIONS.md) and a deploy smoke test),
  or keep the skip and label NFR-S1 explicitly "ops-verified, not CI-verified" in SPEC §12.4 so no
  one reads the green suite as covering it.

### 8. The Docker lane is thinner than it looks and declares no Docker precondition — CONFIRMED

- **Where:** [ci.yml:32-44](.github/workflows/ci.yml#L32-L44)
- **Defect:** the job filters the whole solution to `Category=NeedsDocker`, which matches **one**
  assembly; the other three print `No test matches the given testcase filter` (harmless — this was
  verified to exit 0, it does *not* fail a run). Of the 3 tests that do match, 1 is permanently
  skipped (#7), so the lane's real coverage is **2 tests**. The job also names no `services:` block
  and no Docker health precondition — it relies purely on the runner happening to have a daemon.
- **Impact:** Postgres/Timescale/encryption-at-rest — the things this lane exists to protect — are
  guarded by two assertions, and the lane would silently become a no-op (still green) if the image
  or daemon became unavailable in a way Testcontainers swallowed.
- **Fix:** scope the lane to the one project that has these tests
  (`dotnet test tests/SignalAtlas.Tests.Integration`), add an explicit daemon precondition
  (`docker info`), and fail the job if **zero** Docker-category tests executed, so an empty lane is
  loud rather than green.

---

## P3 — Low / polish

### 9. CLAUDE.md contradicts the shipped seeding behaviour — CONFIRMED

- **Where:** [.claude/CLAUDE.md](.claude/CLAUDE.md) "Deliberate seams" vs
  [PersistenceServiceCollectionExtensions.cs:23](src/SignalAtlas.Persistence/PersistenceServiceCollectionExtensions.cs#L23)
- **Defect:** CLAUDE.md states the in-memory seed (one LoRa signal, one ADS-B device, 3 located
  emitters, spectrum frames) "is why contract tests and the offline UI have data with no DB. Don't
  'fix' empty data by adding DB requirements." In the current code `SeedDemoData` **defaults to
  `false`** by deliberate product decision ("an offline install should show honest empty state, not
  fabricated demo contacts"). Curling a live API with no DB returns **empty payloads** for
  `/signals`, `/devices`, `/alerts`, `/emitters`, `/spectrum/frames` and `/map/heatmap`.
- **Impact:** the guidance file that exists precisely to stop an agent from "fixing" empty data now
  describes behaviour the code no longer has — so the next reader sees empty endpoints, disbelieves
  the note, and may "fix" exactly what the note was protecting.
- **Fix:** update the CLAUDE.md bullet to say seeding is opt-in via `SeedDemoData=true` (and that
  empty is the correct default), keeping the "don't add DB requirements" warning intact.

### 10. An analyst refusal returns zero citations — CONFIRMED (invariant wording)

- **Where:** live `POST /api/v1/analyst/query` with an unsupported question returns
  `{"text":"I can't answer that...","citations":[],"mode":"offline","queryType":"Unsupported"}`.
- **Defect:** prime invariant #2 says *every* analyst answer carries non-empty evidence or
  citations. A refusal carries none.
- **Impact:** benign in practice — a refusal has nothing to cite, and it is clearly labelled
  `queryType: "Unsupported"`. But taken literally the invariant is violated, which erodes an
  invariant that should be absolute.
- **Fix:** narrow the invariant's wording to "every *substantive* answer (any `queryType` other than
  `Unsupported`)", and keep a contract test asserting refusals are the only citation-free shape.

### 11. Minor robustness nits — CONFIRMED

- **Culture-sensitive cursor encoding:** [Pagination.cs:64](src/SignalAtlas.Api/Pagination.cs#L64)
  uses `offset.ToString()` without `CultureInfo.InvariantCulture`, unlike the rest of the codebase
  which is careful about this. Harmless on an invariant-culture host; wrong in principle for a value
  that crosses the wire. Use `ToString(CultureInfo.InvariantCulture)`.
- **Unguarded negative limit:** [InMemoryObservationRepository.cs:29](src/SignalAtlas.Persistence/InMemoryObservationRepository.cs#L29)
  computes `new List<Observation>(Math.Min(limit, count))`, which throws on a negative `limit`. Not
  reachable today (callers pass validated or constant caps), but a `Math.Max(0, ...)` clamp costs
  nothing.
- **Sync-over-async:** three `GetAwaiter().GetResult()` bridges at
  [BoundedSampleSource.cs:50](src/SignalAtlas.Pipeline/BoundedSampleSource.cs#L50),
  [:59](src/SignalAtlas.Pipeline/BoundedSampleSource.cs#L59) and
  [BrowserUploadSampleSource.cs:91](src/SignalAtlas.Collector/BrowserUploadSampleSource.cs#L91).
  These are a **deliberate** async-channel to sync-`IEnumerable` bridge for the pipeline and are safe
  under ASP.NET Core (no sync context); flagged only so the constraint stays visible.
- **Frontend bundle:** `npm run build` emits a single **2,148 kB** JS chunk (619 kB gzip) and Vite
  warns about it. Fine for a loopback-first tool; worth `manualChunks` for maplibre/recharts if
  first paint ever matters.

### 12. `EgressGuard` is a name-based denylist — CONFIRMED (by design, noted for completeness)

- **Where:** [EgressPayload.cs:62-81](src/SignalAtlas.Enhancement/EgressPayload.cs#L62-L81)
- **Note:** the guard scans serialized payloads for eight fixed markers (`raw_iq`, `iq_sample`,
  `cleartext`, ...). Raw IQ smuggled under an unlisted key (`samples`, `baseband`, `i_q`) would pass.
  This is explicitly documented as **belt-and-suspenders** — the primary defence is that
  `EgressPayload` has no member capable of carrying IQ or content, which was verified. No change
  required; recorded so the layered design is not mistaken for the whole defence.

---

## UX & accessibility — validated in a real browser (Chrome DevTools MCP)

> **Method, and a gap this audit originally had.** The first pass of this audit never rendered the
> app — it checked API/type drift by curl and stopped there, despite CLAUDE.md's own instruction to
> "render it and look at it". This section closes that gap: the app was run (API + Vite), driven in
> Chrome, and audited with Lighthouse, the accessibility tree, scripted DOM checks, keyboard
> traversal and screenshots, in **both** the seeded (`SeedDemoData=true`) and default-empty states,
> at desktop and narrow widths.

**Baseline is good.** Lighthouse **Accessibility 94 / Best Practices 100** (desktop, Dashboard).
All tap targets ≥24px. **Zero colour-contrast failures** once alpha compositing is done correctly
(a naive first check reported a 1.43:1 failure on the active nav item — that was a false positive
from not compositing translucent overlays, and is recorded here so it is not "rediscovered").
Tables use real `<table>` + `<th scope="col">`. The protocol legend pairs colour with text, so no
information is conveyed by colour alone. Empty states are honest ("No signals to display.") rather
than broken charts. The Analyst round-trip works end to end, and MUI tooltips correctly appear on
keyboard focus.

### U1. Zero emitters renders as "—" instead of "0" — CONFIRMED (functional bug)
- **Where:** [Dashboard.tsx:134](web/src/views/Dashboard.tsx#L134)
- **Defect:** `value={emitters.length || "—"}`. `0` is falsy in JavaScript, so a **successful**
  response of zero emitters falls through to the em-dash placeholder. Verified live: the API
  returned `200` with `payload.length === 0` while the tile rendered "—", with the three
  neighbouring tiles all correctly showing "0".
- **Impact:** in the shipped default (`SeedDemoData=false`) the very first screen a new operator
  sees misreports a known-zero count as "no data / unknown". For an RF tool, "0 emitters" and
  "emitter count unavailable" mean different things.
- **Latent elsewhere:** [:132](web/src/views/Dashboard.tsx#L132) and
  [:135](web/src/views/Dashboard.tsx#L135) use the same `||` pattern
  (`signals.length || (summary.data?.signalCount ?? "—")`). They only *look* correct because the
  summary fallback also yields `0`; if `/summary` ever fails while `/signals` succeeds, they show
  "—" for a real zero too.
- **Fix:** render the count directly and reserve the placeholder for genuine load/error states —
  `value={signals.length}` with an explicit `loading`/`error` branch. Note line 133 already uses
  `??` correctly, so the file is internally inconsistent.

### U2. Nothing in the app is announced to screen readers — CONFIRMED
- **Where:** app-wide. A DOM scan found **0** elements with `aria-live`, `role="status"`,
  `role="alert"`, `role="log"`, `<output>` or `aria-busy`.
- **Defect:** the Analyst answer renders asynchronously into a plain container. A screen-reader user
  presses Send and hears **nothing** — no "loading", no announcement when the answer lands. The same
  gap applies to live RF updates arriving over SignalR (new alerts/signals).
- **Impact:** this is the most consequential a11y finding here. For a monitoring tool whose value is
  *"an alert just fired"*, silent real-time updates make the product unusable non-visually.
- **Fix:** wrap the Analyst answer in `role="status"` (polite) and set `aria-busy` on the form while
  in flight; give the alert feed a polite live region. Reserve `role="alert"` (assertive) for
  genuinely urgent items so it is not noisy.

### U3. Connection status is invisible to assistive tech — CONFIRMED
- **Where:** the header status dot — `<div class="MuiBox-root" aria-label="Live hub connected · API online">`
  with no text content and no `role`.
- **Defect:** `aria-label` is **prohibited** on a generic `div` (Lighthouse `aria-prohibited-attr`
  fails). The label is therefore ignored, so the online/offline state — conveyed visually by a
  coloured dot alone — has **no** accessible equivalent.
- **Fix:** give it `role="status"` (which also solves announcement on change) or render
  visually-hidden text. This single change fixes both the Lighthouse failure and a colour-only
  indicator.

### U4. The Analyst's main input has no label — CONFIRMED
- **Where:** the query field on `/analyst`.
- **Defect:** no `<label for>`, no wrapping label, no `aria-label`/`aria-labelledby` — the accessible
  name comes from the **placeholder only**, which disappears as soon as the user types and is
  inconsistently exposed by screen readers (WCAG 3.3.2, 4.1.2).
- **Fix:** add a real (optionally visually-hidden) `<label>`, keeping the placeholder as the example.

### U5. Heading order is broken and the headings are meaningless — CONFIRMED
- **Where:** Dashboard stat tiles. Actual order: `h1 "Signal Atlas"` → `h5 "0"`, `h5 "0"`, `h5 "—"`,
  `h5 "0"` → `h2 "Protocol distribution"`… (Lighthouse `heading-order` fails.)
- **Defect:** two problems. It skips `h1`→`h5`; and the headings are the bare **values** ("0", "3"),
  while their labels ("Signals", "Emitters") are separate non-heading text. A user navigating by
  headings hears "0, 0, —, 0".
- **Fix:** make the tile *label* the heading at the right level (`h2`), with the value as its content
  or an `aria-label` like "Emitters: 0". Don't use a heading for a bare number.

### U6. Keyboard focus is barely visible on the primary navigation — CONFIRMED
- **Where:** sidebar nav items.
- **Defect:** focused non-selected items get **no outline and no box-shadow** — the only indicator is
  a `rgba(255,255,255,0.12)` background. Measured against the sidebar that is **1.27:1**, far below
  the **3:1** WCAG 1.4.11 minimum for non-text contrast. Confirmed by screenshot: focused
  "Live Spectrum" is nearly indistinguishable from *selected* "Dashboard", so focus and selection
  are also ambiguous with each other.
- **Impact:** keyboard-only operators lose track of where they are. Note the app is otherwise
  keyboard-navigable and the tab order is logical.
- **Fix:** add a real focus ring (e.g. `outline: 2px solid` in the primary colour with an offset) on
  `.Mui-focusVisible`, styled distinctly from the selected state.

### U7. The page scrolls horizontally at narrow widths — CONFIRMED
- **Where:** `<main>`, on `/devices` at a ~485px viewport.
- **Defect:** `documentElement.scrollWidth` 623 vs `clientWidth` 485 — **138px of horizontal page
  scroll**. The Confidence column and part of the protocol legend are unreachable without scrolling
  sideways (WCAG 1.4.10 Reflow). The sidebar *does* collapse to a hamburger correctly, so the
  responsive intent exists; the content just isn't constrained.
- **Impact:** low if this is desktop-only by design (a loopback tool driven from the machine with the
  HackRF attached) — but then that should be stated, because the hamburger implies mobile support.
- **Fix:** let the table scroll inside its own container (`overflow-x: auto` on the wrapper) instead
  of pushing the page, and allow the legend to wrap.

### U8. The disabled "RF Map" nav item explains nothing — CONFIRMED
- **Where:** sidebar. It is a `<button disabled>` among `<a>` links.
- **Defect:** disabled controls are not focusable, so keyboard and screen-reader users cannot reach
  it to discover *why* it is unavailable (it is gated on the tuned frequency being a mapping band).
  Sighted users see a greyed item with no tooltip either.
- **Fix:** keep it focusable and convey state with `aria-disabled="true"` plus a tooltip/description
  naming the reason ("available when tuned to a mapping band").

### U9. Charts are opaque to assistive technology — CONFIRMED (common recharts limitation)
- **Where:** all three Dashboard charts.
- **Defect:** each renders as `application` containing a dozen empty `group`s and loose axis text. No
  accessible name, no summary, no tabular fallback. The `h2` above each chart is the only context.
- **Fix:** give each chart container `role="img"` with an `aria-label` summarising the takeaway
  ("Protocol distribution: LoRa 1 of 1 signals"), or render a visually-hidden data table. Cheap, and
  it also helps the "agentic browsing" score Lighthouse flagged at 30.

### U10. Minor
- **Layout shift:** Lighthouse CLS **0.097** on load (content reflows as data arrives) — close to the
  0.1 "needs improvement" boundary. Reserve space for the tiles/charts while loading.
- **No skip link:** every route repeats a 7-item sidebar before `<main>`; keyboard users tab through
  it each time.
- **No table caption:** the devices table has no `<caption>`/`aria-label`; the visible "Devices"
  heading is not programmatically associated with it.
- **Empty state has no call to action:** the default install shows zeros with no "connect a HackRF to
  begin" prompt. The header button is discoverable, so this is a polish item, not a defect.

## Security review — OWASP Top 10 (2021) & SOC 2 Trust Services Criteria

> Scope note: this platform's threat model is a **single-operator, loopback, receive-only edge node**
> (SPEC §4.7, ADR-2). Several items below are only exploitable once that assumption is broken — which
> is exactly what finding S1 turned out to do.

### S1. The shipped `docker-compose.yml` published an unauthenticated API to every interface — FIXED
- **OWASP A01 (Broken Access Control) / A07 (Authentication Failures); SOC 2 CC6.1**
- **Defect:** `ports: - "8080:8080"` publishes on **0.0.0.0**, and `IAuthorizationGate` ships as
  `SingleOperatorPassThroughGate` whose `Authorize()` **returns `true` unconditionally**. So a
  `docker compose up` put the entire `/api/v1` surface — plus the deliberately un-gated
  `/ingest/iq` WebSocket and `/hub/live` — on every host interface with **no authentication at all**.
  Anyone on the LAN could read device identifiers and emitter locations, or inject synthetic IQ into
  the pipeline. The "loopback posture" the SPEC relies on was not actually enforced anywhere.
- **Fix applied:** bound the published port to `127.0.0.1:8080:8080`, with a comment stating that LAN
  exposure requires real auth first (ADR-9 RBAC). The pass-through gate itself is unchanged — that is
  a deliberate product decision, and it is *safe* behind loopback; the bug was the binding.

### S2. API container ran as root — FIXED
- **OWASP A05 (Security Misconfiguration); SOC 2 CC6.1**
- **Defect:** the runtime stage had no `USER`, so the process ran as **root** inside the container; a
  process compromise started with root and USB device access (the compose file optionally maps
  `/dev/bus/usb`).
- **Fix applied:** `USER app` (the non-root uid the aspnet base image ships). The published output is
  read-only at runtime, so nothing needed write access.

### S3. A shared hardcoded database password — FIXED
- **SOC 2 CC6.1 (credential management); OWASP A07**
- **Defect:** `POSTGRES_PASSWORD: signalatlas` was committed, and the API's connection string
  repeated the literal — every deployment of this repo shared one password published on GitHub.
- **Fix applied:** both now read `${POSTGRES_PASSWORD:-…}` from the environment, with an obviously
  non-production default so a throwaway local `up` still works.

### S4. Identifier and location reads were not audit-logged — FIXED
- **SOC 2 CC7.2 (monitoring access to sensitive data); the project's own NFR-S2**
- **Defect:** NFR-S2 states *every* identifier/location read is audit-logged, and `/devices*` did so.
  But **`/emitters`** returns `identifiers` **and** `estLatitude`/`estLongitude`, **`/emitters/{id}`**
  returns one emitter's identity, and **`/map/heatmap`** returns positions — **none** wrote an audit
  entry. The endpoints exposing emitter identity and geolocation left no trail whatsoever.
- **Fix applied:** `read:emitters`, `read:emitter` and `read:map-heatmap` entries, with the
  single-emitter entry written **before** the 404 check (the attempt is the auditable event).
  Covered by 3 new contract tests.

### S5. No Content-Security-Policy — FIXED
- **OWASP A05**
- **Defect:** `SecurityHeadersMiddleware` set `X-Content-Type-Options`, `X-Frame-Options` and
  `Referrer-Policy`, but no CSP — thin defence-in-depth for responses carrying model- and
  decoder-derived strings.
- **Fix applied:** `default-src 'none'; frame-ancestors 'none'` — the API serves JSON, so it can
  afford the strictest policy. **Note:** this covers the API only; the SPA is served by Vite/your
  static host and still needs its own CSP. Tracked, not fixed here.

### S6. Open items (NOT fixed — design decisions or need an owner)
- **Audit actor is the constant `"local-operator"`** (`audit.Record("local-operator", …)`). SOC 2
  CC6.1 expects actions attributable to an individual; with a single-operator pass-through there is
  no identity to record. Inherent to the current model — resolve with ADR-9 RBAC, not a patch.
- **No TLS.** Loopback-only makes this defensible, but it should be a *stated* control, and any
  future LAN exposure requires HTTPS + HSTS.
- **Persisted RF identifiers are arguably personal data.** BSSIDs/MAC addresses and ICAO IDs are
  treated as personal data under GDPR/CCPA in many readings, and they are persisted indefinitely.
  There is no retention policy, no subject-access path, and no data-classification statement. This is
  a **privacy/governance gap** (SOC 2 Privacy; Confidentiality C1.1) that needs a policy decision —
  the Timescale retention hook in `TimescaleInitializer` is the natural enforcement point.
- **NFR-S1 encryption-at-rest is unverified anywhere** (finding #7 above).

### Verified clean (checked, no issue found)
- **A03 Injection:** the only raw SQL is a `const string` in `TimescaleInitializer`; everything else
  is EF Core with parameters. No string-concatenated queries.
- **A10 SSRF:** the sole outbound call is a **constant** Celestrak URL; `satelliteName` is used only
  for local matching and never interpolated into the request.
- **Rate limiting** is configured globally (`AddRateLimiter` + `UseRateLimiter`).
- **No CORS policy** is registered — same-origin only, with Vite proxying in dev. Correct for this
  posture; do not add a permissive one.
- **Error handling** returns RFC 7807 problem details; a contract test already asserts internal
  exception text does not leak.

---

## HackRF One hardware-profile validation

Checked the implementation against the HackRF One's published capabilities and libhackrf's actual
USB protocol, on **both** sides of the WebUSB seam.

**The integration is faithful.** Verified correct: VID `0x1d50` with PIDs `0x6089`/`0x604b`; every
vendor request code (`SET_TRANSCEIVER_MODE`=1, `SAMPLE_RATE_SET`=6,
`BASEBAND_FILTER_BANDWIDTH_SET`=7, `SET_FREQ`=16, `AMP_ENABLE`=17, `SET_LNA_GAIN`=19,
`SET_VGA_GAIN`=20, `ANTENNA_ENABLE`=23); the `set_freq` MHz/Hz split-struct layout; the
`sample_rate` freq/divider pair; `value`/`index` packing for the filter width; LNA rounding to 8 dB
and VGA to 2 dB steps; and the 24-byte part-id/serial parse.

Two details are worth calling out as *correct*, because both are easy to get wrong:
- **`SET_LNA_GAIN`/`SET_VGA_GAIN` use the IN direction** with the gain in `index` and a 1-byte
  status read — not an OUT transfer. The code gets this right.
- **Receive-only is enforced at the USB layer, not just in C#.** `SET_TXVGA_GAIN` (21) is absent from
  the request enum and `TransceiverMode` has no `TX` member, so there is no transmit opcode to call.
  That is invariant #1 held at the lowest level available in the browser.

### H1. The backend modelled the baseband filter as continuous; the radio's is discrete — FIXED
- **Where:** [HackRfLimits.cs](src/SignalAtlas.Collector/HackRfLimits.cs) vs
  [hackrf.ts](web/src/sdr/hackrf.ts)
- **Defect:** the MAX2837 baseband filter exposes exactly **16 discrete widths** (1.75, 2.5, 3.5, 5,
  5.5, 6, 7, 8, 9, 10, 12, 14, 15, 20, 24, 28 MHz); libhackrf snaps a request **down** to one of
  them. The browser client models this correctly (`VALID_BASEBAND_BW` + `computeBasebandFilterBw`).
  The **backend did not** — `IsValidBasebandBwHz` was a plain 1.75-28 MHz range check and
  `ClampBasebandBwHz` a plain `Math.Clamp`. So values the radio cannot select (13 MHz, 2.4 MHz, even
  the old test's 2 MHz) passed validation.
- **Why it matters:** `Ingestion:BasebandBwHz` is operator-configurable and flows to
  [ScanCollector.cs:50](src/SignalAtlas.Collector/ScanCollector.cs#L50) as
  `BandwidthHz: block.ReceiverConfig?.BasebandBwHz ?? block.SampleRateHz` — **persisted** as
  `Observation.BandwidthHz`, which landmine #7 documents as *the true analog passband*. The system
  could therefore record a passband the hardware was never running. The default path was affected
  too: `ClampBasebandBwHz(2_400_000)` returned 2.4 MHz, a filter that does not exist.
- **Fix applied:** added `SupportedBasebandBwHz` (the real 16-value table, cross-referenced to the
  TS table in both files' comments); `IsValidBasebandBwHz` is now set membership; `ClampBasebandBwHz`
  snaps down to the widest supported width ≤ the request — the same rule as libhackrf and the
  browser, so both paths now land on an identical passband. Covered by 26 new/updated unit tests.
- **Two existing tests asserted the old behaviour** (`ClampBasebandBwHz(2_000_000) == 2_000_000` and
  `IsValidBasebandBwHz(2_000_000) == true`). They encoded a passband the radio cannot produce, so
  they were changed deliberately, with the reasoning recorded inline per the TDD law (§6.2).

### H2. Open: the same constant lives in two languages
`SupportedBasebandBwHz` (C#) and `VALID_BASEBAND_BW` (TS) must not drift — this finding exists
*because* they already had. Each now names the other in a comment, but that is a convention, not a
guarantee. A generated constant, or a contract test asserting the API's advertised widths match the
client's table, would make it structural.

## What was verified as healthy (no action needed)

These were checked because they are the documented landmines or prime invariants, and all held:

- **Receive-only (invariant #1):** no transmit member on `ISampleSource`/`IHackRfDevice`;
  `ReceiveOnlyTests` enforces it by reflection.
- **Deterministic core (invariant #4):** no `DateTime.Now`, `Guid.NewGuid()`, or unseeded `Random`
  in domain/pipeline code. The two `Guid.NewGuid()` sites in
  [DeferredClaudeAnalyzer.cs:53](src/SignalAtlas.Enhancement/DeferredClaudeAnalyzer.cs#L53) are the
  allowed per-request API-layer case and are documented as such; the enrichment id correctly uses
  `DeterministicGuid.From(...)`.
- **Auth gating (invariant #5):** all 20 `/api/v1` routes go through the `MapGroup` carrying
  `AuthGateMarker` + `AuthorizationGateFilter`; `/health`, `/ready`, `/metrics`, `/hub/live` and
  `/ingest/iq` are ungated by design.
- **Wire contract / type drift (landmine #1):** every live endpoint was curled and compared against
  `web/src/api.ts`. `Signal`, `Device`, `Emitter`, `Alert`, `Summary`, `SpectrumOccupancy`,
  `SpectrumCoverageBand`, `HeatmapCell` and `AnalystAnswer` **all match the real JSON exactly**,
  including the `{schemaVersion, correlationId, payload, nextCursor}` envelope and the
  `POST /analyst/query` request field (`text`). No drift found this pass.
- **Stateful demodulator lifetime (landmine #10):** `AdsBDemodulator` is registered
  `AddTransient` ([Program.cs:102](src/SignalAtlas.Api/Program.cs#L102)) and both pipeline build
  sites resolve `GetServices<IDemodulator>()` fresh.
- **MapLibre `circle-radius` (landmine #3):** [rfmap.ts:102-111](web/src/lib/rfmap.ts#L102-L111) is
  a correct top-level `interpolate`/`exponential 2` on `zoom` with the 2px floor inside the stop
  outputs.
- **Prior P0 remediations are real:** the Zigbee/BLE device-identity collapses (#1/#2 of the
  2026-07-18 audit) are genuinely fixed — `adva` and `src_addr` now lead `PrimaryKeyOrder`
  ([IngestionPipeline.cs:282-283](src/SignalAtlas.Pipeline/IngestionPipeline.cs#L282-L283)) and the
  resolver's per-protocol keys ([DeviceResolver.cs:73](src/SignalAtlas.Decode/DeviceResolver.cs#L73)).
- **Pagination:** `TakePeek = Offset + Limit + 1` (saturating) paired with
  `.Skip(Offset).Take(Limit+1)` is correct, and cursor/offset validation returns RFC 7807 problems.
- **Geolocation weighting:** the dBFS to linear conversion `10^(dBFS/10)` at
  [GeolocationEngine.cs:36](src/SignalAtlas.Geospatial/GeolocationEngine.cs#L36) keeps weights
  strictly positive (no negative-weight centroid blow-up), and `Heatmap` bucketing is deterministic.
- **In-memory repositories are correctly locked** for the singleton/concurrent-request usage.
- **Claude integration** uses current model IDs (`claude-sonnet-5`, `claude-opus-5`), bounds the
  call with a linked timeout CTS, is registered as a singleton, and is genuinely off the critical
  path (`StubClaudeClient` unless a key is configured).
- **No TODO/FIXME/HACK debt** in `src/` or `web/src/`.

---

## Reproducing

```bash
# Full CI-equivalent lane (Windows or Linux) — currently green
dotnet build SignalAtlas.slnx -c Release
dotnet test SignalAtlas.slnx --no-build -c Release --filter "Category!=NeedsDocker"

# THE REPRODUCTION THAT WORKS (finding #1). --cpus=4 is load-bearing: it matches the CI runner's
# core count, so Environment.ProcessorCount (and therefore xunit MaxParallelThreads) is 4. Lower
# quotas serialise the suite and HIDE the race — 14 runs at --cpus=1/2 never reproduced it, while
# this configuration failed on run 2 of 12.
git clone . /tmp/sa && docker run --rm --cpus=4 -m 8g -v /tmp/sa:/mnt/src:ro \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c \
  'cp -r /mnt/src /work && cd /work && dotnet restore SignalAtlas.slnx >/dev/null &&
   dotnet build SignalAtlas.slnx --no-restore -c Release >/dev/null &&
   for i in $(seq 1 20); do
     dotnet test SignalAtlas.slnx --no-build -c Release \
       --filter "Category!=NeedsDocker" --collect:"XPlat Code Coverage" >/tmp/r.log 2>&1
     ec=$?; echo "run $i exit=$ec"
     [ $ec -ne 0 ] && { grep -E "\[FAIL\]|Error Message|Expected:|Actual:" /tmp/r.log | head -20; break; }
   done'
# Expected output when it trips (seen on run 2 of 12):
#   IqIngressEndpointTests.Streaming_RetuneToDifferentCenter_ClearsTransientData_ButKeepsDevices [FAIL]
#   Assert.True() Failure / Expected: True / Actual: False   at IqIngressEndpointTests.cs:line 219

# Finding #2: a generated artifact is tracked, and the .gitignore glob misses it
git ls-files | grep coverage.json          # -> tests/SignalAtlas.Tests.Unit/coverage.json (tracked!)
git log --oneline -1 -- tests/SignalAtlas.Tests.Unit/coverage.json   # -> 3007d0a baseline commit
grep -n 'coverage.json' .gitignore         # -> only "*.coverage.json"; a bare coverage.json is NOT matched

# NOTE: two theories about this file were TESTED AND DISPROVED — do not re-run these experiments.
#  (a) "the stale file corrupts the gate via coverlet merging": two identical checkouts differing
#      only in whether the file was deleted produced byte-identical tables (Behavior 98.59%,
#      Total 95.5% in both); a gate run was also seen to OVERWRITE the file, not merge into it.
#  (b) "the gate misreports Behavior as 0% / varies by platform": the 0% was a one-off from local
#      state and does NOT reproduce — a later Windows run gave Behavior 98.59% / Total 95.5%,
#      matching Linux exactly. The gate is sound; only the tracked-artifact hygiene issue remains.

# Prove the gate misreports a module that is in fact well covered
dotnet test tests/SignalAtlas.Tests.Unit -c Release -p:CollectCoverage=true \
  "-p:Include=[SignalAtlas.Behavior]*" -p:Threshold=85 -p:ThresholdType=line -p:ThresholdStat=total
# The table printed SignalAtlas.Behavior = 0%, while the emitted coverage.json for that module
# holds 142 lines / 140 hit = 98.6%, and 10 BehaviorTests demonstrably exercise BehaviorEngine.
```

---

## Recommended order of work

1. **Fix `IqIngressEndpointTests`** (#1) — this is the CI outage, reproduced. Replace both
   unsynchronised windows with condition-based waiting: poll the endpoint until the expected state
   holds (ceiling ~5 s, clear timeout message), or have the handler expose a signal the test can
   await after it processes a config frame. Do **not** just raise 150 ms — that lowers the failure
   rate without removing it. Verify with the 20-run `--cpus=4` loop above.
2. **Add the `web` CI job** (#3) + **ESLint** (#5) — closes the pipeline hole sitting directly under
   the project's own #1 recurring bug class (API/type drift). With #2 retracted, this is the largest
   remaining *real* gap in the pipeline.
3. **Fix the UX/a11y defects that are actually bugs** — U1 (zero renders as "—" on the default first
   screen), U2 (nothing announced to screen readers), U3 (connection status inaccessible), U4
   (unlabelled Analyst input), U6 (near-invisible focus ring). U1 is a one-line fix; U2/U3 are small
   and together make the app usable non-visually.
4. Then the hygiene tier: package advisories (#4), `Directory.Build.props` + warnings-as-errors (#6),
   Docker-lane precondition (#8), untracking `coverage.json` (#2), the remaining a11y polish
   (U5, U7-U10), and the doc-honesty fixes (#7, #9, #10).

> **Add a11y to CI when the `web` job lands (#3).** Every U-finding above is machine-detectable:
> Lighthouse CI or `axe-core` in the Playwright spec would have caught U3 and U5 automatically, and
> a render test would have caught U1. Right now nothing in the pipeline looks at the UI at all.

> Consider also pinning xunit parallelism explicitly (an `xunit.runner.json` with a fixed
> `maxParallelThreads`) so test scheduling no longer varies silently with the runner's core count —
> that variance is exactly what made this bug reproduce on CI but not on a developer machine.
