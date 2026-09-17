# Changelog

All notable changes to Signal Atlas are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Commits follow
[Conventional Commits](https://www.conventionalcommits.org/), so `feat:` lands under Added, `fix:`
under Fixed, and a `!` / `BREAKING CHANGE:` footer under Breaking.

**Versioning note.** The product version lives in two places and they must stay in step:
[`Directory.Build.props`](Directory.Build.props) (`VersionPrefix`, covering every .NET project) and
[`web/package.json`](web/package.json) (npm requires full `x.y.z` semver). Release tags follow the
repo's existing two-part style — `v1.0`, `v2.0`. The REST wire contract is versioned **separately**
via `schemaVersion` in the API envelope and is still `v1`; bump that only when a payload shape
changes in a backward-incompatible way, independently of the product version.

---

## [2.0] — 2026-09-17

Correctness, security and accessibility release. Everything here is a fix; the major bump is driven
solely by the deployment-surface change below.

### Breaking

- **The API container no longer accepts connections from other hosts.** `docker-compose` published
  `8080:8080`, which binds `0.0.0.0`, while `IAuthorizationGate` ships as
  `SingleOperatorPassThroughGate` whose `Authorize()` returns `true` unconditionally — so
  `docker compose up` exposed the entire `/api/v1` surface, plus the un-gated `/ingest/iq` WebSocket
  and `/hub/live`, on every interface with **no authentication**. The published port is now bound to
  `127.0.0.1`.

  *Migration:* a deployment that reached Signal Atlas over the LAN will stop working. That access was
  unauthenticated; restore it behind real authentication (ADR-9 RBAC), **not** by reverting the port
  binding. Also set `POSTGRES_PASSWORD` in your environment or a `.env` file — the compose fallback
  is explicitly local-dev-only.

### Fixed

- **CI: intermittent failure in the non-Docker test lane.** `ws.SendAsync` only completes the
  client-side write while the server consumes config frames on its own receive loop, so
  `IqIngressEndpointTests` had no happens-before edge and bridged it with `Task.Delay(150)`. Under
  CI-like parallelism the one-time `ClearDemoSeed()` could fire *after* the test seeded its data and
  wipe it. Reproduced at 1 failure in 12 runs in a CI replica; fixed by removing the race (the test
  latches the clear itself via `Interlocked`) and polling for state instead of sleeping. Verified
  20/20 green. A test defect, not a product defect.
- **Dashboard reported a known-zero count as unavailable.** `emitters.length || "—"` — `0` is falsy,
  so a successful response of zero emitters rendered as an em-dash. Because demo seeding is off by
  default this was the first screen a new operator saw, and "0 emitters" and "count unavailable"
  are different claims in an RF tool. The same pattern was latent in two other tiles.
- **Baseband filter widths did not match the radio.** The MAX2837 exposes 16 discrete widths and
  libhackrf snaps a request *down* to one of them; the backend used a continuous range check and
  `Math.Clamp`, so widths the hardware cannot select (13 MHz, 2.4 MHz) passed validation and could be
  persisted as `Observation.BandwidthHz` — documented as the true analog passband. Now modelled as a
  supported-set membership test with round-down clamping, matching the browser client.
- **Identifier and location reads were not audit-logged.** `/emitters`, `/emitters/{id}` and
  `/map/heatmap` return identifiers and positions but wrote no audit entry, contrary to NFR-S2 and
  SOC 2 CC7.2.
- **Accessibility:** the app had no live regions at all, so async analyst answers and real-time
  alerts were never announced; connection status carried a prohibited `aria-label` on a bare `div`
  and was invisible to assistive tech; the analyst input had only a placeholder for a name; the nav
  focus ring measured 1.27:1 against 3:1 required; and the disabled RF Map item was unreachable by
  keyboard so its unavailability could not be explained. Stat tiles no longer put bare numbers in the
  heading outline.

### Security

- API container runs as non-root (`USER app`); it previously ran as root while optionally mapping
  `/dev/bus/usb`.
- Database password is sourced from the environment instead of a literal committed to the repo.
- `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` added to API responses.

### Added

- [`Directory.Build.props`](Directory.Build.props) — single source of truth for the product version
  across all .NET projects, which previously inherited the SDK default of `1.0.0`.
- This changelog.
- [`docs/audits/2026-09-16-ci-correctness-audit.md`](docs/audits/2026-09-16-ci-correctness-audit.md)
  — the audit behind this release, including two claims that were made and then **retracted** when
  controlled experiments disproved them, kept deliberately so they are not re-investigated.

### Known gaps (tracked, not addressed here)

Audit actor is the constant `"local-operator"`, so actions are not attributable to an individual
(SOC 2 CC6.1); no TLS; persisted MAC/BSSID/ICAO identifiers are arguably personal data with no
retention policy; NFR-S1 encryption-at-rest is verified nowhere; and CI has no web job, no ESLint and
no accessibility checks — every UI defect in this release was machine-detectable.

---

## [1.0] — 2026-07-25

Initial tagged baseline: SDR capture → classification → device decode → correlation → geolocation →
behaviour/anomaly → map/UI, with the .NET 10 backend and React/TypeScript frontend. Released before
this changelog existed; see the git history and `docs/audits/2026-07-18-rf-platform-audit.md` for the
state at that point.

[2.0]: https://github.com/rbenzing/SignalAtlas/releases/tag/v2.0
[1.0]: https://github.com/rbenzing/SignalAtlas/releases/tag/v1.0
