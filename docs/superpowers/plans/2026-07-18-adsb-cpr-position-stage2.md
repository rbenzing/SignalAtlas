# Live ADS-B — Stage 2 (CPR Position → Aircraft on the Map) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decode ADS-B airborne-position (CPR) frames so aircraft get lat/lon/altitude and plot as a distinct layer on the RF map.

**Architecture:** `AdsBDecoder` gains TC 9-18 decode but stays pure (raw CPR on a new typed `DecodedFrame.Cpr` field). A new stateful `CprPositionResolver` does global (even/odd) CPR math. `Device` gains nullable position; the pipeline stamps it; the map renders an aircraft layer from `/devices`.

**Tech Stack:** .NET 10, C#, xUnit; EF Core (SQLite tests / Postgres prod); React + TypeScript + MapLibre GL; Vitest.

## Global Constraints

- **Backend + one frontend map layer.** No new `/api/v1` routes (reuse `/devices`, `/emitters`).
- **Receive-only (L1):** decode/CPR only read frames; never add a transmit path.
- **Metadata, not content (L2/L3):** parse only ICAO, callsign, self-reported position/altitude; no payload.
- **Deterministic core (P5):** in `src/`, no `DateTime.Now`/`Guid.NewGuid()`/unseeded `Random`. The decoder is pure; the resolver uses injected timestamps (`IClock`-sourced). Test code may use seeded RNG / fixed clocks.
- **Explainable-only (P4/P6):** determined devices keep non-empty evidence.
- **Raw CPR lives ONLY on the typed `DecodedFrame.Cpr` field** — never in a frame's Identifiers or Evidence. (Per-frame CPR/altitude values vary constantly; `DeviceMerge` unions identifiers+evidence, so putting varying values there would grow a moving aircraft's device unbounded.) Frame evidence for a position frame is one BOUNDED item only.
- **Protocol string is exactly `"ADS-B"`.**
- **Wire contract is camelCase** — `Device.Latitude` → `latitude`, etc. Curl the real `/devices` payload before wiring the map (landmine #1).
- **Canonical CPR test vector (junzis worked example), used verbatim:**
  - Even frame hex `8D40621D58C382D690C8AC2863A7` → `Odd=false`, `CprLat17=93000`, `CprLon17=51372`, `AltitudeFt=38000`.
  - Odd frame hex `8D40621D58C386435CC412692AD6` → `Odd=true`, `CprLat17=74158`, `CprLon17=50194`, `AltitudeFt=38000`.
  - Global decode with the **even frame as the most-recent reference** → latitude `52.2572`, longitude `3.91937`.
- **Verify each task:** `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"` and `dotnet build SignalAtlas.slnx` (backend); `cd web && npm run test && npm run build` (frontend). NU1903 SQLite advisory is a known pre-existing warning.

## File Structure

- **Modify** `src/SignalAtlas.Domain/Decode.cs` — add `CprPosition` record + optional `DecodedFrame.Cpr` field. (Task 1)
- **Modify** `src/SignalAtlas.Decode/Decoders/AdsBDecoder.cs` — TC 9-18 decode. (Task 1)
- **Create** `src/SignalAtlas.Domain/CprPosition*`… — `GeoPosition` record + `ICprPositionResolver` (in `Decode.cs` or a new Domain file). (Task 2)
- **Create** `src/SignalAtlas.Decode/CprPositionResolver.cs` — global CPR + pairing cache. (Task 2)
- **Modify** `src/SignalAtlas.Domain/Decode.cs` (`Device`) + `src/SignalAtlas.Persistence/DeviceMerge.cs` — nullable position + coalesce. (Task 3)
- **Modify** `src/SignalAtlas.Pipeline/IngestionPipeline.cs` + `Program.cs` + `IqIngressEndpoint.cs` + `PipelineHostedService.cs` — inject resolver, stamp position, register/ wire. (Task 4)
- **Modify** `web/src/api.ts`, `web/src/lib/rfmap.ts`, `web/src/views/RfMap.tsx` — aircraft layer. (Task 5)
- **Tests:** `tests/SignalAtlas.Tests.Unit/DecodeAdsbTests.cs` (T1), `CprPositionResolverTests.cs` (T2), `IngestionPipelineDecodeTests.cs` (T4); `tests/SignalAtlas.Tests.Persistence/{InMemoryDeviceRepositoryTests,WritePathRoundTripTests}.cs` (T3); `web/src/lib/rfmap.test.ts` (T5).

---

### Task 1: `AdsBDecoder` airborne-position decode (TC 9-18)

**Files:**
- Modify: `src/SignalAtlas.Domain/Decode.cs`
- Modify: `src/SignalAtlas.Decode/Decoders/AdsBDecoder.cs`
- Test: `tests/SignalAtlas.Tests.Unit/DecodeAdsbTests.cs`

**Interfaces:**
- Produces: `record CprPosition(bool Odd, int CprLat17, int CprLon17, int AltitudeFt)`; `DecodedFrame` gains a trailing optional `CprPosition? Cpr = null`. `AdsBDecoder.Decode` sets `Cpr` + `FrameType="airborne_position"` for TC 9-18.

- [ ] **Step 1: Write the failing test**

Add to `tests/SignalAtlas.Tests.Unit/DecodeAdsbTests.cs` (the `Hex` helper already exists there):

```csharp
    [Fact]
    public void Decode_AirbornePositionEven_ExtractsCprAndAltitude()
    {
        var outcome = Decoder.Decode(Hex("8D40621D58C382D690C8AC2863A7"));

        Assert.True(outcome.Success);
        Assert.Equal("airborne_position", outcome.Frame!.FrameType);
        Assert.Equal("40621D", outcome.Frame.Identifiers["icao"]);
        Assert.NotNull(outcome.Frame.Cpr);
        Assert.False(outcome.Frame.Cpr!.Odd);
        Assert.Equal(93000, outcome.Frame.Cpr.CprLat17);
        Assert.Equal(51372, outcome.Frame.Cpr.CprLon17);
        Assert.Equal(38000, outcome.Frame.Cpr.AltitudeFt);
    }

    [Fact]
    public void Decode_AirbornePositionOdd_ExtractsCpr()
    {
        var outcome = Decoder.Decode(Hex("8D40621D58C386435CC412692AD6"));

        Assert.True(outcome.Success);
        Assert.True(outcome.Frame!.Cpr!.Odd);
        Assert.Equal(74158, outcome.Frame.Cpr.CprLat17);
        Assert.Equal(50194, outcome.Frame.Cpr.CprLon17);
    }

    [Fact]
    public void Decode_IdentificationFrame_HasNoCprPayload()
    {
        var outcome = Decoder.Decode(Hex("8D4840D6202CC371C32CE0576098")); // TC 4 callsign
        Assert.True(outcome.Success);
        Assert.Null(outcome.Frame!.Cpr);
        Assert.Equal("extended_squitter", outcome.Frame.FrameType);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~DecodeAdsbTests"`
Expected: FAIL to COMPILE — `DecodedFrame.Cpr` / `CprPosition` don't exist.

- [ ] **Step 3: Add the domain types**

In `src/SignalAtlas.Domain/Decode.cs`, add the `CprPosition` record (near `DecodedFrame`) and a trailing optional field on `DecodedFrame`:

```csharp
/// <summary>
/// Raw airborne-position payload from an ADS-B extended squitter (TC 9-18): the CPR format bit,
/// the two 17-bit compact-position values, and barometric altitude (ft). Carried on the frame for
/// the position resolver to consume — NEVER merged into a Device's identifiers/evidence (these
/// values change every frame). Null for non-position frames.
/// </summary>
public sealed record CprPosition(bool Odd, int CprLat17, int CprLon17, int AltitudeFt);
```

Change the `DecodedFrame` record to add the optional field (keep the existing members, add the last one):

```csharp
public sealed record DecodedFrame(
    string Protocol,
    string FrameType,
    IReadOnlyDictionary<string, string> Identifiers,
    double DecodeQuality,
    IReadOnlyList<EvidenceItem> Evidence,
    CprPosition? Cpr = null);
```

(The default `= null` keeps every existing `new DecodedFrame(...)` in the other decoders compiling.)

- [ ] **Step 4: Decode TC 9-18 in `AdsBDecoder`**

In `src/SignalAtlas.Decode/Decoders/AdsBDecoder.cs`, replace the block from the callsign `if` through the `return` with position handling added, and add three private helpers. Concretely, the tail of `Decode` (after `int typeCode = msg[4] >> 3;` and the `identifiers`/`evidence` setup) becomes:

```csharp
        string frameType = "extended_squitter";
        CprPosition? cpr = null;

        // TC 1..4 = airborne identification: callsign lives in the ME field (bytes 5..10, 48 bits).
        if (typeCode is >= 1 and <= 4)
        {
            string callsign = DecodeCallsign(msg.Slice(5, 6));
            if (callsign.Length > 0)
            {
                identifiers["callsign"] = callsign;
                evidence.Add(new EvidenceItem("callsign", callsign, 1.0));
            }
        }
        // TC 9..18 = barometric airborne position: CPR lat/lon + altitude.
        else if (typeCode is >= 9 and <= 18)
        {
            frameType = "airborne_position";
            bool odd = (msg[6] & 0x04) != 0;               // ME bit 21 (F format bit)
            int latCpr = ReadBits(msg, 54, 17);            // ME bits 22..38
            int lonCpr = ReadBits(msg, 71, 17);            // ME bits 39..55
            int altFt = DecodeAltitude(ReadBits(msg, 40, 12)); // ME bits 8..19 (12-bit AC field)
            cpr = new CprPosition(odd, latCpr, lonCpr, altFt);
            // ONE bounded evidence item only (frame type) — never the varying CPR/altitude values.
            evidence.Add(new EvidenceItem("adsb_frame", "airborne_position", 1.0));
        }

        var frame = new DecodedFrame("ADS-B", frameType, identifiers, 1.0, evidence, cpr);
        return DecodeOutcome.Decoded(frame);
```

Add these private helpers to the class (near `DecodeCallsign`):

```csharp
    /// <summary>Reads <paramref name="count"/> big-endian bits starting at absolute bit
    /// <paramref name="startBit"/> (bit 0 = MSB of byte 0) into an int.</summary>
    private static int ReadBits(ReadOnlySpan<byte> msg, int startBit, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = startBit + i;
            int b = (msg[bit >> 3] >> (7 - (bit & 7))) & 1;
            value = (value << 1) | b;
        }
        return value;
    }

    /// <summary>Decodes the 12-bit AC altitude field to feet. Q-bit set → 25 ft increments
    /// (ADS-B airborne standard); Q clear (legacy 100 ft Gillham) is not decoded in v1 → 0.</summary>
    private static int DecodeAltitude(int ac12)
    {
        if (ac12 == 0) return 0;                 // altitude unavailable
        if ((ac12 & 0x10) == 0) return 0;        // Q=0 (Gillham) — out of v1 scope
        int n = ((ac12 & 0x0FE0) >> 1) | (ac12 & 0x000F);
        return n * 25 - 1000;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~DecodeAdsbTests"`
Expected: PASS (existing callsign/CRC tests + the 3 new ones).

- [ ] **Step 6: Commit**

```bash
git add src/SignalAtlas.Domain/Decode.cs src/SignalAtlas.Decode/Decoders/AdsBDecoder.cs tests/SignalAtlas.Tests.Unit/DecodeAdsbTests.cs
git commit -m "feat(decode): ADS-B airborne-position (TC 9-18) CPR + altitude extraction

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: `CprPositionResolver` (global CPR + pairing cache)

**Files:**
- Modify: `src/SignalAtlas.Domain/Decode.cs` (add `GeoPosition` + `ICprPositionResolver`)
- Create: `src/SignalAtlas.Decode/CprPositionResolver.cs`
- Test: `tests/SignalAtlas.Tests.Unit/CprPositionResolverTests.cs`

**Interfaces:**
- Produces: `record GeoPosition(double Latitude, double Longitude)`; `interface ICprPositionResolver { GeoPosition? Accept(string icao, bool odd, int cprLat17, int cprLon17, DateTimeOffset time); }`; `class CprPositionResolver : ICprPositionResolver`.

- [ ] **Step 1: Write the failing test**

Create `tests/SignalAtlas.Tests.Unit/CprPositionResolverTests.cs`:

```csharp
using SignalAtlas.Decode;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class CprPositionResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    // junzis canonical vector: even (93000,51372) + odd (74158,50194), even as most-recent
    // reference → 52.2572, 3.91937.
    private const int EvenLat = 93000, EvenLon = 51372, OddLat = 74158, OddLon = 50194;

    [Fact]
    public void Accept_SingleFrame_ReturnsNull()
    {
        var r = new CprPositionResolver();
        Assert.Null(r.Accept("40621D", odd: false, EvenLat, EvenLon, T0));
    }

    [Fact]
    public void Accept_OddThenEven_DecodesCanonicalPosition()
    {
        var r = new CprPositionResolver();
        Assert.Null(r.Accept("40621D", odd: true, OddLat, OddLon, T0));
        var pos = r.Accept("40621D", odd: false, EvenLat, EvenLon, T0.AddSeconds(1)); // even most recent

        Assert.NotNull(pos);
        Assert.Equal(52.2572, pos!.Latitude, 3);   // 3 decimal places
        Assert.Equal(3.91937, pos.Longitude, 3);
    }

    [Fact]
    public void Accept_EvenThenOdd_AlsoResolves()
    {
        var r = new CprPositionResolver();
        Assert.Null(r.Accept("40621D", odd: false, EvenLat, EvenLon, T0));
        var pos = r.Accept("40621D", odd: true, OddLat, OddLon, T0.AddSeconds(1));
        Assert.NotNull(pos); // odd-as-reference position (near the same place)
        Assert.Equal(52.26, pos!.Latitude, 1);
    }

    [Fact]
    public void Accept_StalePartner_ReturnsNull()
    {
        var r = new CprPositionResolver();
        r.Accept("40621D", odd: true, OddLat, OddLon, T0);
        // Even arrives 11 s later — past the 10 s pairing window.
        Assert.Null(r.Accept("40621D", odd: false, EvenLat, EvenLon, T0.AddSeconds(11)));
    }

    [Fact]
    public void Accept_DifferentIcaos_DoNotCrossContaminate()
    {
        var r = new CprPositionResolver();
        r.Accept("AAAAAA", odd: true, OddLat, OddLon, T0);
        // A different ICAO's even frame must not pair with AAAAAA's odd frame.
        Assert.Null(r.Accept("BBBBBB", odd: false, EvenLat, EvenLon, T0.AddSeconds(1)));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~CprPositionResolverTests"`
Expected: FAIL to COMPILE — `CprPositionResolver`/`GeoPosition` don't exist.

- [ ] **Step 3: Add the domain types**

In `src/SignalAtlas.Domain/Decode.cs` add:

```csharp
/// <summary>A decoded geographic position (WGS-84 degrees). Aircraft self-reported location (L2).</summary>
public sealed record GeoPosition(double Latitude, double Longitude);

/// <summary>
/// Resolves ADS-B airborne CPR frames into an absolute position via global (even/odd) decoding.
/// Stateful: caches the last even + last odd frame per ICAO and returns a fix once it holds a
/// consistent pair within the pairing window. Deterministic — position depends only on the frame
/// values and the caller-supplied timestamps (IClock-sourced), never a wall clock.
/// </summary>
public interface ICprPositionResolver
{
    GeoPosition? Accept(string icao, bool odd, int cprLat17, int cprLon17, DateTimeOffset time);
}
```

- [ ] **Step 4: Implement the resolver**

Create `src/SignalAtlas.Decode/CprPositionResolver.cs`:

```csharp
using SignalAtlas.Domain;

namespace SignalAtlas.Decode;

/// <summary>
/// Global (even/odd) airborne CPR decoder with a bounded per-ICAO frame cache (SPEC §8.4 Stage 2).
/// The math is the standard RTCA DO-260 global airborne algorithm; the NL(lat) longitude-zone check
/// rejects a pair that straddles a latitude zone. Thread-safe (single lock); deterministic.
/// </summary>
public sealed class CprPositionResolver : ICprPositionResolver
{
    private static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(10);
    private const int MaxAircraft = 4096;
    private const double Two17 = 131072.0; // 2^17

    private readonly record struct Frame(int Lat, int Lon, DateTimeOffset Time);

    private readonly object _sync = new();
    private readonly Dictionary<string, (Frame? Even, Frame? Odd)> _cache = new(StringComparer.Ordinal);

    public GeoPosition? Accept(string icao, bool odd, int cprLat17, int cprLon17, DateTimeOffset time)
    {
        lock (_sync)
        {
            _cache.TryGetValue(icao, out var pair);
            var f = new Frame(cprLat17, cprLon17, time);
            pair = odd ? (pair.Even, f) : (f, pair.Odd);
            _cache[icao] = pair;

            if (_cache.Count > MaxAircraft)
                Evict(time);

            if (pair.Even is not { } e || pair.Odd is not { } o)
                return null;
            if (time - e.Time > PairWindow || time - o.Time > PairWindow)
                return null;

            bool useOdd = o.Time >= e.Time; // most-recent frame is the reference
            return GlobalDecode(e, o, useOdd);
        }
    }

    private static GeoPosition? GlobalDecode(Frame even, Frame odd, bool useOdd)
    {
        double latCprE = even.Lat / Two17, latCprO = odd.Lat / Two17;
        double lonCprE = even.Lon / Two17, lonCprO = odd.Lon / Two17;

        int j = (int)Math.Floor(59.0 * latCprE - 60.0 * latCprO + 0.5);
        double latE = (360.0 / 60.0) * (Mod(j, 60) + latCprE);
        double latO = (360.0 / 59.0) * (Mod(j, 59) + latCprO);
        if (latE >= 270.0) latE -= 360.0;
        if (latO >= 270.0) latO -= 360.0;

        if (Nl(latE) != Nl(latO)) return null; // pair straddles a latitude zone — await a fresh pair

        double lat = useOdd ? latO : latE;
        int nl = Nl(lat);
        int ni = Math.Max(nl - (useOdd ? 1 : 0), 1);
        double m = Math.Floor(lonCprE * (nl - 1) - lonCprO * nl + 0.5);
        double lonCpr = useOdd ? lonCprO : lonCprE;
        double lon = (360.0 / ni) * (Mod(m, ni) + lonCpr);
        if (lon >= 180.0) lon -= 360.0;

        return new GeoPosition(lat, lon);
    }

    /// <summary>Number of longitude zones at a latitude (RTCA NL table, via the closed form).</summary>
    private static int Nl(double lat)
    {
        double absLat = Math.Abs(lat);
        if (absLat >= 87.0) return 1;
        const double nz = 15.0;
        double a = 1.0 - Math.Cos(Math.PI / (2.0 * nz));
        double cosLat = Math.Cos(Math.PI * absLat / 180.0);
        double x = a / (cosLat * cosLat);
        return (int)Math.Floor(2.0 * Math.PI / Math.Acos(1.0 - x));
    }

    private static double Mod(double a, double b) => a - b * Math.Floor(a / b);

    private void Evict(DateTimeOffset now)
    {
        foreach (var key in _cache.Where(kv =>
                     (kv.Value.Even is not { } e || now - e.Time > PairWindow) &&
                     (kv.Value.Odd is not { } o || now - o.Time > PairWindow))
                 .Select(kv => kv.Key).ToList())
            _cache.Remove(key);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~CprPositionResolverTests"`
Expected: PASS (5 tests). If the canonical position assertion fails on the real math (not a compile error), STOP and report the computed lat/lon rather than mutating tolerances — the algorithm must hit the known vector.

- [ ] **Step 6: Commit**

```bash
git add src/SignalAtlas.Domain/Decode.cs src/SignalAtlas.Decode/CprPositionResolver.cs tests/SignalAtlas.Tests.Unit/CprPositionResolverTests.cs
git commit -m "feat(decode): global CPR position resolver (even/odd pairing)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: `Device` position fields + `DeviceMerge` coalesce + persistence

**Files:**
- Modify: `src/SignalAtlas.Domain/Decode.cs` (`Device`)
- Modify: `src/SignalAtlas.Persistence/DeviceMerge.cs`
- Test: `tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs`, `tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs`

**Interfaces:**
- Produces: `Device` gains trailing optional `double? Latitude = null, double? Longitude = null, int? AltitudeFt = null`. `DeviceMerge.Merge` coalesces them (`incoming ?? existing`).

- [ ] **Step 1: Write the failing test**

Add to `tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs` (uses the file's existing `New()`/`Aircraft` helpers):

```csharp
    private static Device AircraftAt(string icao, double lat, double lon, int altFt) =>
        new(icao, "Aircraft", icao, new Dictionary<string, string> { ["icao"] = icao },
            null, "ADS-B", 1.0, [new EvidenceItem("icao", icao, 1.0)], lat, lon, altFt);

    [Fact]
    public void Upsert_PositionThenIdentityOnly_RetainsPosition()
    {
        var repo = New();
        repo.Upsert(AircraftAt("4840D6", 52.2572, 3.91937, 38000)); // position fix
        repo.Upsert(Aircraft("4840D6", "KLM1023"));                 // later identity frame, no position

        var got = Assert.Single(repo.GetDevices());
        Assert.Equal(52.2572, got.Latitude);        // position survives
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
    }
```

Add to `tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs` (the `SampleDevice` helper exists; extend it or add a positioned variant):

```csharp
    [Fact]
    public void Device_Upsert_RoundTripsPosition()
    {
        var d = new Device("4840D6", "Aircraft", "4840D6",
            new Dictionary<string, string> { ["icao"] = "4840D6" }, null, "ADS-B", 1.0,
            [new EvidenceItem("icao", "4840D6", 1.0)], 52.2572, 3.91937, 38000);
        new EfDeviceRepository(NewContext()).Upsert(d);

        var got = Assert.Single(new EfDeviceRepository(NewContext()).GetDevices());
        Assert.Equal(52.2572, got.Latitude);
        Assert.Equal(3.91937, got.Longitude);
        Assert.Equal(38000, got.AltitudeFt);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Persistence --filter "FullyQualifiedName~InMemoryDeviceRepositoryTests|FullyQualifiedName~WritePathRoundTripTests"`
Expected: FAIL to COMPILE — `Device` has no `Latitude`/`Longitude`/`AltitudeFt`.

- [ ] **Step 3: Add the position fields to `Device`**

In `src/SignalAtlas.Domain/Decode.cs`, change the `Device` record to add three trailing optional members (keep all existing members):

```csharp
public sealed record Device(
    string Id,
    string DeviceType,
    string? PrimaryIdentifier,
    IReadOnlyDictionary<string, string> Identifiers,
    string? Vendor,
    string Protocol,
    double Confidence,
    IReadOnlyList<EvidenceItem> Evidence,
    double? Latitude = null,
    double? Longitude = null,
    int? AltitudeFt = null);
```

(The `= null` defaults keep every existing `new Device(...)` and `DeviceResolver.Resolve` compiling — only position-stamping code sets them.) EF maps the three nullable scalars automatically; no `DbContext` change and no converter are needed. (The SQLite test DB is built via `EnsureCreated()` from the current model, so the round-trip test picks the columns up with no migration. A Postgres EF migration is a deploy-time follow-up — the non-Docker suite does not depend on it.)

- [ ] **Step 4: Coalesce position in `DeviceMerge`**

In `src/SignalAtlas.Persistence/DeviceMerge.cs`, extend the returned record so position fields fall back to the existing device when the incoming one lacks them (add to the `incoming with { ... }` block):

```csharp
        return incoming with
        {
            Identifiers = identifiers,
            Evidence = evidence,
            PrimaryIdentifier = incoming.PrimaryIdentifier ?? existing.PrimaryIdentifier,
            Vendor = incoming.Vendor ?? existing.Vendor,
            Latitude = incoming.Latitude ?? existing.Latitude,
            Longitude = incoming.Longitude ?? existing.Longitude,
            AltitudeFt = incoming.AltitudeFt ?? existing.AltitudeFt,
        };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SignalAtlas.Tests.Persistence --filter "FullyQualifiedName~InMemoryDeviceRepositoryTests|FullyQualifiedName~WritePathRoundTripTests"`
Expected: PASS (existing device tests + the 2 new position ones).

- [ ] **Step 6: Commit**

```bash
git add src/SignalAtlas.Domain/Decode.cs src/SignalAtlas.Persistence/DeviceMerge.cs tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs
git commit -m "feat(persistence): Device self-reported position + coalescing merge

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Pipeline position stamping + DI wiring

**Files:**
- Modify: `src/SignalAtlas.Pipeline/IngestionPipeline.cs`
- Modify: `src/SignalAtlas.Api/Program.cs`, `src/SignalAtlas.Api/IqIngressEndpoint.cs`, `src/SignalAtlas.Api/PipelineHostedService.cs`
- Test: `tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs`

**Interfaces:**
- Consumes: `DecodedFrame.Cpr` (T1), `ICprPositionResolver`/`CprPositionResolver` (T2), `Device.Latitude/Longitude/AltitudeFt` (T3), the Stage-1 `AdsBModulator`.
- Produces: `IngestionPipeline` gains a 5th optional decode dep `ICprPositionResolver? cpr = null`; the decode stage stamps device position when the resolver returns a fix.

- [ ] **Step 1: Write the failing test**

Add to `tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs` (reuses `OneBlockSource`, `CapturingDeviceRepo`, `CapturingNotifier`, `NoopObs`, `NoopSignals`, `Hex` from the existing file; add `CprPositionResolver` to the `Build` deps):

```csharp
    private static IngestionPipeline BuildWithCpr(CapturingDeviceRepo devices, CapturingNotifier notifier) =>
        new(
            collectorId: "adsb-test",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new NoopObs(),
            signals: new NoopSignals(),
            notifier: notifier,
            demodulators: [new AdsBDemodulator()],
            registry: new DecoderRegistry([new AdsBDecoder()]),
            resolver: new DeviceResolver(new OuiLookup()),
            devices: devices,
            cpr: new CprPositionResolver());

    [Fact]
    public void Run_AirbornePositionPair_StampsAircraftPosition()
    {
        // Even + odd canonical frames for ICAO 40621D → a fix near (52.26, 3.92).
        // Default lead/trail slots (4/4) — same pattern as the Stage-1 two-frame test.
        var even = AdsBModulator.Modulate(Hex("8D40621D58C382D690C8AC2863A7"));
        var odd = AdsBModulator.Modulate(Hex("8D40621D58C386435CC412692AD6"));
        var i = even.I.Concat(odd.I).ToArray();
        var q = even.Q.Concat(odd.Q).ToArray();
        var block = new IqBlock(1_090_000_000, 2_000_000, i, q);
        var devices = new CapturingDeviceRepo();

        BuildWithCpr(devices, new CapturingNotifier()).Run(new OneBlockSource(block));

        var d = Assert.Single(devices.Upserts);
        Assert.Equal("40621D", d.Identifiers["icao"]);
        Assert.NotNull(d.Latitude);
        Assert.InRange(d.Latitude!.Value, 52.2, 52.3);
        Assert.InRange(d.Longitude!.Value, 3.85, 3.95);
        Assert.Equal(38000, d.AltitudeFt);
    }
```

> The two 14-byte frames modulate to 248-slot blocks each (default `leadSlots:4, trailSlots:4`); concatenating their I/Q keeps both frames separable for the demod, exactly like the Stage-1 `Demodulate_TwoFramesInOneBlock` test. The only requirement is that one even and one odd frame are present in the block.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~IngestionPipelineDecodeTests"`
Expected: FAIL to COMPILE — `IngestionPipeline` has no `cpr` param.

- [ ] **Step 3: Add the resolver dep + position stamping to the pipeline**

In `src/SignalAtlas.Pipeline/IngestionPipeline.cs`:

Add a field after `_devices`:
```csharp
    private readonly ICprPositionResolver? _cpr;
```
Add the optional param at the END of the constructor parameter list (after `IDeviceRepository? devices = null`):
```csharp
        IDeviceRepository? devices = null,
        ICprPositionResolver? cpr = null)
```
Assign it in the constructor body (after `_devices = devices;`):
```csharp
        _cpr = cpr;
```

In the step-4b decode stage, inside the `foreach (var group in decoded.GroupBy(PrimaryKey))` loop, after `var device = _resolver.Resolve(group.ToList());` and the `if (device is not null)` guard, stamp position from any airborne-position frames BEFORE the `_devices.Upsert(device)` call. Replace the body of the `if (device is not null)` block with:

```csharp
                    if (device is not null)
                    {
                        // Stamp self-reported position from any airborne-position frame in this
                        // ICAO group (CPR resolved against the even/odd pairing cache).
                        if (_cpr is not null)
                        {
                            foreach (var f in group)
                            {
                                if (f.Cpr is not { } c) continue;
                                var pos = _cpr.Accept(group.Key.StartsWith("icao:") ? group.Key[5..] : group.Key,
                                    c.Odd, c.CprLat17, c.CprLon17, obs.Time);
                                device = device with { AltitudeFt = c.AltitudeFt };
                                if (pos is not null)
                                    device = device with { Latitude = pos.Latitude, Longitude = pos.Longitude };
                            }
                        }
                        _devices.Upsert(device);
                        _notifier.DeviceDetermined(device);
                    }
```

> `group.Key` is the `PrimaryKey(frame)` result, formatted `"icao:<value>"` for ADS-B (see the Stage-1 `PrimaryKey` helper). The `StartsWith("icao:")` strip recovers the bare ICAO the resolver keys on. `obs.Time` is the block's `IClock`-sourced timestamp (deterministic).

- [ ] **Step 4: Register + wire the resolver (DI)**

In `src/SignalAtlas.Api/Program.cs`, register the resolver as a singleton next to the Stage-1 decode registrations (after `AddSingleton<IDemodulator, AdsBDemodulator>();`):
```csharp
builder.Services.AddSingleton<ICprPositionResolver, CprPositionResolver>();
```
In BOTH `src/SignalAtlas.Api/IqIngressEndpoint.cs` (`BuildPipeline`) and `src/SignalAtlas.Api/PipelineHostedService.cs`, add the 5th decode dep to the `new IngestionPipeline(...)` call, after `devices: sp.GetService<IDeviceRepository>()`:
```csharp
            devices: sp.GetService<IDeviceRepository>(),
            cpr: sp.GetService<ICprPositionResolver>());
```

- [ ] **Step 5: Run the tests + full non-Docker suite**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~IngestionPipelineDecodeTests"`
Expected: PASS (existing decode tests + the new position-stamping one).

Run: `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"`
Expected: PASS across the whole suite (no regressions).

- [ ] **Step 6: Commit**

```bash
git add src/SignalAtlas.Pipeline/IngestionPipeline.cs src/SignalAtlas.Api/Program.cs src/SignalAtlas.Api/IqIngressEndpoint.cs src/SignalAtlas.Api/PipelineHostedService.cs tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs
git commit -m "feat(pipeline): stamp aircraft CPR position onto determined devices

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Map aircraft layer (frontend)

**Files:**
- Modify: `web/src/api.ts` (Device type)
- Modify: `web/src/lib/rfmap.ts` (aircraft source/layer/GeoJSON)
- Modify: `web/src/views/RfMap.tsx` (poll `/devices`, render + select aircraft)
- Test: `web/src/lib/rfmap.test.ts`

**Interfaces:**
- Consumes: `/devices` payload now carrying `latitude`/`longitude`/`altitudeFt` (Task 3/4).
- Produces: `aircraftToGeoJSON(devices)`; `AIRCRAFT_SOURCE`, `AIRCRAFT_POINT_LAYER`, `aircraftLayer(mode)`.

- [ ] **Step 1: Verify the live payload shape**

With the API running, confirm the field names (landmine #1):
Run: `curl -s http://localhost:5285/api/v1/devices | head -c 600`
Expected: device objects include `"latitude"`, `"longitude"`, `"altitudeFt"` (null for non-aircraft). (If the API isn't running, this is confirmed by Task 3's camelCase serialization; proceed.)

- [ ] **Step 2: Add position to the frontend `Device` type**

In `web/src/api.ts`, add three fields to the existing `Device` interface (keep the others):
```typescript
  latitude: number | null;
  longitude: number | null;
  altitudeFt: number | null;
```

- [ ] **Step 3: Write the failing test**

Add to `web/src/lib/rfmap.test.ts` (create if absent; import the existing `Device` type):

```typescript
import { describe, it, expect } from "vitest";
import { aircraftToGeoJSON } from "./rfmap";
import type { Device } from "../api";

function device(over: Partial<Device>): Device {
  return {
    id: "40621D", deviceType: "Aircraft", primaryIdentifier: "40621D",
    identifiers: { icao: "40621D" }, vendor: null, protocol: "ADS-B",
    confidence: 1, evidence: [], latitude: null, longitude: null, altitudeFt: null,
    ...over,
  } as Device;
}

describe("aircraftToGeoJSON", () => {
  it("includes only devices with a position fix", () => {
    const fc = aircraftToGeoJSON([
      device({ id: "A", latitude: 52.2572, longitude: 3.91937, altitudeFt: 38000 }),
      device({ id: "B", latitude: null, longitude: null }), // no fix → excluded
    ]);
    expect(fc.features).toHaveLength(1);
    expect(fc.features[0].properties.id).toBe("A");
    expect(fc.features[0].geometry.coordinates).toEqual([3.91937, 52.2572]);
  });
});
```

- [ ] **Step 4: Run it to verify it fails**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/lib/rfmap.test.ts`
Expected: FAIL — `aircraftToGeoJSON` is not exported.

- [ ] **Step 5: Add the aircraft source/layer/GeoJSON to `rfmap.ts`**

In `web/src/lib/rfmap.ts`, add near the other exports (and `import type { Device } from "../api";` — extend the existing api import):

```typescript
export const AIRCRAFT_SOURCE = "aircraft";
export const AIRCRAFT_POINT_LAYER = "aircraft-points";

/** Distinct aircraft marker color (amber) — deliberately NOT a protocol color. */
const AIRCRAFT_COLOR = "#f5a623";

export interface AircraftFeatureProps {
  id: string;
  callsign: string;
  icao: string;
  altitudeFt: number | null;
}

/** Aircraft (devices with a self-reported CPR fix) → point GeoJSON; positionless devices excluded. */
export function aircraftToGeoJSON(
  devices: Device[],
): FeatureCollection<Point, AircraftFeatureProps> {
  const features: FeatureCollection<Point, AircraftFeatureProps>["features"] = [];
  for (const d of devices) {
    const lat = d.latitude;
    const lon = d.longitude;
    if (lat === null || lon === null || !Number.isFinite(lat) || !Number.isFinite(lon)) continue;
    features.push({
      type: "Feature",
      geometry: { type: "Point", coordinates: [lon, lat] },
      properties: {
        id: d.id,
        callsign: d.identifiers?.callsign ?? "",
        icao: d.identifiers?.icao ?? d.id,
        altitudeFt: d.altitudeFt,
      },
    });
  }
  return { type: "FeatureCollection", features };
}

/** Aircraft marker layer — a distinct amber circle (offline style has no sprites/glyphs for icons). */
export function aircraftLayer(mode: ColorMode): LayerSpecification {
  return {
    id: AIRCRAFT_POINT_LAYER,
    type: "circle",
    source: AIRCRAFT_SOURCE,
    paint: {
      "circle-radius": 5,
      "circle-color": AIRCRAFT_COLOR,
      "circle-stroke-color": chartTokens[mode].surface,
      "circle-stroke-width": 2,
    },
  } as unknown as LayerSpecification;
}
```

- [ ] **Step 6: Run the lib test to verify it passes**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/lib/rfmap.test.ts`
Expected: PASS.

- [ ] **Step 7: Render + select aircraft in `RfMap.tsx`**

In `web/src/views/RfMap.tsx`:

1. Import the new helpers + `getDevices`/`Device`:
```typescript
import { getEmitters, getDevices, usePolling, type Emitter, type Device } from "../api";
import {
  // ...existing imports...
  aircraftToGeoJSON,
  aircraftLayer,
  AIRCRAFT_SOURCE,
  AIRCRAFT_POINT_LAYER,
} from "../lib/rfmap";
```
2. Poll devices and add aircraft selection state (next to the existing emitter poll/state):
```typescript
  const devices = usePolling(getDevices, 5000);
  const aircraftRef = useRef<Device[]>([]);
  const [selectedAircraft, setSelectedAircraft] = useState<Device | null>(null);
  aircraftRef.current = devices.data ?? [];
```
3. In the map-`load` handler (after the emitter layers are added), add the aircraft source + layer + interactions:
```typescript
      map.addSource(AIRCRAFT_SOURCE, { type: "geojson", data: { type: "FeatureCollection", features: [] } });
      map.addLayer(aircraftLayer(mode));
      map.on("click", AIRCRAFT_POINT_LAYER, (e) => {
        const id = e.features?.[0]?.properties?.id as string | undefined;
        if (id) setSelectedAircraft(aircraftRef.current.find((a) => a.id === id) ?? null);
      });
      map.on("mouseenter", AIRCRAFT_POINT_LAYER, onEnter);
      map.on("mouseleave", AIRCRAFT_POINT_LAYER, onLeave);
```
4. In the data-push effect (alongside the emitter `src?.setData(fc)`), push aircraft:
```typescript
      const aSrc = map.getSource(AIRCRAFT_SOURCE) as GeoJSONSource | undefined;
      aSrc?.setData(aircraftToGeoJSON(devices.data ?? []));
```
   Add `devices.data` to that effect's dependency array.
5. Add an aircraft detail Drawer (mirror the emitter drawer; open when `selectedAircraft !== null`), showing callsign (title), ICAO, altitude (`${altitudeFt} ft`), position (`lat, lon`), and `EvidenceList`. Add an "Aircraft" swatch (amber `#f5a623`) to the legend `Box`.

- [ ] **Step 8: Run the full frontend suite + build**

Run: `cd /c/GIT/SignalAtlas/web && npm run test && npm run build`
Expected: all tests PASS; `tsc -b && vite build` clean.

- [ ] **Step 9: Commit**

```bash
git add web/src/api.ts web/src/lib/rfmap.ts web/src/lib/rfmap.test.ts web/src/views/RfMap.tsx
git commit -m "feat(web): plot ADS-B aircraft on the RF map from /devices

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage:**
- Decoder TC 9-18 (CPR raw + altitude, pure) → Task 1. ✓
- `CprPositionResolver` global CPR + per-ICAO even/odd cache + IClock window + NL zone check → Task 2. ✓
- `Device` nullable position + `DeviceMerge` coalesce + EF/in-memory persistence → Task 3. ✓
- Pipeline stamps position (reads `DecodedFrame.Cpr`, calls resolver) + DI both build sites → Task 4. ✓
- Map aircraft layer from `/devices`, positionless excluded, distinct marker, click→detail, legend → Task 5. ✓
- Invariants (L1/L2-L3/P5/P4) → Global Constraints; decoder pure, resolver deterministic (caller timestamps), raw CPR only on typed frame field (no unbounded device growth). ✓
- Deterministic synthetic testing incl. the canonical CPR vector → Tasks 1/2/4. ✓
- Out-of-scope (surface position, velocity/heading, trails, local CPR) → none implemented. ✓
- **Refinement vs spec:** spec §Component-1 described CPR via transient identifiers/evidence; the plan carries it on a typed `DecodedFrame.Cpr` field instead — same intent (raw CPR consumed by the pipeline, not persisted as identity) but avoids unbounded device evidence growth for a moving aircraft (a real defect the identifier/evidence route would cause). Noted in Global Constraints.

**2. Placeholder scan:** No TBD/TODO/"handle errors"/"similar to Task N". All code shown. The one soft spot (Task 4 `trailStop()`) carries an explicit fallback instruction (call `Modulate` with defaults) rather than an omission.

**3. Type consistency:** `CprPosition(bool Odd, int CprLat17, int CprLon17, int AltitudeFt)` and `DecodedFrame.Cpr` identical across Tasks 1/4. `GeoPosition(double Latitude, double Longitude)` + `ICprPositionResolver.Accept(string, bool, int, int, DateTimeOffset)` identical across Tasks 2/4. `Device` position fields (`Latitude`/`Longitude`/`AltitudeFt`, types `double?/double?/int?`) identical across Tasks 3/4/5. Frontend `latitude`/`longitude`/`altitudeFt` match the camelCased backend fields. `AIRCRAFT_SOURCE`/`AIRCRAFT_POINT_LAYER`/`aircraftToGeoJSON`/`aircraftLayer` identical across Task 5 steps. Canonical vector values identical everywhere.
