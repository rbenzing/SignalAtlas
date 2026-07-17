# Signal Atlas — Operations Runbook

Operational guide for the edge node. Pairs with [SPEC.md](SPEC.md) §15 and the review's
operational-readiness findings. The edge runs **fully offline**; Docker/Postgres are optional.

## 1. Run modes

| Mode | How | Storage | Ingestion |
|---|---|---|---|
| Offline dev (default) | `dotnet run --project src/SignalAtlas.Api` | in-memory (seeded) | off |
| Offline + live capture | set `Ingestion__Enabled=true` | in-memory | file/synthetic/HackRF |
| Browser WebUSB HackRF | **Connect HackRF** in the web navbar | in-memory | live, per-connection via `/ingest/iq` |
| Persistent (Docker) | `docker compose up` | PostgreSQL + TimescaleDB | per config |

The API binds to the configured `ASPNETCORE_URLS`. **Bind to localhost by default**; only expose a
non-loopback address after the authorization gate is upgraded from single-operator pass-through
(SPEC §4.7). The auth seam is enforced on every `/api/v1` route (contract-tested).

## 2. Configuration

| Key / env | Purpose | Default |
|---|---|---|
| `ConnectionStrings:SignalAtlas` | Postgres/Timescale connection; unset → in-memory | unset (offline) |
| `Ingestion:Enabled` | run the live ingestion loop on startup | `false` |
| `Ingestion:IqFile` | replay an `.iq` file when no HackRF is present | unset |
| `Ingestion:BoundedCapacity` | bounded backpressure buffer size (drop-oldest) | unset (synchronous) |
| `SignalAtlas:ClaudeApiKey` / `CLAUDE_API_KEY` | optional Claude uplift key (M12/M13) | unset |

**Secrets:** the Claude API key is read via `ISecretProvider` from configuration / env / user-secrets
and is never logged or committed. Use environment variables or a secret store in production — never
put it in `appsettings.json` in the repo.

## 3. Health & observability

- `GET /health` — liveness (process up). Use for container liveness probes.
- `GET /ready` — readiness; verifies the persistence layer is reachable (503 when the DB is down).
  Use for load-balancer / orchestration readiness probes.
- `GET /metrics` — Prometheus exposition (ungated). Series include `signalatlas_http_requests_total`,
  `signalatlas_signals_total`, `signalatlas_devices_total`, `signalatlas_alerts_total`,
  `signalatlas_ingestion_drops_total` (SPEC NFR-R3).
- Every response carries `X-Correlation-ID` (echoed from the request if supplied); it flows through
  the logging scope and into every response envelope and problem-details body (NFR-R2).
- Errors are RFC 7807 `application/problem+json` with `correlationId`; 5xx never leak stack traces.

## 4. Deployment (Docker, optional)

```
docker compose up --build        # brings up Timescale + API on a named volume
docker compose down              # stop; data persists in the signalatlas-data volume
```

The API image runs EF Core `Migrate()` on startup (forward-only migrations) and then configures
TimescaleDB hypertables + retention idempotently (observations 7d, signals 30d). For HackRF USB
inside a container, pass the device through (see the commented `devices:` note in `docker-compose.yml`).

## 5. Backup / restore & data lifecycle

- **Backup:** `pg_dump` the `signalatlas` database on a schedule; the identity stores
  (`devices`, `emitters`, `device_fingerprints`) are the product's memory (SPEC §7.7).
- **Restore drill:** restore into a scratch database and run the API against it before promoting.
- **Retention:** Timescale retention policies drop raw observations after 7d and signals after 30d;
  identity stores persist. Retention does **not** cascade to continuous aggregates — set those
  separately (SPEC §18.4).
- **Encryption at rest:** upstream Postgres has no native TDE; use an encrypted volume (LUKS /
  filesystem encryption) as the baseline (SPEC §4.6). Key custody (LUKS unlock, rotation) is an ops
  responsibility.

## 6. Recovery objectives (initial targets — validate under load)

| Objective | Target | Notes |
|---|---|---|
| RPO | ≤ backup interval (e.g. 24h identity stores) | measurement data is time-bounded by retention |
| RTO | ≤ 30 min (restore + restart) | single-node edge; no HA in this version |
| SDR disconnect | auto-reconnect, lose ≤ in-flight block (NFR-R1) | bounded exp backoff, health events emitted |

## 7. Failure playbook

- **SDR USB drop:** the source auto-reconnects (bounded backoff, 5 attempts) and emits health events;
  after exhaustion it stops gracefully. Check logs for `SdrHealthState=Failed`; re-seat the device and
  restart ingestion.
- **Ingestion overload:** if `signalatlas_ingestion_drops_total` climbs, raise `Ingestion:BoundedCapacity`
  or reduce scan bands (graceful-degrade, SPEC §4.8). Drops are counted, never silent.
- **DB unreachable:** `/ready` returns 503; the edge keeps collecting to the in-memory path if started
  without a connection string. Investigate the DB/volume; restart once `/ready` clears.
- **Rate limiting:** clients exceeding 100 req/s get 429 problem-details; tune the limiter for trusted LANs.

## 8. Browser WebUSB HackRF ingress

An operator can capture from a **HackRF One attached to the machine running the browser**, with
no server-side SDR. In the web UI navbar, **Connect HackRF** triggers the browser's WebUSB
permission prompt; the browser streams interleaved signed-8-bit IQ to the API over a binary
WebSocket at **`/ingest/iq`**, which feeds the same ingestion pipeline (classify → correlate →
anomaly) and live SignalR push. The protocol is a JSON `config` text frame (center/rate/
`samplesPerBlock`/collectorId) followed by binary IQ frames; a re-sent config frame retunes.

- **Browser:** Chrome or Edge (WebUSB). Firefox/Safari are unsupported.
- **Windows driver:** install WinUSB for the HackRF via [Zadig](https://zadig.akeo.ie/)
  (Options → List All Devices → HackRF One → replace with WinUSB). Without it the device shows
  as a COM port and the chooser is empty. Close other SDR software (SDR#, GNU Radio) first — the
  USB interface can only be claimed once.
- **Endpoint posture:** `/ingest/iq` is intentionally **un-gated** (like `/hub/live`), consistent
  with the single-operator loopback posture — do **not** expose it on a non-loopback address until
  the auth seam is upgraded (SPEC §4.7).
- **Receive-only & data:** the browser driver has no transmit path (asserted by tests on both ends);
  raw IQ is consumed into spectra/features and never persisted or forwarded. Backpressure is
  drop-oldest on both ends (the browser drops when the socket buffer backs up; the backend channel
  is bounded drop-oldest).
- **Tuning:** center frequency, sample rate, and gains are set from the navbar popover (defaults
  915 MHz, 2 MS/s, LNA 16 dB, VGA 20 dB, amp off). A previously-authorized device auto-reconnects
  on page load without re-prompting.
- **Scope:** like other live modes, this determines protocols but **not devices** (IQ→bits
  demodulation deferred — see §9).

## 9. Known limits (this version)

- IQ→bits demodulation front-end is deferred (needs field `.iq` fixtures); live mode determines
  protocols but not devices until the demodulators land.
- Single-node only; multi-sensor fusion and RBAC are seamed but deferred (ADR-9).
- Encryption-at-rest and Timescale hypertable behavior are verified only in the Docker CI lane.
