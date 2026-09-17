namespace SignalAtlas.Collector;

/// <summary>
/// Pure, deterministic validators for the HackRF One's RX ground truth (SPEC §4.1): center
/// frequency 1 MHz-6 GHz, sample rate 2-20 MS/s, RF amp is a 0/+14 dB enable (no range to check),
/// LNA 0-40 dB in 8 dB steps, VGA 0-62 dB in 2 dB steps, baseband filter bandwidth 1.75-28 MHz.
/// No DateTime.Now / Guid.NewGuid() / unseeded Random anywhere here — deterministic core (SPEC P5).
/// Style: explicit validate-and-throw for the config/startup path, plus clamp helpers for callers
/// that want to coerce a value into range instead of failing.
/// </summary>
public static class HackRfLimits
{
    public const long MinFrequencyHz = 1_000_000;
    public const long MaxFrequencyHz = 6_000_000_000;
    public const int MinSampleRateHz = 2_000_000;
    public const int MaxSampleRateHz = 20_000_000;
    public const int MinLnaDb = 0;
    public const int MaxLnaDb = 40;
    public const int LnaStepDb = 8;
    public const int MinVgaDb = 0;
    public const int MaxVgaDb = 62;
    public const int VgaStepDb = 2;
    public const int MinBasebandBwHz = 1_750_000;
    public const int MaxBasebandBwHz = 28_000_000;

    /// <summary>
    /// The baseband filter widths the HackRF's MAX2837 can actually select, ascending. The filter is
    /// NOT continuously tunable: these 16 values are the whole set, and libhackrf's
    /// <c>hackrf_compute_baseband_filter_bw</c> snaps a request down to one of them.
    /// <para>
    /// This must stay in step with <c>VALID_BASEBAND_BW</c> in <c>web/src/sdr/hackrf.ts</c> — the
    /// browser is the other implementation of the same radio, and a mismatch means the passband we
    /// RECORD differs from the one the hardware is RUNNING. That matters because this value is
    /// persisted as <c>Observation.BandwidthHz</c>, documented as the true analog passband.
    /// </para>
    /// </summary>
    public static readonly int[] SupportedBasebandBwHz =
    [
        1_750_000, 2_500_000, 3_500_000, 5_000_000, 5_500_000, 6_000_000, 7_000_000, 8_000_000,
        9_000_000, 10_000_000, 12_000_000, 14_000_000, 15_000_000, 20_000_000, 24_000_000, 28_000_000,
    ];

    public static bool IsValidFrequencyHz(long hz) => hz >= MinFrequencyHz && hz <= MaxFrequencyHz;

    public static bool IsValidSampleRateHz(int hz) => hz >= MinSampleRateHz && hz <= MaxSampleRateHz;

    public static bool IsValidLnaDb(int db) => db >= MinLnaDb && db <= MaxLnaDb && db % LnaStepDb == 0;

    public static bool IsValidVgaDb(int db) => db >= MinVgaDb && db <= MaxVgaDb && db % VgaStepDb == 0;

    /// <summary>
    /// True only for a width the radio can actually select. A plain range check used to accept any
    /// value in 1.75-28 MHz, so an unsupported width (13 MHz, 2.4 MHz…) passed validation and was
    /// then recorded as the analog passband while the hardware ran a different filter.
    /// </summary>
    public static bool IsValidBasebandBwHz(int hz) => Array.IndexOf(SupportedBasebandBwHz, hz) >= 0;

    /// <summary>RF amp enable is a boolean (0/+14 dB) so only the LNA/VGA stages need range checks.</summary>
    public static bool IsValidGain(RxGain gain) => IsValidLnaDb(gain.LnaDb) && IsValidVgaDb(gain.VgaDb);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> with a clear message when <paramref name="hz"/>
    /// falls outside the HackRF's 1 MHz-6 GHz tuning range.</summary>
    public static void ValidateFrequencyHz(long hz)
    {
        if (!IsValidFrequencyHz(hz))
            throw new ArgumentOutOfRangeException(nameof(hz), hz,
                $"HackRF center frequency must be between {MinFrequencyHz:N0} Hz and {MaxFrequencyHz:N0} Hz.");
    }

    /// <summary>Throws when <paramref name="hz"/> falls outside the HackRF's 2-20 MS/s sample-rate range.</summary>
    public static void ValidateSampleRateHz(int hz)
    {
        if (!IsValidSampleRateHz(hz))
            throw new ArgumentOutOfRangeException(nameof(hz), hz,
                $"HackRF sample rate must be between {MinSampleRateHz:N0} Hz and {MaxSampleRateHz:N0} Hz.");
    }

    /// <summary>Throws when <paramref name="db"/> is outside 0-40 dB or not a multiple of the 8 dB LNA step.</summary>
    public static void ValidateLnaDb(int db)
    {
        if (!IsValidLnaDb(db))
            throw new ArgumentOutOfRangeException(nameof(db), db,
                $"HackRF LNA gain must be between {MinLnaDb} and {MaxLnaDb} dB in {LnaStepDb} dB steps.");
    }

    /// <summary>Throws when <paramref name="db"/> is outside 0-62 dB or not a multiple of the 2 dB VGA step.</summary>
    public static void ValidateVgaDb(int db)
    {
        if (!IsValidVgaDb(db))
            throw new ArgumentOutOfRangeException(nameof(db), db,
                $"HackRF VGA gain must be between {MinVgaDb} and {MaxVgaDb} dB in {VgaStepDb} dB steps.");
    }

    /// <summary>Throws when <paramref name="hz"/> falls outside the modeled baseband filter bandwidth range.</summary>
    public static void ValidateBasebandBwHz(int hz)
    {
        if (!IsValidBasebandBwHz(hz))
            throw new ArgumentOutOfRangeException(nameof(hz), hz,
                $"HackRF baseband filter bandwidth must be between {MinBasebandBwHz:N0} Hz and {MaxBasebandBwHz:N0} Hz.");
    }

    /// <summary>Validates every range-checked field of <paramref name="gain"/> (RF amp enable is a boolean
    /// and needs no range check).</summary>
    public static void ValidateGain(RxGain gain)
    {
        ValidateLnaDb(gain.LnaDb);
        ValidateVgaDb(gain.VgaDb);
    }

    /// <summary>Clamps a center frequency into the valid HackRF range.</summary>
    public static long ClampFrequencyHz(long hz) => Math.Clamp(hz, MinFrequencyHz, MaxFrequencyHz);

    /// <summary>Clamps a sample rate into the valid HackRF range.</summary>
    public static int ClampSampleRateHz(int hz) => Math.Clamp(hz, MinSampleRateHz, MaxSampleRateHz);

    /// <summary>
    /// Snaps a requested baseband bandwidth to the widest supported filter that does not exceed it
    /// (and to the narrowest filter when the request is below the minimum) — the same rule as
    /// libhackrf and the browser client, so both paths land on the same passband. Rounding DOWN
    /// never admits more spectrum than the caller asked for.
    /// </summary>
    public static int ClampBasebandBwHz(int hz)
    {
        if (hz >= MaxBasebandBwHz) return MaxBasebandBwHz;

        var best = SupportedBasebandBwHz[0];
        foreach (var bw in SupportedBasebandBwHz)
        {
            if (bw > hz) break;
            best = bw;
        }
        return best;
    }

    /// <summary>Clamps each gain stage independently into its valid range, rounding LNA/VGA down to the
    /// nearest step at or below the requested value (never exceeds the requested dB).</summary>
    public static RxGain ClampGain(RxGain gain)
    {
        int lna = Math.Clamp(gain.LnaDb, MinLnaDb, MaxLnaDb);
        lna -= lna % LnaStepDb;
        int vga = Math.Clamp(gain.VgaDb, MinVgaDb, MaxVgaDb);
        vga -= vga % VgaStepDb;
        return new RxGain(gain.AmpEnable, lna, vga);
    }
}
