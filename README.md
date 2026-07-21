# Signal Atlas

**A living, explainable map of the radio spectrum.** Signal Atlas is a receive-only
RF intelligence platform: it observes RF activity with an SDR, classifies signals,
decodes device identity, correlates emitters, estimates locations, learns behavior,
detects anomalies, and presents it all as an intuitive map + spectral UI — fully
offline-capable on a field laptop.

> © 2026 Russell Benzing. All Rights Reserved. Proprietary — see [LICENSE](LICENSE).
> Receive-only, metadata-not-content, lawful-use-only (see LICENSE §4 and [SPEC.md](docs/SPEC.md) §4.2).

---

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | **10.0+** | backend + tests |
| Node.js | **18+** (npm 10+) | frontend |
| Docker | any recent | *optional* — Postgres/TimescaleDB deployment + the DB test lane |
| HackRF + SoapySDR | — | *optional* — real RF capture (else file/synthetic sources) |

No database, Docker, or SDR is required to run and explore the app — it falls back
to in-memory storage and seeded/synthetic data.

---

## Quick start (offline dev — two processes)

**1. Backend API** (listens on `http://localhost:5285`):

```bash
dotnet run --project src/SignalAtlas.Api
```

**2. Frontend** (in a second terminal — dev server on `http://localhost:5173`,
proxies `/api` and `/hub` to the backend):

```bash
cd web
npm install
npm run dev
```

Open **http://localhost:5173**. With no connection string configured the API uses
in-memory seeded data, so the Dashboard, Live Spectrum (waterfall), RF Map,
Emitters, Devices, and Alerts views all show data immediately.

### See live data flow through the pipeline

Start the API with the ingestion loop enabled to stream a synthetic (or file/HackRF)
source through classify → correlate → anomaly → persist, pushing live updates over
SignalR:

```bash
# bash
Ingestion__Enabled=true dotnet run --project src/SignalAtlas.Api
```
```powershell
# PowerShell
$env:Ingestion__Enabled="true"; dotnet run --project src/SignalAtlas.Api
```

Point it at a captured `.iq` file with `Ingestion__IqFile=/path/to/capture.iq`
(interleaved signed-8-bit I/Q, HackRF format). A real HackRF is auto-detected when
present; otherwise it uses a bounded synthetic source.

### Live capture from a browser HackRF (WebUSB)

No server-side SDR? Plug a **HackRF One** into the machine running the browser and
capture straight from the UI. In the top navbar, click **Connect HackRF** and pick the
device in the browser's WebUSB permission prompt. The browser reads IQ over WebUSB and
streams it to the API over a binary WebSocket (`/ingest/iq`); the backend runs the same
classify → correlate → anomaly pipeline and pushes the live waterfall/occupancy over
SignalR. The radio is **receive-only** (no transmit path exists) and raw IQ never leaves
the edge — only spectra/features are produced. Tune center freq / sample rate / gains /
bias-tee from the navbar popover (the RX config is recorded as capture provenance); a
previously-authorized device reconnects on load without re-prompting.

Requirements: a WebUSB browser (**Chrome or Edge**) and the API running (so the Vite proxy
reaches `/ingest/iq` on 5285). On **Windows**, install the WinUSB driver for the HackRF with
[Zadig](https://zadig.akeo.ie/) (Options → List All Devices → HackRF One → replace driver
with WinUSB), otherwise it enumerates as a COM port and the chooser is empty. Like live
file/synthetic mode, this classifies protocols but **defers device determination** (the
IQ→bits demodulators are a separate seam). See [OPERATIONS.md](docs/OPERATIONS.md) §8.

---

## Run from your IDE

### VS Code
Open the repo folder. Recommended extensions (C# Dev Kit, ESLint, Prettier) are
prompted from [.vscode/extensions.json](.vscode/extensions.json). Then use the
**Run and Debug** panel:

- **Full stack: API + Web** — one click starts the API (with debugger) *and* the
  Vite dev server; open http://localhost:5173.
- **API** / **API (live ingestion)** — run just the backend (the live variant sets
  `Ingestion__Enabled=true`).
- **Web dev server** — just the frontend.

Run `web: install` once from **Terminal → Run Task** (or `npm install` in `web/`)
before the first web launch. Build/test tasks (`build`, `test (no docker)`,
`web: build`) are also under Run Task.

### Visual Studio (2022 17.10+ for `.slnx`)
Open **`SignalAtlas.slnx`**. Set **SignalAtlas.Api** as the startup project, pick a
profile from the Run dropdown — **API** or **API (live ingestion)** — and press F5.
The frontend runs alongside it from a terminal: `cd web && npm install && npm run dev`,
then browse to http://localhost:5173 (it proxies to the API on 5285).

---

## Running with a database (optional, Docker)

For persistent Postgres/TimescaleDB storage and the full edge bundle:

```bash
docker compose up --build      # Timescale + API on a named volume
docker compose down            # stop; data persists in signalatlas-data
```

Or run the API against your own Postgres by setting the connection string — the API
selects EF Core + Postgres when it's present, in-memory otherwise:

```bash
ConnectionStrings__SignalAtlas="Host=localhost;Database=signalatlas;Username=postgres;Password=..." \
  dotnet run --project src/SignalAtlas.Api
```

See **[OPERATIONS.md](docs/OPERATIONS.md)** for run modes, configuration keys, secrets,
health/metrics, backup/restore, retention, and the failure playbook.

---

## Tests

```bash
# Full suite minus the Docker-dependent lane (runs everywhere):
dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"

# Docker lane (needs a Docker host): Postgres/Timescale round-trip, hypertables:
dotnet test SignalAtlas.slnx --filter "Category=NeedsDocker"

# Frontend build / type-check:
cd web && npm run build
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs the fast lane, a
Docker lane, and a coverage gate (≥85% line on the core + AI projects).

---

## Key endpoints

REST is under `/api/v1` (versioned envelope `{schemaVersion, correlationId, payload}`,
RFC 7807 problem-details, gated by the single-operator authorization seam). Live
push is over the SignalR hub `/hub/live`.

`GET /signals` · `GET /devices` · `GET /emitters[/{id}]` · `GET /alerts` ·
`GET /summary` · `GET /spectrum/frames|occupancy|coverage` ·
`POST /analyst/query` · `GET /sessions` · `POST /analysis/runs` ·
`GET /enrichments` + accept/reject · `GET /health` · `GET /ready` · `GET /metrics`.

Browser WebUSB HackRF IQ ingress is the un-gated binary WebSocket `/ingest/iq`
(mapped outside `/api/v1`, alongside `/hub/live`; see [OPERATIONS.md](docs/OPERATIONS.md) §8).

---

## Architecture

```
src/
  SignalAtlas.Domain          entities, value objects, interfaces (no deps)
  SignalAtlas.Collector       scan collector, sample sources (File/Synthetic/HackRF)
  SignalAtlas.Processing      DSP: FFT, PSD, occupancy, features
  SignalAtlas.Classification  rule-based classifier
  SignalAtlas.Ml              M9 ML classifier (behind IClassifier)
  SignalAtlas.Decode          protocol decoders + device resolver
  SignalAtlas.Correlation     emitter correlation
  SignalAtlas.Fingerprint     M10 RF/PHY fingerprinting + spoof detection
  SignalAtlas.Geospatial      centroid + uncertainty
  SignalAtlas.Behavior        behavior profiles (M6) + prediction (M11)
  SignalAtlas.Anomaly         rule-based anomaly detectors
  SignalAtlas.Analyst         M12 dual-mode NL analyst (offline + Claude seam)
  SignalAtlas.Enhancement     M13 optional Claude enhancement (cited overlay)
  SignalAtlas.Pipeline        live ingestion orchestrator
  SignalAtlas.Persistence     EF Core (Postgres/Timescale) + in-memory repos
  SignalAtlas.Api             Minimal API, SignalR hub, auth/observability
web/                          React + TS + MUI + MapLibre + Recharts UI
tests/                        unit · contract · integration(Docker) · persistence
```

**Principles:** receive-only; explainable-only (every verdict carries evidence);
offline-first; deterministic core; honest limits (uncertainty always surfaced).
Built test-first (RED → GREEN → REFACTOR).

---

## Current limitations

- **IQ→bits demodulation** is a seam pending a physical HackRF + field `.iq`
  fixtures; live mode classifies protocols but defers device determination until then.
- **Live Claude client** is stubbed — the analyst cloud mode and the M13 enhancement
  pass call an `IClaudeClient` that needs an API key (via `SignalAtlas:ClaudeApiKey`)
  and network; the offline/deterministic paths are fully functional.
- Single-node; a real map basemap drops in via `VITE_BASEMAP_STYLE` + a bundled
  `.pmtiles` (offline graticule is the default).

---

## Documentation map

| Document | Purpose |
|---|---|
| [docs/SPEC.md](docs/SPEC.md) | The buildable engineering specification (the contract). |
| [docs/plan.md](docs/plan.md) | The founder vision. |
| [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) | Milestones, decisions, TDD approach. |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Deployment, config, health/metrics, backups, runbook. |
| [LICENSE](LICENSE) | Proprietary license + responsible-use terms. |
