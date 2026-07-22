using System.Globalization;
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
    // Envelope low-pass: a boxcar (moving-average) filter of exactly 25 raw audio samples (at the
    // 20 kHz audio rate). This is deliberately not a single-pole IIR: a full-wave-rectified 2400 Hz
    // tone carries ripple only at *even* multiples of 2400 Hz (4800, 9600, ...), and an N-sample
    // boxcar has exact spectral nulls at every multiple of AudioRate/N. With N=25, AudioRate/N =
    // 800 Hz, and every ripple harmonic (4800 = 6*800, 9600 = 12*800, ...) lands exactly on a null --
    // so a steady (non-transitioning) window recovers the true word amplitude with zero residual
    // ripple, to floating-point precision, regardless of the window's phase. That determinism is
    // what makes the round-trip's strict monotonic-non-decreasing property achievable: a boxcar
    // (or any further block-average) of a non-decreasing sequence is itself non-decreasing, so once
    // the ripple is exactly nulled, the reconstructed gradient cannot "wobble" within a line.
    private const int EnvelopeWindow = 25;
    private const double SyncThreshold = 0.55; // normalized (0..1) correlation to lock a line start
    private const int MinLinesToEmit = 4;
    private const int MaxImageLines = 3000; // > a full ~15-min pass (~1800 lines at 2 lines/sec)

    // Sync-A template: 7 alternating high/low words (+1/-1) at the start of every line, starting AND
    // ending low. Must match AptModulator's SyncA polarity exactly (0,255,0,255,0,255,0 -> -1,1,-1,1,-1,1,-1).
    private static readonly double[] SyncTemplate = { -1, 1, -1, 1, -1, 1, -1 };

    // Constant low "settle" words between sync-A and the first real video pixel (mirrors real APT's
    // sync -> space -> video structure). Right when correlation locks, the envelope boxcar's 25-sample
    // window still holds samples from the alternating sync-A pattern; without this gap, that stale
    // content would leak into the first video word(s) as a transient. 10 words (>= 48 audio samples,
    // comfortably more than the 25-sample window) guarantees the boxcar is fully flushed with settle-
    // only samples before real pixel collection starts. Must match AptModulator's SettleWords.
    private const int SettleWords = 10;

    // The correlation-based lock does not land on the mathematically exact ideal word (the boxcar's
    // own causal group delay -- ~(EnvelopeWindow-1)/2 raw samples -- shifts exactly where the
    // strongest match is seen), so the fixed 2080-word collection window is not naturally centered on
    // the encoder's true video content: it would run a couple words into whatever follows it. Rather
    // than chase that offset by hand-tuning SettleWords (which also controls how much ringing gets
    // flushed -- the two concerns fight each other), AptModulator pads each row with EdgeTrim extra
    // edge-duplicated words on both sides; the decoder collects the padded width and keeps only the
    // middle LineWidth words. The padding absorbs the lock-timing slop, whichever direction it falls.
    private const int EdgeTrim = 4;
    private const int PaddedLineWidth = LineWidth + 2 * EdgeTrim;

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

    private readonly double[] _envelopeRing = new double[EnvelopeWindow];
    private int _envelopeRingPos;
    private double _envelopeSum;

    private double _wordPos;
    private double _wordSum;
    private int _wordSampleCount;

    private readonly double[] _syncRing = new double[7];
    private int _syncRingPos;
    private int _syncRingCount;

    private bool _settling;
    private int _settleRemaining;

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
            // Always divide by the fixed window (not how many samples have flowed in yet): the
            // not-yet-written ring slots default to 0.0, i.e. "silence before the capture started",
            // which is exactly the right assumption -- and it means the very first ~25 samples get
            // the same exact-null boxcar averaging as steady state, instead of a ramp-up transient
            // that divides by a shrinking count and amplifies early samples out of proportion. That
            // ramp-up was distorting the correlation for the pass's very first sync-A specifically.
            double outgoing = _envelopeRing[_envelopeRingPos];
            _envelopeSum += rectified - outgoing;
            _envelopeRing[_envelopeRingPos] = rectified;
            _envelopeRingPos = (_envelopeRingPos + 1) % EnvelopeWindow;
            double envelope = _envelopeSum / EnvelopeWindow;

            _wordSum += envelope;
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
        string mhzString = mhz.ToString(CultureInfo.InvariantCulture);
        string deviceId = DeterministicGuid.From($"NOAA-APT:{satName}:{_passStartTicks}").ToString();

        var evidence = new List<EvidenceItem>
        {
            new("satellite_freq", mhzString, 1.0),
            new("apt_sync", "locked", _bestSyncQuality),
            new("subcarrier", "2400 Hz", 1.0),
        };

        var passStart = new DateTimeOffset(_passStartTicks, TimeSpan.Zero);
        var identifiers = new Dictionary<string, string>
        {
            ["satellite"] = satName,
            ["frequencyMhz"] = mhzString,
            ["lines"] = lines.ToString(CultureInfo.InvariantCulture),
            ["passStart"] = passStart.ToString("o"),
        };

        var device = new Device(deviceId, "Satellite", satName, identifiers, null, "NOAA-APT",
            _bestSyncQuality, evidence);

        return new SatellitePass(device, png, lines, _bestSyncQuality);
    }

    private void ProcessWord(double wordVal)
    {
        // Settle words are consumed silently (never fed into the sync ring or a line buffer): they
        // exist only to let the envelope low-pass ring down from the alternating sync-A pattern
        // before real pixel collection starts, matching AptModulator's SettleWords gap.
        if (_settling)
        {
            _settleRemaining--;
            if (_settleRemaining <= 0)
            {
                _settling = false;
                _inLine = true;
                _currentLine = new byte[PaddedLineWidth];
                _currentLinePos = 0;
            }
            return;
        }

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

        // The sync ring stores the RAW (pre-normalization) word value, not the byte normalized against
        // the whole-pass running min/max. Correlation below normalizes against the ring's OWN local
        // min/max instead: using the whole-pass range would make the very first sync-A (whose words
        // are the only data point the whole-pass min/max has seen so far -- it's still bootstrapping)
        // correlate differently than every later sync-A (which rides on an already-well-calibrated
        // whole-pass range from the preceding line's full 0..255 video content), skewing exactly where
        // the FIRST line's lock lands relative to every subsequent line's.
        _syncRing[_syncRingPos] = wordVal;
        _syncRingPos = (_syncRingPos + 1) % _syncRing.Length;
        if (_syncRingCount < _syncRing.Length) _syncRingCount++;

        if (_inLine)
        {
            _currentLine![_currentLinePos++] = normalized;
            if (_currentLinePos >= PaddedLineWidth)
            {
                if (_lines.Count < MaxImageLines)
                {
                    // Keep only the middle LineWidth words -- the EdgeTrim padding on both sides
                    // absorbs the lock-timing slop (see EdgeTrim's comment) and is discarded here.
                    var trimmed = new byte[LineWidth];
                    Array.Copy(_currentLine, EdgeTrim, trimmed, 0, LineWidth);
                    _lines.Add(trimmed);
                }
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
            _settling = true;
            _settleRemaining = SettleWords;
        }
    }

    // Normalized cross-correlation (0..1) of the last 7 raw word values (oldest-first) against
    // SyncTemplate, normalized against the RING'S OWN local min/max (not the whole-pass running
    // min/max -- see the comment where _syncRing is written).
    private double SyncCorrelation()
    {
        double localMin = double.MaxValue, localMax = double.MinValue;
        for (int k = 0; k < _syncRing.Length; k++)
        {
            double v = _syncRing[k];
            if (v < localMin) localMin = v;
            if (v > localMax) localMax = v;
        }

        double mid = (localMin + localMax) / 2.0;
        double halfRange = (localMax - localMin) / 2.0;
        if (halfRange <= 0.0) return 0.0; // flat window: cannot look like an alternating pattern

        double sum = 0.0;
        for (int k = 0; k < _syncRing.Length; k++)
        {
            int idx = (_syncRingPos + k) % _syncRing.Length; // oldest first (pos is the next-write slot)
            double norm = (_syncRing[idx] - mid) / halfRange; // roughly -1..1
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

        Array.Clear(_envelopeRing);
        _envelopeRingPos = 0;
        _envelopeSum = 0.0;

        _wordPos = 0.0;
        _wordSum = 0.0;
        _wordSampleCount = 0;

        Array.Clear(_syncRing);
        _syncRingPos = 0;
        _syncRingCount = 0;

        _settling = false;
        _settleRemaining = 0;

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
