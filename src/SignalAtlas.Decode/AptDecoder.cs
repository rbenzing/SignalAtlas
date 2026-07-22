using SignalAtlas.Domain;
using SignalAtlas.Processing;

namespace SignalAtlas.Decode;

/// <summary>
/// NOAA APT (Automatic Picture Transmission) satellite-imagery decoder (SPEC §8.4 / NOAA APT
/// Phase 1). Turns 137 MHz FM-modulated IQ into a scanned greyscale image: FM-discriminate to
/// audio -&gt; AM envelope-detect the 2400 Hz subcarrier -&gt; resample to the 4160 words/sec word
/// rate -&gt; correlate against the APT sync-A template to find line starts -&gt; assemble 2080-wide
/// scan lines -&gt; PNG-encode. Metadata-only (L2/L3): only the public broadcast image content and
/// satellite identity are produced, never persisted (invariant-#3 carve-out — the PNG lives only
/// on the returned <see cref="SatellitePass"/>).
///
/// STATEFUL streaming decoder (per-stream instance, mirrors <c>AdsBDemodulator</c>/landmine #10):
/// it accumulates one image across a multi-minute pass, so DI registration (a later task) MUST be
/// Transient, never Singleton. A block that doesn't apply (wrong frequency or non-multiple-of-
/// 20kHz sample rate) resets all pass state — that block cannot belong to an in-progress pass.
/// Pure DSP: deterministic (P5), receive-only (L1), no wall clock, no RNG.
/// </summary>
public sealed class AptDecoder : ISatelliteImageDecoder
{
    private const double WordRate = 4160.0;
    private const double AudioRate = 20_000.0;
    private const int LineWidth = 2080;
    private const double EnvelopeAlpha = 0.35; // single-pole low-pass on the rectified subcarrier
    private const double SyncThreshold = 0.55; // normalized (0..1) correlation to lock a line start
    private const int MinLinesToEmit = 4;
    private const int MaxImageLines = 1200; // ~10 minutes of APT at 2 lines/sec

    // Sync-A template: 7 alternating high/low words (+1/-1) at the start of every line. Must match
    // AptModulator's SyncA polarity exactly (255,0,255,0,255,0,255 -> +1,-1,+1,-1,+1,-1,+1).
    private static readonly double[] SyncTemplate = { 1, -1, 1, -1, 1, -1, 1 };

    private static readonly (long CenterHz, string Name)[] AptBands =
    {
        (137_100_000, "NOAA-19"),
        (137_620_000, "NOAA-15"),
        (137_912_500, "NOAA-18"),
    };
    private const long ToleranceHz = 25_000;

    // --- per-pass mutable state ---
    private FmDiscriminator? _disc;
    private int _discDecimation;

    private bool _envelopeInit;
    private double _envelope;

    private double _wordPos;
    private double _wordSum;
    private int _wordSampleCount;

    private readonly double[] _syncRing = new double[7];
    private int _syncRingPos;
    private int _syncRingCount;

    private bool _inLine;
    private byte[]? _currentLine;
    private int _currentLinePos;

    private readonly List<byte[]> _lines = new();
    private double _bestSyncQuality;

    private double _runningMin = double.NaN;
    private double _runningMax = double.NaN;

    private bool _havePass;
    private long _passStartTicks;
    private long _lastCenterHz;

    public bool AppliesTo(long centerFreqHz)
    {
        foreach (var band in AptBands)
            if (Math.Abs(centerFreqHz - band.CenterHz) <= ToleranceHz) return true;
        return false;
    }

    public SatellitePass? Accept(IqBlock block, FeatureVector features, DateTimeOffset time)
    {
        if (!AppliesTo(block.CenterFreqHz) || block.SampleRateHz % (int)AudioRate != 0)
        {
            ResetPass();
            return null;
        }

        // Retune guard: a block for a different satellite band must not be spliced onto an
        // in-progress pass's accumulated lines.
        if (_havePass && block.CenterFreqHz != _lastCenterHz)
            ResetPass();
        _lastCenterHz = block.CenterFreqHz;

        int decimation = block.SampleRateHz / (int)AudioRate;
        if (_disc is null || _discDecimation != decimation)
        {
            _disc = new FmDiscriminator(decimation);
            _discDecimation = decimation;
        }

        if (!_havePass)
        {
            _passStartTicks = time.UtcTicks;
            _havePass = true;
        }

        float[] audio = _disc.Process(block);
        for (int m = 0; m < audio.Length; m++)
        {
            double rectified = Math.Abs(audio[m]);
            if (!_envelopeInit) { _envelope = rectified; _envelopeInit = true; }
            else _envelope += EnvelopeAlpha * (rectified - _envelope);

            _wordSum += _envelope;
            _wordSampleCount++;
            _wordPos += WordRate / AudioRate;
            if (_wordPos >= 1.0)
            {
                _wordPos -= 1.0;
                double wordVal = _wordSampleCount > 0 ? _wordSum / _wordSampleCount : 0.0;
                _wordSum = 0.0;
                _wordSampleCount = 0;
                ProcessWord(wordVal);
            }
        }

        if (_lines.Count < MinLinesToEmit)
            return null;

        long satCenter = NearestBandCenter(block.CenterFreqHz, out string satName);
        int lines = _lines.Count;

        var pixels = new byte[lines * LineWidth];
        for (int r = 0; r < lines; r++)
            Array.Copy(_lines[r], 0, pixels, r * LineWidth, LineWidth);

        byte[] png = GreyscalePng.Encode(pixels, LineWidth, lines);

        double mhz = satCenter / 1_000_000.0;
        string deviceId = DeterministicGuid.From($"NOAA-APT:{satName}:{_passStartTicks}").ToString();

        var evidence = new List<EvidenceItem>
        {
            new("satellite_freq", $"{mhz}", 1.0),
            new("apt_sync", "locked", _bestSyncQuality),
            new("subcarrier", "2400 Hz", 1.0),
        };

        var passStart = new DateTimeOffset(_passStartTicks, TimeSpan.Zero);
        var identifiers = new Dictionary<string, string>
        {
            ["satellite"] = satName,
            ["frequencyMhz"] = mhz.ToString(),
            ["lines"] = lines.ToString(),
            ["passStart"] = passStart.ToString("o"),
        };

        var device = new Device(deviceId, "Satellite", satName, identifiers, null, "NOAA-APT",
            _bestSyncQuality, evidence);

        return new SatellitePass(device, png, lines, _bestSyncQuality);
    }

    private void ProcessWord(double wordVal)
    {
        if (double.IsNaN(_runningMin) || wordVal < _runningMin) _runningMin = wordVal;
        if (double.IsNaN(_runningMax) || wordVal > _runningMax) _runningMax = wordVal;

        byte normalized;
        if (_runningMax > _runningMin)
        {
            double t = (wordVal - _runningMin) / (_runningMax - _runningMin);
            normalized = (byte)Math.Clamp(Math.Round(t * 255.0), 0.0, 255.0);
        }
        else
        {
            normalized = 128; // fixed-gain fallback until the range is established
        }

        _syncRing[_syncRingPos] = normalized;
        _syncRingPos = (_syncRingPos + 1) % _syncRing.Length;
        if (_syncRingCount < _syncRing.Length) _syncRingCount++;

        if (_inLine)
        {
            _currentLine![_currentLinePos++] = normalized;
            if (_currentLinePos >= LineWidth)
            {
                if (_lines.Count < MaxImageLines) _lines.Add(_currentLine);
                _inLine = false;
                _currentLine = null;
                _currentLinePos = 0;
            }
            return; // never search for a new sync mid-line
        }

        if (_syncRingCount < _syncRing.Length)
            return;

        double quality = SyncCorrelation();
        if (quality > _bestSyncQuality) _bestSyncQuality = quality;
        if (quality >= SyncThreshold)
        {
            _inLine = true;
            _currentLine = new byte[LineWidth];
            _currentLinePos = 0;
        }
    }

    // Normalized cross-correlation (0..1) of the last 7 words (oldest-first) against SyncTemplate.
    private double SyncCorrelation()
    {
        double sum = 0.0;
        for (int k = 0; k < _syncRing.Length; k++)
        {
            int idx = (_syncRingPos + k) % _syncRing.Length; // oldest first (pos is the next-write slot)
            double norm = (_syncRing[idx] - 128.0) / 128.0;  // roughly -1..1
            sum += SyncTemplate[k] * norm;
        }
        double raw = sum / _syncRing.Length; // roughly -1..1
        return Math.Clamp((raw + 1.0) / 2.0, 0.0, 1.0);
    }

    private static long NearestBandCenter(long centerFreqHz, out string satName)
    {
        long bestCenter = AptBands[0].CenterHz;
        string bestName = AptBands[0].Name;
        long bestDist = long.MaxValue;
        foreach (var band in AptBands)
        {
            long dist = Math.Abs(centerFreqHz - band.CenterHz);
            if (dist < bestDist) { bestDist = dist; bestCenter = band.CenterHz; bestName = band.Name; }
        }
        satName = bestName;
        return bestCenter;
    }

    private void ResetPass()
    {
        _disc = null;
        _discDecimation = 0;

        _envelopeInit = false;
        _envelope = 0.0;

        _wordPos = 0.0;
        _wordSum = 0.0;
        _wordSampleCount = 0;

        Array.Clear(_syncRing);
        _syncRingPos = 0;
        _syncRingCount = 0;

        _inLine = false;
        _currentLine = null;
        _currentLinePos = 0;

        _lines.Clear();
        _bestSyncQuality = 0.0;

        _runningMin = double.NaN;
        _runningMax = double.NaN;

        _havePass = false;
        _passStartTicks = 0;
        _lastCenterHz = 0;
    }
}
