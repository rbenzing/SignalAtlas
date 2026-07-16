using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

/// <summary>
/// Turns a <see cref="FeatureVector"/> into the numeric design row consumed by the softmax model
/// (SPEC §8.9). Seven numeric features (frequency in MHz, bandwidths in kHz, power/SNR/duration/
/// duty) plus a one-hot encoding of <c>modulation_hint</c> over a fixed vocabulary. The column
/// order here is the single source of truth for <see cref="MlModel.FeatureNames"/>, so trainer,
/// classifier and drift monitor all agree.
/// </summary>
public static class FeatureEncoder
{
    /// <summary>Fixed modulation vocabulary; anything else (incl. null) maps to the "none" column.</summary>
    public static readonly string[] Modulations = { "CSS", "GFSK", "OFDM", "PPM", "FM", "O-QPSK" };

    public static readonly string[] FeatureNames = BuildFeatureNames();

    public static int Dimension => FeatureNames.Length;

    private static string[] BuildFeatureNames()
    {
        var names = new List<string>
        {
            "center_freq_mhz",
            "bandwidth_3db_khz",
            "bandwidth_20db_khz",
            "peak_power_dbfs",
            "snr_db",
            "duration_ms",
            "duty_cycle",
        };
        foreach (var m in Modulations) names.Add("mod=" + m);
        names.Add("mod=none");
        return names.ToArray();
    }

    /// <summary>Raw (un-standardized) feature row for the given vector, in <see cref="FeatureNames"/> order.</summary>
    public static double[] Encode(FeatureVector f)
    {
        var row = new double[Dimension];
        row[0] = f.CenterFreqHz / 1e6;
        row[1] = f.Bandwidth3dBHz / 1e3;
        row[2] = f.Bandwidth20dBHz / 1e3;
        row[3] = f.PeakPowerDbfs;
        row[4] = f.SnrDb;
        row[5] = f.DurationMs;
        row[6] = f.DutyCycle;

        int oneHotBase = 7;
        int matched = -1;
        for (int i = 0; i < Modulations.Length; i++)
        {
            if (string.Equals(f.ModulationHint, Modulations[i], StringComparison.OrdinalIgnoreCase))
            {
                matched = i;
                break;
            }
        }

        if (matched >= 0) row[oneHotBase + matched] = 1.0;
        else row[oneHotBase + Modulations.Length] = 1.0; // "none"

        return row;
    }

    /// <summary>Human-readable raw value for a feature column, for rendering in evidence (P4).</summary>
    public static string DisplayValue(FeatureVector f, int featureIndex) => featureIndex switch
    {
        0 => $"{f.CenterFreqHz / 1e6:0.###} MHz",
        1 => $"{f.Bandwidth3dBHz / 1e3:0.###} kHz",
        2 => $"{f.Bandwidth20dBHz / 1e3:0.###} kHz",
        3 => $"{f.PeakPowerDbfs:0.#} dBFS",
        4 => $"{f.SnrDb:0.#} dB",
        5 => $"{f.DurationMs} ms",
        6 => $"{f.DutyCycle:0.###}",
        _ => (Encode(f)[featureIndex] > 0.5) ? "yes" : "no", // one-hot columns
    };
}
