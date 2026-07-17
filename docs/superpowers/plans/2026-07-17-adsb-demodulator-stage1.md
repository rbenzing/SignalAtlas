# Live ADS-B Demodulator — Stage 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Demodulate live 1090 MHz IQ into Mode S frames and wire a pipeline decode stage so aircraft appear as Devices (ICAO + callsign).

**Architecture:** A new `AdsBDemodulator` (implements the existing `IDemodulator` domain seam) turns IQ into 14-byte candidate frames; a new null-safe decode stage in `IngestionPipeline` runs it, feeds frames to the existing `AdsBDecoder` → `DeviceResolver`, and upserts/pushes Aircraft devices. Reuses the tested decoder/resolver/registry unchanged.

**Tech Stack:** .NET 10, C#; xUnit; EF Core (SQLite for tests, Postgres in prod). Frontend untouched.

## Global Constraints

- **Backend only.** No `web/` changes. No new `/api/v1` routes.
- **Receive-only (L1).** Demod only reads IQ; never add a transmit member/path.
- **Metadata, not content (L2/L3).** Demod emits raw frames; only identity (ICAO/callsign) is interpreted downstream by the existing decoder. Never parse/persist payload.
- **Deterministic core (P5).** In `src/`, no `DateTime.Now`, `Guid.NewGuid()`, or unseeded `Random`. The demod is pure DSP; device ids come from `DeterministicGuid`. (Test code MAY use a seeded `new Random(seed)` — determinism rule binds `src/`, not `tests/`.)
- **Explainable-only (P4/P6).** Devices carry the decoder's evidence via `DeviceResolver` (already non-empty).
- **Protocol string is exactly `"ADS-B"`** everywhere (matches `AdsBDecoder.Protocol` and `DecoderRegistry` dispatch).
- **Golden test frame** (valid DF17, CRC syndrome 0, ICAO `4840D6`, callsign `KLM1023`): hex `8D4840D6202CC371C32CE0576098` — taken from `tests/SignalAtlas.Tests.Unit/DecodeAdsbTests.cs`.
- **Design/tested sample rate = 2 MS/s** (`hus = (sampleRateHz/1_000_000)/2 = 1`). Even rates ≥ 2 MS/s work via `hus`; only 2 MS/s is tested.
- **Verify each task:** `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"` (or the named test class). Build: `dotnet build SignalAtlas.slnx`. No `global.json` — needs the .NET 10 SDK.

## File Structure

- **Create** `src/SignalAtlas.Decode/Demodulators/AdsBDemodulator.cs` — IQ → 14-byte Mode S candidate frames. Pure DSP, self-gates on 1090 MHz. (Task 3)
- **Modify** `src/SignalAtlas.Domain/IDeviceRepository.cs` — add `Upsert(Device)`. (Task 1)
- **Modify** `src/SignalAtlas.Persistence/InMemoryDeviceRepository.cs` — implement idempotent, bounded, newest-first `Upsert`. (Task 1)
- **Modify** `src/SignalAtlas.Persistence/EfRepositories.cs` — `EfDeviceRepository.Upsert` (mirrors `EfEmitterRepository`). (Task 1)
- **Modify** `src/SignalAtlas.Pipeline/IngestionPipeline.cs` — 4 optional deps + step 4b decode stage. (Task 4)
- **Modify** `src/SignalAtlas.Api/Program.cs` — register `IDemodulator` → `AdsBDemodulator`. (Task 5)
- **Modify** `src/SignalAtlas.Api/IqIngressEndpoint.cs` — pass new deps in `BuildPipeline`. (Task 5)
- **Modify** `src/SignalAtlas.Api/PipelineHostedService.cs` — pass new deps. (Task 5)
- **Create** `tests/SignalAtlas.Tests.Unit/AdsBModulator.cs` — test-only PPM fixture generator. (Task 2)
- **Create** `tests/SignalAtlas.Tests.Unit/AdsBModulatorTests.cs` (Task 2), `AdsBDemodulatorTests.cs` (Task 3), `IngestionPipelineDecodeTests.cs` (Task 4).
- **Create** `tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs` (Task 1); **Modify** `tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs` (EF device upsert, Task 1).
- **Create** `tests/SignalAtlas.Tests.Integration/AdsBDemodulatorWiringTests.cs` — DI resolution (Task 5).

---

### Task 1: Device write path (`IDeviceRepository.Upsert`)

**Files:**
- Modify: `src/SignalAtlas.Domain/IDeviceRepository.cs`
- Modify: `src/SignalAtlas.Persistence/InMemoryDeviceRepository.cs`
- Modify: `src/SignalAtlas.Persistence/EfRepositories.cs:37-42`
- Create: `tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs`
- Modify: `tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs`

**Interfaces:**
- Consumes: `Device`, `DecodedFrame`, `EvidenceItem`, `IDeviceResolver` (all in `SignalAtlas.Domain`).
- Produces: `IDeviceRepository.Upsert(Device device)` — idempotent on `Device.Id`; in-memory keeps newest-first, bounded at 2000.

- [ ] **Step 1: Write the failing in-memory test**

Create `tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs`:

```csharp
using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

public sealed class InMemoryDeviceRepositoryTests
{
    // The repo ctor runs one seed frame through an IDeviceResolver; a fake keeps this project
    // free of a SignalAtlas.Decode reference (it does not reference Decode).
    private sealed class FakeResolver : IDeviceResolver
    {
        public Device? Resolve(IReadOnlyList<DecodedFrame> frames)
        {
            if (frames.Count == 0) return null;
            var id = frames[0].Identifiers.TryGetValue("icao", out var icao) ? icao : "seed";
            return new Device(id, "Aircraft", id, frames[0].Identifiers, null, "ADS-B", 1.0,
                [new EvidenceItem("crc", "pass", 1.0)]);
        }
    }

    private static Device Aircraft(string icao, string? callsign = null) =>
        new(icao, "Aircraft", icao,
            new Dictionary<string, string>(callsign is null
                ? new Dictionary<string, string> { ["icao"] = icao }
                : new Dictionary<string, string> { ["icao"] = icao, ["callsign"] = callsign }),
            null, "ADS-B", 1.0, [new EvidenceItem("icao", icao, 1.0)]);

    private static InMemoryDeviceRepository New()
    {
        var repo = new InMemoryDeviceRepository(new FakeResolver());
        repo.ClearDemoSeed(); // start empty so assertions count only what we upsert
        return repo;
    }

    [Fact]
    public void Upsert_NewDevice_IsReturnedByGet()
    {
        var repo = New();
        repo.Upsert(Aircraft("4840D6", "KLM1023"));

        var got = Assert.Single(repo.GetDevices());
        Assert.Equal("4840D6", got.Id);
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
    }

    [Fact]
    public void Upsert_SameId_IsIdempotent_AndUpdatesInPlace()
    {
        var repo = New();
        repo.Upsert(Aircraft("4840D6"));                 // no callsign yet
        repo.Upsert(Aircraft("4840D6", "KLM1023"));      // later frame adds callsign

        var got = Assert.Single(repo.GetDevices());      // one entry, not two
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
    }

    [Fact]
    public void Upsert_OrdersNewestFirst()
    {
        var repo = New();
        repo.Upsert(Aircraft("AAAAAA"));
        repo.Upsert(Aircraft("BBBBBB"));

        var all = repo.GetDevices();
        Assert.Equal("BBBBBB", all[0].Id);   // most recent upsert first
        Assert.Equal("AAAAAA", all[1].Id);
    }

    [Fact]
    public void Upsert_SameId_Concurrently_KeepsOneEntry()
    {
        var repo = New();
        Parallel.For(0, 200, _ => repo.Upsert(Aircraft("4840D6")));
        Assert.Single(repo.GetDevices());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Persistence --filter "FullyQualifiedName~InMemoryDeviceRepositoryTests"`
Expected: FAIL to COMPILE — `InMemoryDeviceRepository` has no `Upsert`.

- [ ] **Step 3: Add `Upsert` to the interface**

In `src/SignalAtlas.Domain/IDeviceRepository.cs`, replace the interface body:

```csharp
namespace SignalAtlas.Domain;

/// <summary>Read + write access to determined devices (SPEC §9.2 GET /devices; §8.4 write).</summary>
public interface IDeviceRepository
{
    IReadOnlyList<Device> GetDevices(int limit = 100);

    /// <summary>Idempotent insert-or-update keyed on the deterministic <see cref="Device.Id"/>
    /// (SPEC §8.4): the same aircraft re-seen across blocks converges instead of duplicating.</summary>
    void Upsert(Device device);
}
```

- [ ] **Step 4: Implement in-memory `Upsert`**

In `src/SignalAtlas.Persistence/InMemoryDeviceRepository.cs`, add a cap constant and the method (place after `GetDevices`):

```csharp
    private const int MaxDevices = 2000;

    /// <summary>Idempotent, newest-first, bounded upsert (SPEC §8.4). Replaces any existing row with
    /// the same id, then prepends, so live re-sightings of one aircraft stay a single entry.</summary>
    public void Upsert(Device device)
    {
        lock (_sync)
        {
            _devices.RemoveAll(d => string.Equals(d.Id, device.Id, StringComparison.Ordinal));
            _devices.Insert(0, device);
            if (_devices.Count > MaxDevices)
                _devices.RemoveRange(MaxDevices, _devices.Count - MaxDevices);
        }
    }
```

Note: `GetDevices` already returns `_devices.Take(limit)` under the same lock, so newest-first holds.

- [ ] **Step 5: Implement EF `Upsert` (mirrors `EfEmitterRepository`)**

In `src/SignalAtlas.Persistence/EfRepositories.cs`, replace the `EfDeviceRepository` class:

```csharp
/// <summary>EF-backed device store (SPEC §9.2 GET /devices; §8.4 write from the live decode stage).</summary>
public sealed class EfDeviceRepository(SignalAtlasDbContext db) : IDeviceRepository
{
    public IReadOnlyList<Device> GetDevices(int limit = 100) =>
        db.Devices.OrderBy(d => d.Id).Take(limit).ToList();

    /// <summary>Idempotent insert-or-update keyed on the deterministic device id (SPEC §8.4).</summary>
    public void Upsert(Device device)
    {
        var existing = db.Devices.Find(device.Id);
        if (existing is null)
            db.Devices.Add(device);
        else
            db.Entry(existing).CurrentValues.SetValues(device);
        db.SaveChanges();
    }
}
```

- [ ] **Step 6: Add the EF round-trip test**

In `tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs`, add a device sample helper next to `SampleEmitter` and two tests (after the emitter tests):

```csharp
    private static Device SampleDevice(string icao, string? callsign) =>
        new(
            Id: icao,
            DeviceType: "Aircraft",
            PrimaryIdentifier: icao,
            Identifiers: callsign is null
                ? new Dictionary<string, string> { ["icao"] = icao }
                : new Dictionary<string, string> { ["icao"] = icao, ["callsign"] = callsign },
            Vendor: null,
            Protocol: "ADS-B",
            Confidence: 1.0,
            Evidence: [new EvidenceItem("icao", icao, 1.0)]);

    [Fact]
    public void Device_Upsert_Then_Get_RoundTrips()
    {
        new EfDeviceRepository(NewContext()).Upsert(SampleDevice("4840D6", "KLM1023"));

        var read = new EfDeviceRepository(NewContext()).GetDevices();

        var got = Assert.Single(read);
        Assert.Equal("4840D6", got.Id);
        Assert.Equal("KLM1023", got.Identifiers["callsign"]);
        Assert.NotEmpty(got.Evidence);
    }

    [Fact]
    public void Device_Upsert_IsIdempotent_AndUpdatesExistingRow()
    {
        new EfDeviceRepository(NewContext()).Upsert(SampleDevice("4840D6", null));
        new EfDeviceRepository(NewContext()).Upsert(SampleDevice("4840D6", "KLM1023"));

        var read = new EfDeviceRepository(NewContext()).GetDevices();

        var got = Assert.Single(read);                       // same id → no duplicate
        Assert.Equal("KLM1023", got.Identifiers["callsign"]); // updated in place
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SignalAtlas.Tests.Persistence --filter "FullyQualifiedName~InMemoryDeviceRepositoryTests|FullyQualifiedName~WritePathRoundTripTests"`
Expected: PASS (4 in-memory + all EF round-trip tests including the 2 new device ones).

- [ ] **Step 8: Commit**

```bash
git add src/SignalAtlas.Domain/IDeviceRepository.cs src/SignalAtlas.Persistence/InMemoryDeviceRepository.cs src/SignalAtlas.Persistence/EfRepositories.cs tests/SignalAtlas.Tests.Persistence/InMemoryDeviceRepositoryTests.cs tests/SignalAtlas.Tests.Persistence/WritePathRoundTripTests.cs
git commit -m "feat(persistence): idempotent Device.Upsert (in-memory + EF)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: `AdsBModulator` test fixture generator

**Files:**
- Create: `tests/SignalAtlas.Tests.Unit/AdsBModulator.cs`
- Create: `tests/SignalAtlas.Tests.Unit/AdsBModulatorTests.cs`

**Interfaces:**
- Consumes: `IqBlock` (`SignalAtlas.Domain`).
- Produces: `AdsBModulator.Modulate(byte[] frame, int sampleRateHz = 2_000_000, long centerFreqHz = 1_090_000_000, int leadSlots = 4, int trailSlots = 4, double amp = 1.0, double noiseSigma = 0.0, int seed = 12345) : IqBlock`. Layout in half-µs "slots": `hus = (sampleRateHz/1_000_000)/2` samples per slot; preamble pulses at slots {0,2,7,9}; 112 data bits at slots 16 + 2·bit (a `1`) or 16 + 2·bit + 1 (a `0`), MSB-first per byte; total slots = leadSlots + 240 + trailSlots.

This is the exact inverse of the Task 3 demodulator; its structural tests here are independent of the demod, so a wrong fixture is caught before it can make demod tests lie.

- [ ] **Step 1: Write the failing structural test**

Create `tests/SignalAtlas.Tests.Unit/AdsBModulatorTests.cs`:

```csharp
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class AdsBModulatorTests
{
    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    // At 2 MS/s hus=1, leadSlots=0: preamble pulses land on I samples {0,2,7,9}, gaps are 0.
    [Fact]
    public void Modulate_PlacesPreamblePulsesAtKnownHalfMicrosecondSlots()
    {
        var block = AdsBModulator.Modulate(new byte[14], leadSlots: 0, trailSlots: 0, noiseSigma: 0.0);

        foreach (var slot in new[] { 0, 2, 7, 9 })
            Assert.True(block.I[slot] > 0.5f, $"preamble slot {slot} should be a pulse");
        foreach (var slot in new[] { 1, 3, 4, 5, 6, 8 })
            Assert.Equal(0f, block.I[slot]);
    }

    // A data '1' bit puts the pulse in the FIRST half-chip; a '0' in the SECOND.
    [Fact]
    public void Modulate_EncodesBitsAsPpmFirstOrSecondHalfChip()
    {
        // bit0 = MSB of byte0. 0x80 → bit0 = 1; 0x00 → bit0 = 0.
        var one = AdsBModulator.Modulate(Hex("80000000000000000000000000"), leadSlots: 0, trailSlots: 0);
        var zero = AdsBModulator.Modulate(Hex("00000000000000000000000000"), leadSlots: 0, trailSlots: 0);

        // Data starts at slot 16 (after the 16-slot preamble). bit0 occupies slots 16,17.
        Assert.True(one.I[16] > 0.5f && one.I[17] == 0f);   // '1' → first half-chip
        Assert.True(zero.I[16] == 0f && zero.I[17] > 0.5f); // '0' → second half-chip
    }

    [Fact]
    public void Modulate_IsInBandAt1090AndDeterministicUnderSeededNoise()
    {
        var a = AdsBModulator.Modulate(Hex("8D4840D6202CC371C32CE0576098"), noiseSigma: 0.1, seed: 7);
        var b = AdsBModulator.Modulate(Hex("8D4840D6202CC371C32CE0576098"), noiseSigma: 0.1, seed: 7);

        Assert.Equal(1_090_000_000, a.CenterFreqHz);
        Assert.Equal(a.I, b.I);   // same seed → identical noise → deterministic fixtures
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~AdsBModulatorTests"`
Expected: FAIL to COMPILE — `AdsBModulator` does not exist.

- [ ] **Step 3: Implement the modulator**

Create `tests/SignalAtlas.Tests.Unit/AdsBModulator.cs`:

```csharp
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// TEST-ONLY inverse of <c>AdsBDemodulator</c>: renders a 14-byte Mode S extended squitter as
/// Mode S PPM IQ (amplitude in I, Q carries only optional noise). Deterministic (seeded RNG) so
/// fixtures are reproducible. NOT shipped — lives in the test project.
/// </summary>
public static class AdsBModulator
{
    private const int FrameBits = 112;
    private const int PreambleSlots = 16;                    // 8 µs = 16 half-µs slots
    private const int FrameSlots = PreambleSlots + FrameBits * 2; // 240
    private static readonly int[] PulseSlots = { 0, 2, 7, 9 };

    public static IqBlock Modulate(
        byte[] frame,
        int sampleRateHz = 2_000_000,
        long centerFreqHz = 1_090_000_000,
        int leadSlots = 4,
        int trailSlots = 4,
        double amp = 1.0,
        double noiseSigma = 0.0,
        int seed = 12345)
    {
        if (frame.Length != 14) throw new ArgumentException("frame must be 14 bytes", nameof(frame));
        int hus = (sampleRateHz / 1_000_000) / 2;
        if (hus < 1) throw new ArgumentException("sample rate must be >= 2 MS/s", nameof(sampleRateHz));

        int totalSlots = leadSlots + FrameSlots + trailSlots;
        int n = totalSlots * hus;
        var i = new float[n];
        var q = new float[n];

        int baseSlot = leadSlots;
        foreach (var s in PulseSlots) FillSlot(i, baseSlot + s, hus, amp);
        for (int b = 0; b < FrameBits; b++)
        {
            bool one = (frame[b >> 3] & (0x80 >> (b & 7))) != 0;
            int slot = baseSlot + PreambleSlots + 2 * b + (one ? 0 : 1);
            FillSlot(i, slot, hus, amp);
        }

        if (noiseSigma > 0) AddNoise(i, q, noiseSigma, seed);
        return new IqBlock(centerFreqHz, sampleRateHz, i, q);
    }

    private static void FillSlot(float[] i, int slot, int hus, double amp)
    {
        int start = slot * hus;
        for (int k = 0; k < hus; k++) i[start + k] = (float)amp;
    }

    private static void AddNoise(float[] i, float[] q, double sigma, int seed)
    {
        var rng = new Random(seed); // TEST-ONLY seeded RNG → deterministic fixtures.
        for (int k = 0; k < i.Length; k++)
        {
            i[k] += (float)(Gaussian(rng) * sigma);
            q[k] += (float)(Gaussian(rng) * sigma);
        }
    }

    private static double Gaussian(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~AdsBModulatorTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/SignalAtlas.Tests.Unit/AdsBModulator.cs tests/SignalAtlas.Tests.Unit/AdsBModulatorTests.cs
git commit -m "test: ADS-B PPM modulator fixture generator (closed-loop harness)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: `AdsBDemodulator`

**Files:**
- Create: `src/SignalAtlas.Decode/Demodulators/AdsBDemodulator.cs`
- Create: `tests/SignalAtlas.Tests.Unit/AdsBDemodulatorTests.cs`

**Interfaces:**
- Consumes: `IDemodulator`, `IqBlock`, `FeatureVector` (`SignalAtlas.Domain`); `AdsBModulator` (Task 2); `AdsBDecoder` (`SignalAtlas.Decode.Decoders`).
- Produces: `AdsBDemodulator : IDemodulator`, `Protocol => "ADS-B"`, `Demodulate(IqBlock, FeatureVector) : IEnumerable<ReadOnlyMemory<byte>>`.

- [ ] **Step 1: Write the failing test**

Create `tests/SignalAtlas.Tests.Unit/AdsBDemodulatorTests.cs`:

```csharp
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class AdsBDemodulatorTests
{
    private const string GoldenHex = "8D4840D6202CC371C32CE0576098";
    private static readonly AdsBDemodulator Demod = new();
    private static FeatureVector Features() => new(1_090_000_000, 0, 0, 0, 0, 0, 0, null);

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    [Fact]
    public void Demodulate_CleanGoldenFrame_RecoversExactBytes()
    {
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.0);

        var frames = Demod.Demodulate(block, Features()).ToList();

        var got = Assert.Single(frames);
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }

    [Fact]
    public void Demodulate_ThroughRealDecoder_YieldsIcaoAndCallsign()
    {
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.05);

        var frames = Demod.Demodulate(block, Features()).ToList();
        var outcome = new AdsBDecoder().Decode(Assert.Single(frames));

        Assert.True(outcome.Success);
        Assert.Equal("4840D6", outcome.Frame!.Identifiers["icao"]);
        Assert.Equal("KLM1023", outcome.Frame.Identifiers["callsign"]);
    }

    [Fact]
    public void Demodulate_UnderModerateNoise_StillRecoversExactBytes()
    {
        var block = AdsBModulator.Modulate(Hex(GoldenHex), noiseSigma: 0.1, seed: 3);

        var got = Assert.Single(Demod.Demodulate(block, Features()).ToList());
        Assert.Equal(Hex(GoldenHex), got.ToArray());
    }

    [Fact]
    public void Demodulate_PureNoise_YieldsNothing()
    {
        // amp:0 → no pulses, only seeded noise.
        var block = AdsBModulator.Modulate(new byte[14], amp: 0.0, noiseSigma: 1.0, seed: 99);

        Assert.Empty(Demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_OffFrequencyBlock_YieldsNothing_SelfGate()
    {
        // Frame present, but tuned to 915 MHz @ 2 MS/s → 1090 MHz not in band.
        var block = AdsBModulator.Modulate(Hex(GoldenHex), centerFreqHz: 915_000_000);

        Assert.Empty(Demod.Demodulate(block, Features()).ToList());
    }

    [Fact]
    public void Demodulate_TwoFramesInOneBlock_RecoversBoth()
    {
        // Concatenate two modulated frames into one block's I/Q.
        var a = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 2, trailSlots: 2);
        var b = AdsBModulator.Modulate(Hex(GoldenHex), leadSlots: 2, trailSlots: 2);
        var i = a.I.Concat(b.I).ToArray();
        var q = a.Q.Concat(b.Q).ToArray();
        var block = new IqBlock(1_090_000_000, 2_000_000, i, q);

        var frames = Demod.Demodulate(block, Features()).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(Hex(GoldenHex), f.ToArray()));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~AdsBDemodulatorTests"`
Expected: FAIL to COMPILE — `AdsBDemodulator` does not exist.

- [ ] **Step 3: Implement the demodulator**

Create `src/SignalAtlas.Decode/Demodulators/AdsBDemodulator.cs`:

```csharp
using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Demodulators;

/// <summary>
/// Mode S extended-squitter (DF17/18) physical-layer demodulator (SPEC §8.4 Tier A). Turns 1090 MHz
/// IQ into candidate 14-byte frames by envelope detection → preamble correlation → PPM bit-slicing.
/// CRC is NOT checked here — <c>AdsBDecoder</c> validates it, so noise candidates are rejected
/// downstream. Pure DSP: deterministic, receive-only, reads only the magnitude envelope (L1/L2/P5).
/// </summary>
public sealed class AdsBDemodulator : IDemodulator
{
    private const long AdsBFreqHz = 1_090_000_000;
    private const int FrameBits = 112;
    private const int FrameBytes = 14;
    private const int PreambleSlots = 16;                    // 8 µs preamble = 16 half-µs slots
    private const int FrameSlots = PreambleSlots + FrameBits * 2; // 240
    private const double PulseFactor = 4.0;                  // pulses must clear the local floor ×4

    public string Protocol => "ADS-B";

    public IEnumerable<ReadOnlyMemory<byte>> Demodulate(IqBlock block, FeatureVector features)
    {
        // Self-gate: 1090 MHz must sit inside the captured band, else this block isn't ours.
        long half = block.SampleRateHz / 2L;
        if (AdsBFreqHz < block.CenterFreqHz - half || AdsBFreqHz > block.CenterFreqHz + half)
            yield break;

        int hus = (block.SampleRateHz / 1_000_000) / 2; // samples per half-µs slot
        if (hus < 1) yield break;                        // need >= 2 MS/s

        float[] iCh = block.I, qCh = block.Q;
        int n = iCh.Length;
        var mag = new double[n];
        for (int k = 0; k < n; k++) mag[k] = (double)iCh[k] * iCh[k] + (double)qCh[k] * qCh[k];

        int frameSamples = FrameSlots * hus;
        int pos = 0;
        while (pos + frameSamples <= n)
        {
            if (TryPreamble(mag, pos, hus))
            {
                yield return SliceFrame(mag, pos, hus);
                pos += frameSamples;   // consume the frame; resume scanning after it
            }
            else
            {
                pos++;
            }
        }
    }

    // Energy in one half-µs slot = sum of magnitude over its hus samples.
    private static double Chip(double[] mag, int baseSample, int slot, int hus)
    {
        double sum = 0;
        int start = baseSample + slot * hus;
        for (int k = 0; k < hus; k++) sum += mag[start + k];
        return sum;
    }

    // Mode S preamble shape: pulses at slots {0,2,7,9}, gaps at {1,3,6,8}. Each pulse must dominate
    // its adjacent gaps AND clear the local floor ×PulseFactor so random noise doesn't trigger.
    private static bool TryPreamble(double[] mag, int pos, int hus)
    {
        double c0 = Chip(mag, pos, 0, hus), c1 = Chip(mag, pos, 1, hus),
               c2 = Chip(mag, pos, 2, hus), c3 = Chip(mag, pos, 3, hus),
               c6 = Chip(mag, pos, 6, hus), c7 = Chip(mag, pos, 7, hus),
               c8 = Chip(mag, pos, 8, hus), c9 = Chip(mag, pos, 9, hus);

        if (!(c0 > c1 && c2 > c1 && c2 > c3 && c7 > c6 && c7 > c8 && c9 > c8))
            return false;

        double floor = (c1 + c3 + c6 + c8) / 4.0;
        double minPulse = Math.Min(Math.Min(c0, c2), Math.Min(c7, c9));
        return minPulse > 0 && minPulse > floor * PulseFactor;
    }

    private static byte[] SliceFrame(double[] mag, int pos, int hus)
    {
        var bytes = new byte[FrameBytes];
        for (int b = 0; b < FrameBits; b++)
        {
            double first = Chip(mag, pos, PreambleSlots + 2 * b, hus);
            double second = Chip(mag, pos, PreambleSlots + 2 * b + 1, hus);
            if (first > second) bytes[b >> 3] |= (byte)(0x80 >> (b & 7)); // '1' = pulse in first half
        }
        return bytes;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~AdsBDemodulatorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SignalAtlas.Decode/Demodulators/AdsBDemodulator.cs tests/SignalAtlas.Tests.Unit/AdsBDemodulatorTests.cs
git commit -m "feat(decode): ADS-B Mode S PPM demodulator (IQ -> 14-byte frames)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Pipeline decode stage

**Files:**
- Modify: `src/SignalAtlas.Pipeline/IngestionPipeline.cs`
- Create: `tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs`

**Interfaces:**
- Consumes: `IDemodulator`, `IDecoderRegistry`, `IDeviceResolver`, `IDeviceRepository`, `DecodedFrame` (Domain); the concretes `AdsBDemodulator`, `DecoderRegistry`, `AdsBDecoder`, `DeviceResolver`, `OuiLookup` (Decode) in tests.
- Produces: `IngestionPipeline` ctor gains 4 optional trailing params `IEnumerable<IDemodulator>? demodulators = null, IDecoderRegistry? registry = null, IDeviceResolver? resolver = null, IDeviceRepository? devices = null`. When any is null the decode stage is a no-op.

- [ ] **Step 1: Write the failing test**

Create `tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs`:

```csharp
using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Correlation;
using SignalAtlas.Decode;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

public class IngestionPipelineDecodeTests
{
    private const string GoldenHex = "8D4840D6202CC371C32CE0576098";

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    // Single-block source carrying the caller's IqBlock (SPEC ISampleSource, receive-only).
    private sealed class OneBlockSource(IqBlock block) : ISampleSource
    {
        public IEnumerable<IqBlock> Blocks() { yield return block; }
    }

    private sealed class CapturingDeviceRepo : IDeviceRepository
    {
        public List<Device> Upserts { get; } = [];
        public IReadOnlyList<Device> GetDevices(int limit = 100) => Upserts;
        public void Upsert(Device device) => Upserts.Add(device);
    }

    private sealed class CapturingNotifier : ILiveNotifier
    {
        public List<Device> Devices { get; } = [];
        public void SignalCreated(Signal signal) { }
        public void EmitterUpdated(Emitter emitter) { }
        public void AlertRaised(Alert alert) { }
        public void SpectrumFrame(SpectrumFrame frame) { }
        public void DeviceDetermined(Device device) => Devices.Add(device);
    }

    private sealed class NoopObs : IObservationRepository
    {
        public void Add(Observation o) { }
        public IReadOnlyList<Observation> GetRecent(int limit) => [];
    }

    private sealed class NoopSignals : ISignalWriter { public void Add(Signal s) { } }

    private static IngestionPipeline Build(CapturingDeviceRepo devices, CapturingNotifier notifier) =>
        new(
            collectorId: "adsb-test",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
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
            devices: devices);

    [Fact]
    public void Run_BlockWithAdsBFrame_UpsertsAircraftAndPushesDeviceDetermined()
    {
        // Frame embedded in an 8192-sample @ 2 MS/s block at 1090 MHz (light noise avoids all-zero DSP).
        var block = AdsBModulator.Modulate(Hex(GoldenHex), trailSlots: 7948, noiseSigma: 0.01);
        var devices = new CapturingDeviceRepo();
        var notifier = new CapturingNotifier();

        Build(devices, notifier).Run(new OneBlockSource(block));

        var device = Assert.Single(devices.Upserts);
        Assert.Equal("Aircraft", device.DeviceType);
        Assert.Equal("4840D6", device.Identifiers["icao"]);
        Assert.Contains(notifier.Devices, d => d.Identifiers["icao"] == "4840D6");
    }

    [Fact]
    public void Run_BlockWithoutAdsB_DeterminesNoDevice()
    {
        // A 915 MHz block → demod self-gates off → no devices, no regression to the RF pipeline.
        var block = AdsBModulator.Modulate(Hex(GoldenHex), centerFreqHz: 915_000_000,
            trailSlots: 7948, noiseSigma: 0.01);
        var devices = new CapturingDeviceRepo();
        var notifier = new CapturingNotifier();

        Build(devices, notifier).Run(new OneBlockSource(block));

        Assert.Empty(devices.Upserts);
        Assert.Empty(notifier.Devices);
    }
}
```

Note: `FixedClock` and `StaticPositionSource` are existing shared test doubles in `SignalAtlas.Tests.Unit` (used by `IngestionPipelineNotifierTests`). If the compiler cannot find them, add `using` for their namespace or check `IngestionPipelineNotifierTests.cs` for their definitions and reuse as-is — do not redefine them.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~IngestionPipelineDecodeTests"`
Expected: FAIL to COMPILE — `IngestionPipeline` has no `demodulators`/`registry`/`resolver`/`devices` params.

- [ ] **Step 3: Add the fields and constructor params**

In `src/SignalAtlas.Pipeline/IngestionPipeline.cs`, add fields after `_notifier` (line ~34):

```csharp
    private readonly IReadOnlyList<IDemodulator> _demodulators;
    private readonly IDecoderRegistry? _registry;
    private readonly IDeviceResolver? _resolver;
    private readonly IDeviceRepository? _devices;
```

Extend the constructor signature — add these four params at the END of the parameter list (after `ILiveNotifier? notifier = null`):

```csharp
        ILiveNotifier? notifier = null,
        IEnumerable<IDemodulator>? demodulators = null,
        IDecoderRegistry? registry = null,
        IDeviceResolver? resolver = null,
        IDeviceRepository? devices = null)
```

And assign in the constructor body (after `_notifier = ...`):

```csharp
        // Optional decode stage (SPEC §8.4): demodulate IQ → frames → decode → determine devices.
        // Any null → the stage is a no-op (RF-only pipeline, unchanged).
        _demodulators = demodulators?.ToList() ?? [];
        _registry = registry;
        _resolver = resolver;
        _devices = devices;
```

- [ ] **Step 4: Add the decode stage (step 4b) and the grouping helper**

In `Run`, immediately after `_notifier.SignalCreated(signal);` (line ~119, before the `// 4. Correlate` comment), insert:

```csharp
            // 4b. DECODE (SPEC §8.4): demodulate this block, decode frames, determine devices.
            if (_demodulators.Count > 0 && _registry is not null && _resolver is not null && _devices is not null)
            {
                var decoded = new List<DecodedFrame>();
                foreach (var demod in _demodulators)
                    foreach (var frameBytes in demod.Demodulate(block, features))
                    {
                        var outcome = _registry.Decode(demod.Protocol, frameBytes);
                        if (outcome.Success && outcome.Frame is not null)
                            decoded.Add(outcome.Frame);
                    }

                // One device per distinct primary identifier (e.g. ICAO): frames from the same
                // aircraft converge on the same deterministic device id → Upsert merges them.
                foreach (var group in decoded.GroupBy(PrimaryKey))
                {
                    var device = _resolver.Resolve(group.ToList());
                    if (device is not null)
                    {
                        _devices.Upsert(device);
                        _notifier.DeviceDetermined(device);
                    }
                }
            }
```

Add the helper near the other private statics (e.g. after `EmptyIdentifiers`):

```csharp
    // Ordered primary-identifier keys (mirrors DeviceResolver's precedence) used to group a block's
    // decoded frames per device before resolving. Falls back to protocol when none is present.
    private static readonly string[] PrimaryKeyOrder =
        { "icao", "bssid", "mac", "devaddr", "deveui", "ext_addr", "short_addr", "pan_id", "callsign", "pi", "ps" };

    private static string PrimaryKey(DecodedFrame f)
    {
        foreach (var key in PrimaryKeyOrder)
            if (f.Identifiers.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return $"{key}:{v}";
        return $"proto:{f.Protocol}";
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SignalAtlas.Tests.Unit --filter "FullyQualifiedName~IngestionPipelineDecodeTests|FullyQualifiedName~IngestionPipelineNotifierTests"`
Expected: PASS — both new decode tests AND the existing notifier test (proves the RF-only path with null decode deps still works).

- [ ] **Step 6: Commit**

```bash
git add src/SignalAtlas.Pipeline/IngestionPipeline.cs tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs
git commit -m "feat(pipeline): decode stage — demodulate IQ to Aircraft devices

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: DI wiring

**Files:**
- Modify: `src/SignalAtlas.Api/Program.cs:88-96`
- Modify: `src/SignalAtlas.Api/IqIngressEndpoint.cs:159-172`
- Modify: `src/SignalAtlas.Api/PipelineHostedService.cs:52-65`
- Create: `tests/SignalAtlas.Tests.Integration/AdsBDemodulatorWiringTests.cs`

**Interfaces:**
- Consumes: `AdsBDemodulator` (`SignalAtlas.Decode.Demodulators`); the pipeline's 4 new optional params from Task 4; DI helpers `sp.GetServices<IDemodulator>()`.
- Produces: `IDemodulator` registered; both live pipeline build sites pass the decode-stage deps.

- [ ] **Step 1: Write the failing DI test**

Create `tests/SignalAtlas.Tests.Integration/AdsBDemodulatorWiringTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Integration;

/// <summary>
/// The live decode stage only determines devices if the demodulator + decode collaborators are
/// registered. Asserts DI provides them so BuildPipeline / PipelineHostedService can inject them.
/// </summary>
public class AdsBDemodulatorWiringTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void AdsBDemodulator_IsRegisteredAsIDemodulator()
    {
        using var scope = factory.Services.CreateScope();
        var demods = scope.ServiceProvider.GetServices<IDemodulator>();
        Assert.Contains(demods, d => d is AdsBDemodulator);
    }

    [Fact]
    public void DecodeStageCollaborators_AreResolvable()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetService<IDecoderRegistry>());
        Assert.NotNull(sp.GetService<IDeviceResolver>());
        Assert.NotNull(sp.GetService<IDeviceRepository>());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SignalAtlas.Tests.Integration --filter "FullyQualifiedName~AdsBDemodulatorWiringTests"`
Expected: FAIL — `AdsBDemodulator_IsRegisteredAsIDemodulator` fails (no `IDemodulator` registered).

- [ ] **Step 3: Register the demodulator**

In `src/SignalAtlas.Api/Program.cs`, add the using near the top with the other `SignalAtlas.Decode` usings:

```csharp
using SignalAtlas.Decode.Demodulators;
```

And register it alongside the decoder registrations (right after the `AddSingleton<IDeviceResolver, DeviceResolver>();` line, ~line 96):

```csharp
// IQ→frame demodulators feeding the live decode stage (SPEC §8.4). ADS-B is the first.
builder.Services.AddSingleton<IDemodulator, AdsBDemodulator>();
```

- [ ] **Step 4: Pass the deps in `BuildPipeline`**

In `src/SignalAtlas.Api/IqIngressEndpoint.cs`, extend the `new IngestionPipeline(...)` call in `BuildPipeline` — add these arguments after `notifier: notifier`:

```csharp
            notifier: notifier,
            demodulators: sp.GetServices<IDemodulator>(),
            registry: sp.GetService<IDecoderRegistry>(),
            resolver: sp.GetService<IDeviceResolver>(),
            devices: sp.GetService<IDeviceRepository>());
```

- [ ] **Step 5: Pass the deps in `PipelineHostedService`**

In `src/SignalAtlas.Api/PipelineHostedService.cs`, extend its `new IngestionPipeline(...)` call — replace the final `notifier:` argument with:

```csharp
            notifier: sp.GetService<ILiveNotifier>(),   // live push (SPEC §9.3); null-safe if unregistered.
            demodulators: sp.GetServices<IDemodulator>(),
            registry: sp.GetService<IDecoderRegistry>(),
            resolver: sp.GetService<IDeviceResolver>(),
            devices: sp.GetService<IDeviceRepository>());
```

- [ ] **Step 6: Run the wiring test + full backend suite**

Run: `dotnet test tests/SignalAtlas.Tests.Integration --filter "FullyQualifiedName~AdsBDemodulatorWiringTests"`
Expected: PASS (2 tests).

Then the whole non-Docker suite to confirm no regressions:

Run: `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"`
Expected: PASS across Unit / Integration / Contract / Persistence.

- [ ] **Step 7: Commit**

```bash
git add src/SignalAtlas.Api/Program.cs src/SignalAtlas.Api/IqIngressEndpoint.cs src/SignalAtlas.Api/PipelineHostedService.cs tests/SignalAtlas.Tests.Integration/AdsBDemodulatorWiringTests.cs
git commit -m "feat(api): wire ADS-B demodulator + decode stage into live pipelines

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Manual end-to-end verification (post-Task 5, controller/engineer)

Automated tests prove the demod, the pipeline stage (with real components), and DI registration. To confirm the live socket path end-to-end: run the API with the ADS-B preset tuned (1090 MHz @ 2 MS/s) against a real HackRF with aircraft overhead, and confirm the Devices page lists aircraft (ICAO/callsign) within a poll cycle. Alternatively replay a captured 1090 MHz `.iq` via `Ingestion__Enabled=true` + `Ingestion__IqFile=...` + `Ingestion__CenterFreqHz=1090000000`. This is a real-hardware/field step, not a CI gate.

## Self-Review

**1. Spec coverage:**
- `AdsBDemodulator` (self-gate, envelope, preamble, PPM slice) → Task 3. ✓
- Pipeline decode stage (step 4b, group-by-ICAO, resolve, upsert, DeviceDetermined) → Task 4. ✓
- Device write path (`IDeviceRepository.Upsert`, in-memory + EF) → Task 1. ✓
- DI registration + both build sites → Task 5. ✓
- Reuse of `AdsBDecoder`/`DeviceResolver`/`DecoderRegistry` unchanged → Tasks 3–5 consume them; none modified. ✓
- Invariants (L1/L2-L3/P5/P4) → Global Constraints + demod is pure DSP + `DeterministicGuid` device ids (via the untouched resolver). ✓
- Testing: synthetic closed-loop (`AdsBModulator`) → Task 2; demod/edge → Task 3; pipeline integration → Task 4; upsert → Task 1; DI → Task 5. ✓
- Demo-seed clearing unchanged (already handles devices) → no task needed, correct. ✓
- Out-of-scope items (CPR/map, cross-block carry-over, `Signal.DeviceId`, short squitters) → not implemented; Task 3 drops boundary frames by design; no map/position code. ✓

**2. Placeholder scan:** No TBD/TODO/"handle errors"/"similar to Task N". Every code step shows full code. The one cross-reference (FixedClock/StaticPositionSource in Task 4) points at existing shared test doubles with explicit fallback instructions, not omitted code. ✓

**3. Type consistency:** `Upsert(Device)` signature identical across interface, in-memory, EF, and all tests. Pipeline's 4 new params (`demodulators`/`registry`/`resolver`/`devices`) named identically in Task 4 definition and Task 5 call sites. `AdsBModulator.Modulate(...)` signature identical across Tasks 2/3/4. Protocol string `"ADS-B"` consistent with `AdsBDecoder`. Golden hex identical everywhere. `IDemodulator.Demodulate(IqBlock, FeatureVector)` matches the Domain seam and Task 3 impl. ✓
