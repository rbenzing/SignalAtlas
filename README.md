# Signal Atlas

<div align="center">

[![License: Proprietary](https://img.shields.io/badge/License-Proprietary-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0+-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![Node.js](https://img.shields.io/badge/Node.js-18+-339933?logo=node.js)](https://nodejs.org)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.0+-3178C6?logo=typescript)](https://www.typescriptlang.org)
[![C#](https://img.shields.io/badge/C%23-12+-239120?logo=csharp)](https://learn.microsoft.com/en-us/dotnet/csharp)
[![React](https://img.shields.io/badge/React-18+-61DAFB?logo=react)](https://react.dev)
[![Docker Ready](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker)](https://www.docker.com)

**A living, explainable map of the radio spectrum.**

Signal Atlas is a receive-only RF intelligence platform: it observes RF activity with an SDR, classifies signals, decodes device identity, correlates emitters, estimates locations, learns behavior, detects anomalies, and presents it all as an intuitive map + spectral UI — fully offline-capable on a field laptop.

[Features](#features) • [Quick Start](#quick-start) • [Documentation](#documentation) • [Architecture](#architecture)

</div>

---

> © 2026 Russell Benzing. All Rights Reserved. Proprietary — see [LICENSE](LICENSE).
>
> **Receive-only, metadata-not-content, lawful-use-only** (see LICENSE §4 and [SPEC.md](docs/SPEC.md) §4.2).

---

## Features

- 🗺️ **Intuitive Map UI** – Geolocate emitters with uncertainty visualization
- 📊 **Live Spectrum** – Real-time waterfall and occupancy analysis
- 🤖 **Intelligent Classification** – Rule-based + ML classifiers (M9) for protocol identification
- 🔍 **Signal Correlation** – Smart emitter grouping across multiple observations
- 🎯 **Anomaly Detection** – Rule-based and behavioral anomaly detectors
- 📋 **Device Resolution** – Protocol decode + fingerprinting (M10) + device identity
- 🌐 **Browser WebUSB** – Capture live from HackRF directly in Chrome/Edge (no server-side SDR required)
- 💾 **Offline-First** – In-memory storage with optional Postgres/TimescaleDB persistence
- 📡 **RF Fingerprinting** – Spoof detection and behavior profiles (M6, M11)
- 🧠 **Analyst Mode** – Dual-mode NL analyst (M12) — offline deterministic + Claude integration
- ⚡ **SignalR Live Push** – Real-time updates over WebSockets
- 🎓 **Fully Explainable** – Every verdict carries evidence; uncertainty always surfaced

---

## Prerequisites

| Tool | Version | Purpose |
|---|---|---|
| **.NET SDK** | **10.0+** | Backend + tests |
| **Node.js** | **18+** (npm 10+) | Frontend |
| **Docker** | any recent | *Optional* – Postgres/TimescaleDB + DB tests |
| **HackRF + SoapySDR** | — | *Optional* – Real RF capture |

> **No database, Docker, or SDR is required.** The app falls back to in-memory storage with seeded/synthetic data.

---

## Quick Start

### 1️⃣ Backend API
Start the .NET API (listens on `http://localhost:5285`):

```bash
dotnet run --project src/SignalAtlas.Api
```

### 2️⃣ Frontend
In a new terminal, start the React dev server (on `http://localhost:5173`, proxies `/api` and `/hub` to backend):

```bash
cd web
npm install
npm run dev
```

Open **http://localhost:5173** — the Dashboard, Live Spectrum, RF Map, Emitters, Devices, and Alerts views all load with in-memory seeded data.

---

### See Live Data Flow

Start the API with the ingestion loop enabled to stream synthetic (or file/HackRF) data through classify → correlate → anomaly → persist, with live SignalR updates:

**Bash:**
```bash
Ingestion__Enabled=true dotnet run --project src/SignalAtlas.Api
```

**PowerShell:**
```powershell
$env:Ingestion__Enabled="true"; dotnet run --project src/SignalAtlas.Api
```

Point at a captured `.iq` file:
```bash
Ingestion__IqFile=/path/to/capture.iq dotnet run --project src/SignalAtlas.Api
```

*(Interleaved signed-8-bit I/Q, HackRF format. Real HackRF auto-detected when present; otherwise uses bounded synthetic source.)*

---

### Live Browser Capture with HackRF (WebUSB)

**No server-side SDR?** Plug a **HackRF One** into your machine and capture live from the browser:

1. Click **Connect HackRF** in the navbar
2. Select the device in the browser's WebUSB permission prompt
3. Browser reads IQ over WebUSB, streams to API over binary WebSocket (`/ingest/iq`)
4. Backend runs classify → correlate → anomaly pipeline, pushes live waterfall/occupancy over SignalR

**Features:**
- Receive-only (no transmit path)
- Raw IQ never leaves the edge — only spectra/features produced
- Tune center freq / sample rate / gains / bias-tee from navbar popover
- RX config recorded as capture provenance
- Previously-authorized device auto-reconnects without re-prompting

**Requirements:**
- WebUSB browser (**Chrome** or **Edge**)
- API running (so Vite proxy reaches `/ingest/iq`)
- **Windows:** Install WinUSB driver via [Zadig](https://zadig.akeo.ie/) (Options → List All Devices → HackRF One → replace with WinUSB)

See [OPERATIONS.md](docs/OPERATIONS.md) §8 for more.

---

## Run from Your IDE

### VS Code

Open the repo folder. Recommended extensions (C# Dev Kit, ESLint, Prettier) are auto-prompted from [.vscode/extensions.json](.vscode/extensions.json).

Use the **Run and Debug** panel:

- **Full stack: API + Web** — Starts API (with debugger) + Vite dev server → http://localhost:5173
- **API** / **API (live ingestion)** — Backend only (`Ingestion__Enabled=true` for live variant)
- **Web dev server** — Frontend only

Run `web: install` from **Terminal → Run Task** (or `npm install` in `web/`) once before first web launch.

Build/test tasks (`build`, `test (no docker)`, `web: build`) also in Run Task.

### Visual Studio (2022 17.10+ for `.slnx`)

Open **`SignalAtlas.slnx`**. Set **SignalAtlas.Api** as startup project, pick a profile (**API** or **API (live ingestion)**), press F5.

Run the frontend in a terminal:
```bash
cd web && npm install && npm run dev
```

Then browse to http://localhost:5173 (proxies to API on 5285).

---

## Database & Deployment

### With Docker (Postgres/TimescaleDB)

For persistent storage and full edge bundle:

```bash
docker compose up --build      # Timescale + API on named volume
docker compose down            # Stop; data persists in signalatlas-data
```

### Custom Postgres Connection

Set the connection string — API auto-selects EF Core + Postgres when present, in-memory otherwise:

```bash
ConnectionStrings__SignalAtlas="Host=localhost;Database=signalatlas;Username=postgres;Password=..." \
  dotnet run --project src/SignalAtlas.Api
```

See **[OPERATIONS.md](docs/OPERATIONS.md)** for run modes, config keys, secrets, health/metrics, backup/restore, retention, and the failure playbook.

---

## Testing

```bash
# Full suite (minus Docker-dependent lane — runs everywhere):
dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"

# Docker lane (Postgres/Timescale round-trip, hypertables):
dotnet test SignalAtlas.slnx --filter "Category=NeedsDocker"

# Frontend build / type-check:
cd web && npm run build
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs the fast lane, Docker lane, and coverage gate (≥85% line on core + AI projects).

---

## API Overview

REST API is versioned under `/api/v1` with envelope schema `{schemaVersion, correlationId, payload}`, RFC 7807 problem-details, and single-operator authorization.

### Key Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /signals` | List detected RF signals |
| `GET /devices` | Enumerated devices with identity |
| `GET /emitters[/{id}]` | Correlated emitters + geolocation |
| `GET /alerts` | Anomalies and alerts |
| `GET /summary` | Dashboard aggregates |
| `GET /spectrum/frames\|occupancy\|coverage` | Spectral data |
| `POST /analyst/query` | NL analyst queries (M12) |
| `GET /sessions` | Capture sessions |
| `POST /analysis/runs` | Trigger analysis runs |
| `GET /enrichments` | Pending enrichments (accept/reject) |
| `GET /health` · `GET /ready` · `GET /metrics` | Observability |

**Browser WebUSB:** Binary WebSocket `/ingest/iq` (outside `/api/v1`, alongside `/hub/live`) for HackRF IQ ingress.

---

## Architecture

```
src/
  SignalAtlas.Domain          ← Entities, value objects, interfaces (no deps)
  SignalAtlas.Collector       ← Scan collector, sample sources (File/Synthetic/HackRF)
  SignalAtlas.Processing      ← DSP: FFT, PSD, occupancy, features
  SignalAtlas.Classification  ← Rule-based classifier
  SignalAtlas.Ml              ← M9 ML classifier (behind IClassifier)
  SignalAtlas.Decode          ← Protocol decoders + device resolver
  SignalAtlas.Correlation     ← Emitter correlation
  SignalAtlas.Fingerprint     ← M10 RF/PHY fingerprinting + spoof detection
  SignalAtlas.Geospatial      ← Centroid + uncertainty
  SignalAtlas.Behavior        ← Behavior profiles (M6) + prediction (M11)
  SignalAtlas.Anomaly         ← Rule-based anomaly detectors
  SignalAtlas.Analyst         ← M12 dual-mode NL analyst (offline + Claude seam)
  SignalAtlas.Enhancement     ← M13 optional Claude enhancement (cited overlay)
  SignalAtlas.Pipeline        ← Live ingestion orchestrator
  SignalAtlas.Persistence     ← EF Core (Postgres/Timescale) + in-memory repos
  SignalAtlas.Api             ← Minimal API, SignalR hub, auth/observability
web/                          ← React + TS + MUI + MapLibre + Recharts UI
tests/                        ← Unit · Contract · Integration (Docker) · Persistence
```

**Core Principles:**
- ✅ Receive-only
- ✅ Explainable-only (every verdict carries evidence)
- ✅ Offline-first
- ✅ Deterministic core
- ✅ Honest limits (uncertainty always surfaced)
- ✅ Built test-first (RED → GREEN → REFACTOR)

---

## Current Limitations

| Limitation | Status |
|---|---|
| **IQ→bits demodulation** | Pending physical HackRF + field `.iq` fixtures; live mode classifies protocols but defers device determination (except ADS-B aircraft and NOAA APT satellites) |
| **NOAA APT weather-satellite imagery** | Decodes live to greyscale (137 MHz); held in-memory only (never persisted/egressed); georeferenced map placement is Phase 2 |
| **Live Claude client** | Stubbed — analyst cloud mode (M13) passes calls to `IClaudeClient` (needs `SignalAtlas:ClaudeApiKey` + network); offline/deterministic paths fully functional |
| **Multi-node deployment** | Single-node only; basemap via `VITE_BASEMAP_STYLE` + bundled `.pmtiles` (offline graticule is default) |

---

## Documentation

| Document | Purpose |
|---|---|
| [**SPEC.md**](docs/SPEC.md) | Engineering specification (the contract) |
| [**IMPLEMENTATION_PLAN.md**](docs/IMPLEMENTATION_PLAN.md) | Milestones, decisions, TDD approach |
| [**OPERATIONS.md**](docs/OPERATIONS.md) | Deployment, config, health/metrics, backups, runbook |
| [**LICENSE**](LICENSE) | Proprietary license + responsible-use terms |

---

<div align="center">

**Made with ❤️ by Russell Benzing**

[📖 Documentation](docs/SPEC.md) • [🐛 Issues](https://github.com/rbenzing/SignalAtlas/issues) • [💼 License](LICENSE)

</div>
