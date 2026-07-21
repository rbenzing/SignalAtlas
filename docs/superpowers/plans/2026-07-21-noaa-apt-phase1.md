# NOAA APT Weather-Satellite Subsystem — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decode a live 137 MHz NOAA APT pass into a greyscale image, surface the satellite as a `Device` with decoded info, and show the image in the Devices detail drawer — fully offline, receive-only, deterministic.

**Architecture:** A new **parallel pass-decoder seam** (`ISatelliteImageDecoder`) runs alongside the existing frame decoders in `IngestionPipeline`. `FmDiscriminator` (Processing) turns IQ into audio; `AptDecoder` (Decode) turns audio into a growing image + a `SatellitePass`; `GreyscalePng` encodes it dependency-free; `AptImageStore` (Persistence) holds the bytes in-memory; a gated `GET /devices/{id}/image` serves them; the Devices drawer renders them.

**Tech Stack:** .NET 10 (C#), xUnit, React/TS + MUI + Vitest. No new NuGet or npm dependencies.

## Global Constraints

- **Receive-only (L1):** no transmit path; DSP reads only magnitude/phase. Do not add any TX member.
- **Deterministic core (P5):** in `src/`, no `DateTime.Now`, `Guid.NewGuid()`, or unseeded `Random`. Use `IClock`, `DeterministicGuid.From(string)`, seeded RNG. Same IQ → identical image bytes + device id.
- **Explainable-only (P4/P6):** the satellite `Device` carries **non-empty** `Evidence`.
- **Invariant #3 carve-out:** decoded image content is **in-memory only** — never persisted (no DB, no disk), never egressed; the M13 `EgressGuard` stays in force. Only device *metadata* persists.
- **Auth-gated `/api/v1` (invariant #5):** the image endpoint lives inside the `api` route group.
- **Stateful decoders are `AddTransient` (landmine #10);** the image store is a bounded thread-safe **singleton**.
- **No new dependencies.** PNG encoding is hand-rolled (greyscale, zlib stored blocks). No System.Drawing/ImageSharp.
- **Frontend/API type drift (landmine #1):** the image endpoint returns raw `image/png`, not the JSON envelope. Match that in the client.
- **`namespace SignalAtlas.Classification` shadows `Classification`** — not relevant here, but if touched, fully-qualify.

## Constants (use verbatim)

- `AptBands`: NOAA APT center frequencies (Hz) → name: `137_100_000 → "NOAA-19"`, `137_620_000 → "NOAA-15"`, `137_912_500 → "NOAA-18"`. Band match tolerance: **±25 kHz** around each center.
- `AudioRateHz = 20_000`. `FmDiscriminator` decimation = `block.SampleRateHz / AudioRateHz` (2 MS/s → 100). Require `SampleRateHz % AudioRateHz == 0`, else the decoder declines the block.
- APT: subcarrier `2400 Hz`; word/pixel rate `4160 words/sec`; line rate `2 lines/sec`; **line width = 2080 words**.
- `MinLinesToEmit = 4` (≈2 s — emit the device once this many lines are assembled).
- `MaxImageLines = 3000` (a full ~15-min pass ≈ 1800; cap so a stuck stream can't grow unbounded).
- `AptImageStore` capacity = `16` (LRU).

---

## Task 1: Domain contracts (the seam)

**Files:**
- Create: `src/SignalAtlas.Domain/SatelliteImaging.cs`
- Test: `tests/SignalAtlas.Tests.Unit/SatelliteImagingContractTests.cs`

**Interfaces:**
- Consumes: existing `Device`, `IqBlock`, `FeatureVector` (all in `SignalAtlas.Domain`).
- Produces (every later task consumes these EXACT signatures):
```csharp
namespace SignalAtlas.Domain;

/// <summary>Result of an in-progress/complete APT pass: the satellite Device plus the decoded
/// greyscale image as a PNG (in-memory content only — never persisted, never egressed, per the
/// invariant-#3 carve-out for public non-personal broadcast imagery).</summary>
public sealed record SatellitePass(Device Device, byte[] PngImage, int Lines, double SyncQuality);

/// <summary>Parallel pass-decoder seam (SPEC §8.4 / NOAA APT Phase 1). Stateful per stream: it
/// accumulates one image over a multi-minute pass, so DI registration MUST be Transient
/// (landmine #10). Returns null until sync locks and enough lines are assembled.</summary>
public interface ISatelliteImageDecoder
{
    bool AppliesTo(long centerFreqHz);
    SatellitePass? Accept(IqBlock block, FeatureVector features, DateTimeOffset time);
}

/// <summary>Bounded in-memory store of decoded APT images keyed by device id. Thread-safe singleton.
/// Content is NEVER persisted to disk/DB (invariant-#3 carve-out).</summary>
public interface IAptImageStore
{
    void Put(string deviceId, byte[] png);
    byte[]? Get(string deviceId);
}
```

- [ ] **Step 1: Write the failing test**
```csharp
using SignalAtlas.Domain;
using Xunit;

public class SatelliteImagingContractTests
{
    [Fact]
    public void SatellitePass_CarriesImageAndMetadata()
    {
        var dev = new Device("id", "Satellite", "NOAA-19", new Dictionary<string, string>(),
            null, "NOAA-APT", 0.9, [new EvidenceItem("apt_sync", "locked", 0.9)]);
        var pass = new SatellitePass(dev, [1, 2, 3], 4, 0.9);
        Assert.Equal("NOAA-19", pass.Device.PrimaryIdentifier);
        Assert.Equal(3, pass.PngImage.Length);
        Assert.Equal(4, pass.Lines);
    }
}
```

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~SatelliteImagingContract"`
Expected: FAIL — `SatellitePass` / interfaces do not exist (compile error).

- [ ] **Step 3: Create `SatelliteImaging.cs`** with the exact three declarations from the Interfaces block above.

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~SatelliteImagingContract"`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Domain/SatelliteImaging.cs tests/SignalAtlas.Tests.Unit/SatelliteImagingContractTests.cs
git commit -m "feat(domain): NOAA APT seam contracts (SatellitePass, ISatelliteImageDecoder, IAptImageStore)"
```

---

## Task 2: FmDiscriminator (Processing) — PARALLEL after Task 1

**Files:**
- Create: `src/SignalAtlas.Processing/FmDiscriminator.cs`
- Test: `tests/SignalAtlas.Tests.Unit/FmDiscriminatorTests.cs`

**Interfaces:**
- Consumes: `IqBlock` (Domain).
- Produces:
```csharp
namespace SignalAtlas.Processing;
public sealed class FmDiscriminator
{
    public FmDiscriminator(int decimation);   // e.g. 2_000_000/20_000 = 100
    public int Decimation { get; }
    /// <summary>FM-discriminate this block (instantaneous frequency = phase difference of successive
    /// complex samples) then box-average-decimate by Decimation. Carries the last sample's phase
    /// across calls so block boundaries don't drop a sample. Deterministic; returns audio samples
    /// (radians/sample, roughly [-pi, pi]).</summary>
    public float[] Process(IqBlock block);
}
```

**Algorithm (implement exactly):**
1. For each complex sample `s[k] = I[k] + j·Q[k]`, instantaneous phase = `atan2(Q[k], I[k])`.
2. Discriminated value `d[k] = wrapToPi(phase[k] - prevPhase)` where `prevPhase` starts at the instance field `_lastPhase` (init 0.0 in ctor) and is updated to `phase[last]` at the end of each `Process`. `wrapToPi(x)` adds/subtracts `2π` until `x ∈ (-π, π]`.
3. Decimate: output sample `m` = mean of `d[m*Decimation .. m*Decimation+Decimation-1]`. Output length = `block.SampleCount / Decimation` (integer floor; carry no partial group — a leftover < Decimation samples is dropped, acceptable at block scale).

- [ ] **Step 1: Write the failing test** (a pure 3 kHz FM tone on a 2 MS/s carrier must discriminate to a constant positive audio level; decimation ×100 → 20 kHz):
```csharp
using SignalAtlas.Domain;
using SignalAtlas.Processing;
using Xunit;

public class FmDiscriminatorTests
{
    // Synthesize IQ whose instantaneous frequency is a constant toneHz offset: phase advances by
    // 2*pi*toneHz/fs each sample. The discriminator output should be ~constant = 2*pi*toneHz/fs.
    private static IqBlock FmTone(double toneHz, int fs, int n)
    {
        var i = new float[n]; var q = new float[n];
        double ph = 0, step = 2 * Math.PI * toneHz / fs;
        for (int k = 0; k < n; k++) { i[k] = (float)Math.Cos(ph); q[k] = (float)Math.Sin(ph); ph += step; }
        return new IqBlock(137_100_000, fs, i, q);
    }

    [Fact]
    public void Process_ConstantToneFrequency_ProducesConstantAudioLevel()
    {
        var d = new FmDiscriminator(100);
        var audio = d.Process(FmTone(3000, 2_000_000, 200_000));
        Assert.Equal(2000, audio.Length);           // 200000/100
        double expected = 2 * Math.PI * 3000 / 2_000_000;
        double mean = audio.Skip(5).Take(1990).Average();
        Assert.InRange(mean, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void Process_IsDeterministic()
    {
        var block = FmTone(3000, 2_000_000, 100_000);
        Assert.Equal(new FmDiscriminator(100).Process(block), new FmDiscriminator(100).Process(block));
    }
}
```

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~FmDiscriminator"`
Expected: FAIL — class does not exist.

- [ ] **Step 3: Implement `FmDiscriminator.cs`** per the Algorithm above (instance field `double _lastPhase`; `Decimation` stored; `Process` returns `float[]`).

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~FmDiscriminator"`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Processing/FmDiscriminator.cs tests/SignalAtlas.Tests.Unit/FmDiscriminatorTests.cs
git commit -m "feat(processing): FM discriminator (IQ -> decimated audio) for APT"
```

---

## Task 3: GreyscalePng encoder (Processing) — PARALLEL after Task 1

**Files:**
- Create: `src/SignalAtlas.Processing/GreyscalePng.cs`
- Test: `tests/SignalAtlas.Tests.Unit/GreyscalePngTests.cs`

**Interfaces:**
- Produces:
```csharp
namespace SignalAtlas.Processing;
public static class GreyscalePng
{
    /// <summary>Encode row-major 8-bit greyscale pixels (length == width*height) as an 8-bit
    /// greyscale PNG. Dependency-free: zlib "stored" (uncompressed) deflate blocks + Adler-32, so it
    /// needs no System.Drawing/ImageSharp and is byte-for-byte deterministic (P5). Each PNG row is
    /// prefixed with filter byte 0 (None).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height);
}
```

**Algorithm (implement exactly):**
- PNG signature: `89 50 4E 47 0D 0A 1A 0A`.
- IHDR chunk: width, height (big-endian uint32), bit depth `8`, colour type `0` (greyscale), compression `0`, filter `0`, interlace `0`.
- IDAT: build the raw stream = for each row, one `0x00` filter byte then `width` pixel bytes. Wrap in a zlib stream: 2-byte header `0x78 0x01`, then DEFLATE **stored** blocks (each ≤ 65535 bytes: 1 byte `BFINAL`, 2 bytes `LEN` little-endian, 2 bytes `~LEN`, then the literal bytes; last block sets BFINAL=1), then 4-byte big-endian Adler-32 of the raw stream.
- IEND chunk (empty).
- Every chunk: `[length:uint32 BE][type:4 ascii][data][CRC-32 of type+data:uint32 BE]`. Implement CRC-32 (poly `0xEDB88320`) and Adler-32 inline.

- [ ] **Step 1: Write the failing test**
```csharp
using SignalAtlas.Processing;
using Xunit;

public class GreyscalePngTests
{
    [Fact]
    public void Encode_ProducesValidPngSignatureAndSize()
    {
        var px = new byte[4 * 3]; // 4x3
        for (int k = 0; k < px.Length; k++) px[k] = (byte)(k * 10);
        var png = GreyscalePng.Encode(px, 4, 3);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
        // IHDR type at offset 12..16
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.EndsWith("IEND", BitConverter.ToString(png)); // sanity: contains IEND near the end
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var px = new byte[64];
        for (int k = 0; k < px.Length; k++) px[k] = (byte)k;
        Assert.Equal(GreyscalePng.Encode(px, 8, 8), GreyscalePng.Encode(px, 8, 8));
    }

    [Fact]
    public void Encode_RejectsMismatchedDimensions()
    {
        Assert.Throws<ArgumentException>(() => GreyscalePng.Encode(new byte[10], 4, 3));
    }
}
```
> Note: the `EndsWith("IEND")` assertion checks the hex dump ends with the IEND type+CRC region; if that reads awkwardly in review, assert instead that `System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4) == "IEND"`. Use whichever the implementer verifies passes — the intent is "IEND is the final chunk."

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~GreyscalePng"`
Expected: FAIL — class does not exist.

- [ ] **Step 3: Implement `GreyscalePng.cs`** per the Algorithm (CRC-32 + Adler-32 inline; stored deflate blocks; throw `ArgumentException` when `pixels.Length != width*height`).

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~GreyscalePng"`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Processing/GreyscalePng.cs tests/SignalAtlas.Tests.Unit/GreyscalePngTests.cs
git commit -m "feat(processing): dependency-free deterministic greyscale PNG encoder"
```

---

## Task 4: AptImageStore (Persistence) — PARALLEL after Task 1

**Files:**
- Create: `src/SignalAtlas.Persistence/AptImageStore.cs`
- Test: `tests/SignalAtlas.Tests.Persistence/AptImageStoreTests.cs`

**Interfaces:**
- Consumes: `IAptImageStore` (Domain, Task 1).
- Produces: `public sealed class AptImageStore : IAptImageStore` with ctor `AptImageStore(int capacity = 16)`.

**Behavior:** thread-safe (single `lock`); LRU by insertion/refresh order; `Put` replaces an existing key's bytes and marks it most-recent; when count exceeds `capacity`, evict the least-recently-used key. `Get` returns the stored bytes (marking most-recent is NOT required on Get for Phase 1 — keep it simple: recency tracked on Put only) or `null`.

- [ ] **Step 1: Write the failing test**
```csharp
using SignalAtlas.Domain;
using SignalAtlas.Persistence;
using Xunit;

public class AptImageStoreTests
{
    [Fact]
    public void PutThenGet_ReturnsBytes()
    {
        IAptImageStore s = new AptImageStore();
        s.Put("a", [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Get("a"));
        Assert.Null(s.Get("missing"));
    }

    [Fact]
    public void Put_RefreshesBytesForSameKey()
    {
        IAptImageStore s = new AptImageStore();
        s.Put("a", [1]);
        s.Put("a", [9, 9]);
        Assert.Equal(new byte[] { 9, 9 }, s.Get("a"));
    }

    [Fact]
    public void Put_EvictsLeastRecentlyPutBeyondCapacity()
    {
        IAptImageStore s = new AptImageStore(capacity: 2);
        s.Put("a", [1]);
        s.Put("b", [2]);
        s.Put("c", [3]);      // evicts "a"
        Assert.Null(s.Get("a"));
        Assert.NotNull(s.Get("b"));
        Assert.NotNull(s.Get("c"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~AptImageStore"`
Expected: FAIL — class does not exist.

- [ ] **Step 3: Implement `AptImageStore.cs`** (a `Dictionary<string,byte[]>` + a `LinkedList<string>` recency queue under one lock, capacity-bounded; pattern mirrors the LinkedList eviction used elsewhere in Persistence).

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~AptImageStore"`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Persistence/AptImageStore.cs tests/SignalAtlas.Tests.Persistence/AptImageStoreTests.cs
git commit -m "feat(persistence): bounded thread-safe in-memory APT image store"
```

---

## Task 5: NOAA APT band preset (frontend) — PARALLEL after Task 1 (no backend dep)

**Files:**
- Modify: `web/src/sdr/bandPresets.ts` (add one band to the `bandPresets` array)
- Test: `web/src/sdr/bandPresets.test.ts` (add one assertion)

**Interfaces:** none new — reuses the existing `BandPreset` shape.

- [ ] **Step 1: Add the failing test** (in `bandPresets.test.ts`, inside the `activeBand` describe):
```ts
it("resolves the NOAA APT satellite band at 137.1 MHz", () => {
  expect(activeBand(137_100_000)?.key).toBe("noaa-apt");
});
```

- [ ] **Step 2: Run to verify it fails**
Run: `cd web && npx vitest run src/sdr/bandPresets.test.ts`
Expected: FAIL — no band contains 137.1 MHz.

- [ ] **Step 3: Add the band** to the `bandPresets` array in `bandPresets.ts` (place it before the 162 MHz `noaa-weather` voice band; they are unrelated):
```ts
{
  key: "noaa-apt",
  label: "NOAA APT weather sat 137 MHz",
  lowHz: 137_000_000,
  highHz: 138_000_000,
  centerFreqHz: 137_100_000,
  sampleRateHz: 2 * MS, // APT needs ~40 kHz; 2 MS/s is the HackRF floor, decimated to 20 kHz audio backend-side
  channels: [
    { key: "noaa-19", label: "NOAA-19 · 137.100 MHz", centerFreqHz: 137_100_000, sampleRateHz: 2 * MS },
    { key: "noaa-15", label: "NOAA-15 · 137.620 MHz", centerFreqHz: 137_620_000, sampleRateHz: 2 * MS },
    { key: "noaa-18", label: "NOAA-18 · 137.9125 MHz", centerFreqHz: 137_912_500, sampleRateHz: 2 * MS },
  ],
},
```

- [ ] **Step 4: Run to verify it passes**
Run: `cd web && npx vitest run src/sdr/bandPresets.test.ts`
Expected: PASS (existing HackRF-valid-rate/frequency-range/unique-key tests stay green; 137.9125 MHz is within 1 MHz–6 GHz and its rate is 2 MS/s).

- [ ] **Step 5: Commit**
```bash
git add web/src/sdr/bandPresets.ts web/src/sdr/bandPresets.test.ts
git commit -m "feat(web): NOAA APT 137 MHz satellite band preset (NOAA-15/18/19)"
```

---

## Task 6: AptDecoder + AptModulator test helper (Decode) — after Tasks 1,2,3

**Files:**
- Create: `src/SignalAtlas.Decode/AptDecoder.cs`
- Create: `tests/SignalAtlas.Tests.Unit/AptModulator.cs` (test helper — decoder inverse)
- Test: `tests/SignalAtlas.Tests.Unit/AptDecoderTests.cs`

**Interfaces:**
- Consumes: `ISatelliteImageDecoder`, `SatellitePass`, `Device`, `EvidenceItem`, `IqBlock`, `FeatureVector`, `DeterministicGuid.From` (Domain); `FmDiscriminator` (Task 2); `GreyscalePng.Encode` (Task 3).
- Produces: `public sealed class AptDecoder : ISatelliteImageDecoder`.

**Behavior (implement exactly):**
- `AppliesTo(centerFreqHz)`: true iff `centerFreqHz` is within ±25 kHz of one of the three `AptBands` centers.
- `Accept(block, features, time)`:
  - If `!AppliesTo(block.CenterFreqHz)` OR `block.SampleRateHz % 20000 != 0` → reset any pass state and return `null` (the retune/self-gate guard mirrors `AdsBDemodulator`).
  - Instantiate/keep an internal `FmDiscriminator(block.SampleRateHz / 20000)` (per-instance state; recreate if the rate changed).
  - `audio = disc.Process(block)` (20 kHz).
  - **AM envelope-detect the 2400 Hz subcarrier:** rectify (`abs`) then single-pole low-pass (`y += α(x−y)`, `α ≈ 0.15`) → brightness samples. Normalize to 0..255 using a running min/max (or a fixed gain if min/max not yet established) — deterministic given input.
  - **Sync + line assembly:** resample brightness to `4160` words/sec via a deterministic fractional accumulator (`wordPos += 4160/20000` per audio sample; emit a pixel when it crosses an integer). Accumulate pixels into the current line buffer (width `2080`). Detect APT sync-A (the 1040 Hz / 7-pulse pattern → 7 alternating high/low words at the line start) by correlation against the known sync template to align line starts; on a strong correlation, start a new line. Append completed lines to an internal `List<byte[]>` image (cap at `MaxImageLines` — stop appending past the cap).
  - Track `syncQuality` = normalized best sync correlation (0..1); track total `lines`.
  - Identify satellite from `block.CenterFreqHz` (nearest `AptBands` center).
  - When `lines >= MinLinesToEmit`: build `pixels` = concatenation of all line buffers (`width=2080`, `height=lines`); `png = GreyscalePng.Encode(pixels, 2080, lines)`.
    - `deviceId = DeterministicGuid.From($"NOAA-APT:{satName}:{_passStartTicks}").ToString()` where `_passStartTicks` is the `time.UtcTicks` captured on the FIRST accepted block of this pass (stable across the pass).
    - `evidence = [ new("satellite_freq", $"{MHz}", 1.0), new("apt_sync", "locked", syncQuality), new("subcarrier", "2400 Hz", 1.0) ]`.
    - `identifiers = { ["satellite"]=satName, ["frequencyMhz"]=mhzString, ["lines"]=lines.ToString(), ["passStart"]=time-of-pass-start ISO }`.
    - `device = new Device(deviceId, "Satellite", satName, identifiers, null, "NOAA-APT", syncQuality, evidence)`.
    - return `new SatellitePass(device, png, lines, syncQuality)`.
  - Else return `null`.

**AptModulator (test helper) — the inverse:** given a `byte[,] image` (height×2080), synthesize APT audio (each pixel → 2400 Hz tone amplitude, prefixed per line with the sync-A pattern), then FM-modulate to IQ at a given `sampleRateHz` (integrate the audio as instantaneous frequency deviation). Signature:
```csharp
public static class AptModulator
{
    // rows: greyscale rows, each length 2080. Returns one IqBlock at centerFreqHz/sampleRateHz.
    public static IqBlock Modulate(byte[][] rows, long centerFreqHz = 137_100_000, int sampleRateHz = 2_000_000);
}
```

- [ ] **Step 1: Write the failing test** (round-trip a small known gradient; assert a Device with the right satellite, non-empty evidence, and that the decoded image correlates with the source):
```csharp
using SignalAtlas.Decode;
using SignalAtlas.Domain;
using Xunit;

public class AptDecoderTests
{
    private static FeatureVector Features(long f) => new(f, 0, 0, 0, 0, 0, 0, null);

    private static byte[][] Gradient(int lines)
    {
        var rows = new byte[lines][];
        for (int r = 0; r < lines; r++) { rows[r] = new byte[2080]; for (int c = 0; c < 2080; c++) rows[r][c] = (byte)((c * 255) / 2079); }
        return rows;
    }

    [Fact]
    public void Accept_SyntheticPass_EmitsSatelliteDeviceWithImage()
    {
        var rows = Gradient(8);
        var block = AptModulator.Modulate(rows, centerFreqHz: 137_100_000, sampleRateHz: 2_000_000);
        var d = new AptDecoder();
        var pass = d.Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);

        Assert.NotNull(pass);
        Assert.Equal("Satellite", pass!.Device.DeviceType);
        Assert.Equal("NOAA-APT", pass.Device.Protocol);
        Assert.Equal("NOAA-19", pass.Device.PrimaryIdentifier);
        Assert.NotEmpty(pass.Device.Evidence);                 // P4/P6
        Assert.True(pass.Lines >= 4);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, pass.PngImage.Take(4).ToArray()); // PNG magic
    }

    [Fact]
    public void AppliesTo_OnlyNoaaAptCenters()
    {
        var d = new AptDecoder();
        Assert.True(d.AppliesTo(137_100_000));
        Assert.True(d.AppliesTo(137_912_500));
        Assert.False(d.AppliesTo(915_000_000));
    }

    [Fact]
    public void Accept_IsDeterministic()
    {
        var block = AptModulator.Modulate(Gradient(6));
        var a = new AptDecoder().Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);
        var b = new AptDecoder().Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);
        Assert.Equal(a!.PngImage, b!.PngImage);        // byte-identical (P5)
        Assert.Equal(a.Device.Id, b.Device.Id);
    }
}
```

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~AptDecoder"`
Expected: FAIL — `AptDecoder`/`AptModulator` do not exist.

- [ ] **Step 3: Implement `AptModulator.cs`** (test helper) then `AptDecoder.cs` per Behavior. Tune the sync template + envelope `α` until the round-trip test passes; the modulator and decoder must agree on the sync-A pattern and word rate.

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~AptDecoder"`
Expected: PASS (all three).

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Decode/AptDecoder.cs tests/SignalAtlas.Tests.Unit/AptDecoderTests.cs tests/SignalAtlas.Tests.Unit/AptModulator.cs
git commit -m "feat(decode): NOAA APT decoder (audio -> synced greyscale image -> satellite Device)"
```

---

## Task 7: Pipeline wiring + DI + invariant doc — after Tasks 1,2,3,4,6

**Files:**
- Modify: `src/SignalAtlas.Pipeline/IngestionPipeline.cs` (ctor param + per-block branch)
- Modify: `src/SignalAtlas.Api/Program.cs` (DI registration)
- Modify: `src/SignalAtlas.Api/PipelineHostedService.cs` (build-site thread-through)
- Modify: `src/SignalAtlas.Api/IqIngressEndpoint.cs` (build-site thread-through)
- Modify: `.claude/CLAUDE.md` (invariant #3 carve-out) and `docs/SPEC.md` (§8.4 note)
- Test: `tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs` (add a case)

**Interfaces:**
- Consumes: `ISatelliteImageDecoder`, `IAptImageStore` (Task 1), `AptDecoder` (Task 6), `AptImageStore` (Task 4).
- Produces: `IngestionPipeline` gains two optional ctor params: `ISatelliteImageDecoder? satDecoder = null, IAptImageStore? aptImages = null` (appended AFTER `cpr` so all existing call sites keep compiling).

**Pipeline branch (add inside `Run`'s per-block loop, after the ADS-B decode block, guarded like it):**
```csharp
// NOAA APT (Phase 1): parallel pass-decoder. Content held in-memory only (invariant-#3 carve-out).
if (_satDecoder is not null && _aptImages is not null && _satDecoder.AppliesTo(block.CenterFreqHz))
{
    var pass = _satDecoder.Accept(block, features, obs.Time);
    if (pass is not null)
    {
        _aptImages.Put(pass.Device.Id, pass.PngImage);
        _devices?.Upsert(pass.Device);
        _notifier.DeviceDetermined(pass.Device);
    }
}
```

- [ ] **Step 1: Write the failing test** (append to `IngestionPipelineDecodeTests`; feed a synthetic APT block through the full pipeline and assert the device lands + image stored):
```csharp
[Fact]
public void Run_NoaaAptPass_StoresImageAndDeterminesSatelliteDevice()
{
    var rows = new byte[8][];
    for (int r = 0; r < 8; r++) { rows[r] = new byte[2080]; for (int c = 0; c < 2080; c++) rows[r][c] = (byte)(c & 0xFF); }
    var block = SignalAtlas.Decode.AptModulator.Modulate(rows, 137_100_000, 2_000_000);

    var devices = new InMemoryDeviceRepository(new DeviceResolver(new OuiLookup()));
    var images = new AptImageStore();
    var pipeline = /* build IngestionPipeline with satDecoder: new AptDecoder(), aptImages: images,
                      devices: devices, and the usual processor/classifier/correlation/anomaly/obs/signals */;

    pipeline.Run(new OneBlockSource(block));   // a test ISampleSource yielding the single block

    var sat = devices.GetDevices(50).FirstOrDefault(d => d.Protocol == "NOAA-APT");
    Assert.NotNull(sat);
    Assert.Equal("NOAA-19", sat!.PrimaryIdentifier);
    Assert.NotNull(images.Get(sat.Id));
}
```
> Reuse the existing test's pipeline-construction helper and any `OneBlockSource`/single-block `ISampleSource` already present in `IngestionPipelineDecodeTests`; if none exists, add a tiny local `ISampleSource` that yields the one block. Match the exact constructor argument names shown in `IngestionPipeline` (ctor listed in the plan header context).

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~IngestionPipelineDecode"`
Expected: FAIL — the new ctor params/branch don't exist.

- [ ] **Step 3: Implement:**
  1. Add the two optional ctor params + backing fields `_satDecoder`, `_aptImages` to `IngestionPipeline` and the per-block branch above.
  2. `Program.cs`: after the ADS-B/CPR registrations (near line 101), add:
     ```csharp
     builder.Services.AddTransient<ISatelliteImageDecoder, SignalAtlas.Decode.AptDecoder>();
     builder.Services.AddSingleton<IAptImageStore, SignalAtlas.Persistence.AptImageStore>();
     ```
  3. `PipelineHostedService.cs` and `IqIngressEndpoint.cs`: at each `new IngestionPipeline(...)` build site, pass `satDecoder: sp.GetService<ISatelliteImageDecoder>(), aptImages: sp.GetService<IAptImageStore>()`.
  4. `.claude/CLAUDE.md` invariant #3: append the carve-out sentence (public non-personal NOAA APT imagery, in-memory only, never persisted, never egressed, EgressGuard still in force). `docs/SPEC.md` §8.4: one line noting the APT pass-decoder seam + the in-memory image store.

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"`
Expected: PASS (new case + the whole non-docker suite stays green).

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Pipeline/IngestionPipeline.cs src/SignalAtlas.Api/Program.cs src/SignalAtlas.Api/PipelineHostedService.cs src/SignalAtlas.Api/IqIngressEndpoint.cs .claude/CLAUDE.md docs/SPEC.md tests/SignalAtlas.Tests.Unit/IngestionPipelineDecodeTests.cs
git commit -m "feat(pipeline): wire NOAA APT pass-decoder + image store; document invariant-3 carve-out"
```

---

## Task 8: Image API endpoint — after Tasks 4,7

**Files:**
- Modify: `src/SignalAtlas.Api/Program.cs` (add the route inside the `api` group)
- Test: `tests/SignalAtlas.Tests.Integration/AptImageEndpointTests.cs`

**Interfaces:**
- Consumes: `IAptImageStore` (Task 1/4).
- Produces: `GET /api/v1/devices/{id}/image` → `200 image/png` (raw bytes) or `404`.

**Endpoint (add near the `/devices` route, inside the `api` group so it is auth-gated):**
```csharp
api.MapGet("/devices/{id}/image", IResult (string id, IAptImageStore images, IAuditLog audit, HttpContext ctx) =>
{
    audit.Record("local-operator", "read:device-image", id);
    var png = images.Get(id);
    return png is null
        ? Results.NotFound()
        : Results.File(png, "image/png");
});
```

- [ ] **Step 1: Write the failing test** (put an image in the store via the app's `IAptImageStore` singleton, then GET it):
```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Domain;
using Xunit;

public class AptImageEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetDeviceImage_ReturnsPngWhenPresent_404WhenNot()
    {
        var app = factory.WithWebHostBuilder(_ => { });
        var store = app.Services.GetRequiredService<IAptImageStore>();
        store.Put("sat-1", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var client = app.CreateClient();

        var ok = await client.GetAsync("/api/v1/devices/sat-1/image");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);

        var missing = await client.GetAsync("/api/v1/devices/nope/image");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~AptImageEndpoint"`
Expected: FAIL — route not mapped (404 for the present case, or the `IAptImageStore` singleton not shared).

- [ ] **Step 3: Add the endpoint** to `Program.cs` inside the `api` group.

- [ ] **Step 4: Run to verify it passes**
Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~AptImageEndpoint"`
Expected: PASS. Also run the auth-gate contract test to confirm the new route is gated:
`dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~Auth"` → PASS (the enumerated-endpoints test must still pass with the new route inside `/api/v1`).

- [ ] **Step 5: Commit**
```bash
git add src/SignalAtlas.Api/Program.cs tests/SignalAtlas.Tests.Integration/AptImageEndpointTests.cs
git commit -m "feat(api): gated GET /devices/{id}/image serving decoded APT PNG"
```

---

## Task 9: Devices drawer image panel (frontend) — after Task 8

**Files:**
- Modify: `web/src/api.ts` (add `getDeviceImage`)
- Modify: `web/src/views/Devices.tsx` (image panel for NOAA-APT devices)
- Test: `web/src/api.test.ts` (add `getDeviceImage` test) — create if absent
- Test: `web/src/views/Devices.test.tsx` (image panel renders for a satellite device) — create if absent

**Interfaces:**
- Consumes: `GET /api/v1/devices/{id}/image` (Task 8).
- Produces: `export async function getDeviceImage(id: string): Promise<Blob | null>`.

- [ ] **Step 1: Write the failing test** (api client):
```ts
import { describe, it, expect, vi, afterEach } from "vitest";
import { getDeviceImage } from "./api";

afterEach(() => vi.unstubAllGlobals());

describe("getDeviceImage", () => {
  it("returns a Blob on 200 and null on 404", async () => {
    const blob = new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: true, blob: () => Promise.resolve(blob) }));
    expect(await getDeviceImage("a")).toBe(blob);

    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false, status: 404 }));
    expect(await getDeviceImage("a")).toBeNull();
  });
});
```

- [ ] **Step 2: Run to verify it fails**
Run: `cd web && npx vitest run src/api.test.ts`
Expected: FAIL — `getDeviceImage` not exported.

- [ ] **Step 3: Implement** in `api.ts` (raw fetch — NOT `getEnvelope`, the response is binary):
```ts
/** Fetch a device's decoded image (e.g. NOAA APT). Returns the PNG Blob, or null on 404/none. */
export async function getDeviceImage(id: string): Promise<Blob | null> {
  const resp = await fetch(`${BASE}/api/v1/devices/${encodeURIComponent(id)}/image`);
  if (!resp.ok) return null;
  return resp.blob();
}
```

- [ ] **Step 4: Run to verify it passes**
Run: `cd web && npx vitest run src/api.test.ts`
Expected: PASS.

- [ ] **Step 5: Add the drawer image panel** in `Devices.tsx`. Inside the drawer (after the Identifiers block, before Evidence), add a self-contained `SatelliteImage` component that, for `selected.protocol === "NOAA-APT"`, loads the blob into an object URL and renders it (revoking on cleanup):
```tsx
function SatelliteImage({ deviceId }: { deviceId: string }) {
  const [url, setUrl] = useState<string | null>(null);
  const [pending, setPending] = useState(true);
  useEffect(() => {
    let active = true;
    let objectUrl: string | null = null;
    getDeviceImage(deviceId).then((blob) => {
      if (!active) return;
      if (blob) { objectUrl = URL.createObjectURL(blob); setUrl(objectUrl); }
      setPending(false);
    });
    return () => { active = false; if (objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [deviceId]);
  return (
    <>
      <Divider sx={{ my: 1.5 }} />
      <Typography variant="subtitle2" sx={{ mb: 0.5 }}>Decoded image</Typography>
      {url ? (
        <Box component="img" src={url} alt="Decoded APT image"
          sx={{ width: "100%", imageRendering: "pixelated", borderRadius: 1, border: 1, borderColor: "divider" }} />
      ) : (
        <Typography variant="caption" color="text.secondary">{pending ? "Loading…" : "Image pending"}</Typography>
      )}
    </>
  );
}
```
Render it in the drawer with `{selected.protocol === "NOAA-APT" && <SatelliteImage deviceId={selected.id} />}`. Import `useEffect`, `getDeviceImage`.

- [ ] **Step 6: Add the view test** (`Devices.test.tsx`): mock `getDevices` to return one `NOAA-APT` device and `getDeviceImage` to resolve a blob; click the row; assert an `img[alt="Decoded APT image"]` appears. (Mirror the mocking style used in existing view tests; stub `URL.createObjectURL`.)

- [ ] **Step 7: Run tests + build**
Run: `cd web && npx vitest run && npm run build`
Expected: PASS + clean build.

- [ ] **Step 8: Commit**
```bash
git add web/src/api.ts web/src/api.test.ts web/src/views/Devices.tsx web/src/views/Devices.test.tsx
git commit -m "feat(web): show decoded NOAA APT image in the device detail drawer"
```

---

## Task 10: Docs (README/OPERATIONS) — after Task 9

**Files:**
- Modify: `README.md` (Current limitations / feature note), `docs/OPERATIONS.md` (§8 tuning — NOAA APT band note + Phase-1 image behavior).

- [ ] **Step 1: Update README** — add a short bullet: NOAA APT weather-satellite passes (137 MHz) decode to a greyscale image shown in the device drawer; the image is held in-memory only (never persisted/egressed); georeferenced map placement is a deferred Phase 2.
- [ ] **Step 2: Update OPERATIONS §8** — note the NOAA APT 137 MHz band selects the pass-decoder; image is in-memory only; served via the gated `GET /devices/{id}/image`.
- [ ] **Step 3: Commit**
```bash
git add README.md docs/OPERATIONS.md
git commit -m "docs: NOAA APT Phase 1 (image-in-drawer, in-memory-only, Phase 2 georef deferred)"
```

---

## Execution / worktree strategy

- **Merge order:** Task 1 first (merge to `main`). Then **parallel worktrees** for Tasks 2, 3, 4, 5 (disjoint files: Processing×2, Persistence, web) — merge each as it passes review. Then **sequential** Tasks 6 → 7 → 8 → 9 → 10 (each depends on the prior being on `main`).
- Every task rebases on the latest `main` before merging so the shared `.slnx`/`Program.cs` edits (Tasks 7, 8) integrate cleanly.
- A green build is not proof the pieces fit (landmine #1): after Task 9, run the API + web and click a NOAA-APT device to confirm the image renders end-to-end.
