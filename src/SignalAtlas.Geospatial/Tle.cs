using System.Globalization;

namespace SignalAtlas.Geospatial;

/// <summary>
/// A NORAD two-line element set (SPEC §8.4 Phase 2, §3): the orbital mean-elements input to
/// <see cref="Sgp4"/>. Column layout follows the standard 69-character TLE format (Spacetrack
/// Report #3 / Vallado "Revisiting Spacetrack Report #3", §2). Parsing is pure/deterministic
/// (P5): no clock or randomness is involved, only string→number extraction.
/// </summary>
public sealed record Tle(string Name, string Line1, string Line2)
{
    private const int LineLength = 69;

    /// <summary>NORAD catalog number (line 1, cols 3-7).</summary>
    public int NoradId => ParseInt(Line1, 2, 5);

    /// <summary>UTC epoch of the elements (line 1, cols 19-32): 2-digit year + fractional day-of-year.</summary>
    public DateTimeOffset Epoch => ParseEpoch(Line1);

    /// <summary>First derivative of mean motion / 2 (rev/day^2), line 1 cols 34-43. Not used by SGP4 physics
    /// (superseded by <see cref="BStar"/>), retained only because it is part of the parsed element set.</summary>
    public double MeanMotionDot => ParseDecimal(Line1, 33, 10);

    /// <summary>Drag term B* (Earth radii^-1), line 1 cols 54-61, exponential-notation encoding.</summary>
    public double BStar => ParseExponential(Line1, 53, 8);

    /// <summary>Inclination in degrees (line 2, cols 9-16).</summary>
    public double InclinationDeg => ParseDecimal(Line2, 8, 8);

    /// <summary>Right ascension of the ascending node in degrees (line 2, cols 18-25).</summary>
    public double RaanDeg => ParseDecimal(Line2, 17, 8);

    /// <summary>Eccentricity (line 2, cols 27-33) — implied leading "0.".</summary>
    public double Eccentricity => ParseImpliedDecimal(Line2, 26, 7);

    /// <summary>Argument of perigee in degrees (line 2, cols 35-42).</summary>
    public double ArgumentOfPerigeeDeg => ParseDecimal(Line2, 34, 8);

    /// <summary>Mean anomaly in degrees (line 2, cols 44-51).</summary>
    public double MeanAnomalyDeg => ParseDecimal(Line2, 43, 8);

    /// <summary>Mean motion in revolutions/day (line 2, cols 53-63).</summary>
    public double MeanMotionRevPerDay => ParseDecimal(Line2, 52, 11);

    /// <summary>
    /// Parses and validates a single TLE (line lengths, leading '1'/'2' markers, matching NORAD
    /// IDs on both lines, mod-10 checksums). Throws <see cref="FormatException"/> on any violation.
    /// </summary>
    public static Tle Parse(string name, string line1, string line2)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(line1);
        ArgumentNullException.ThrowIfNull(line2);

        if (line1.Length != LineLength)
            throw new FormatException($"TLE line 1 must be {LineLength} characters, got {line1.Length}.");
        if (line2.Length != LineLength)
            throw new FormatException($"TLE line 2 must be {LineLength} characters, got {line2.Length}.");
        if (line1[0] != '1')
            throw new FormatException("TLE line 1 must start with '1'.");
        if (line2[0] != '2')
            throw new FormatException("TLE line 2 must start with '2'.");

        ValidateChecksum(line1, "line 1");
        ValidateChecksum(line2, "line 2");

        var noradLine1 = ParseInt(line1, 2, 5);
        var noradLine2 = ParseInt(line2, 2, 5);
        if (noradLine1 != noradLine2)
            throw new FormatException(
                $"TLE line 1/2 NORAD ids do not match ({noradLine1} vs {noradLine2}).");

        return new Tle(name.Trim(), line1, line2);
    }

    /// <summary>
    /// Parses a multi-satellite TLE text blob (repeating name/line1/line2 triples, one per line,
    /// CRLF or LF). Blank lines are skipped. Throws <see cref="FormatException"/> on malformed input.
    /// </summary>
    public static IReadOnlyList<Tle> ParseMany(string blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var lines = blob
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        var result = new List<Tle>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Length > 0 && (lines[i][0] == '1' || lines[i][0] == '2'))
                throw new FormatException($"Expected a satellite name line at position {i}, got a TLE line.");

            if (i + 2 >= lines.Count)
                throw new FormatException("Truncated TLE blob: expected name + 2 element lines.");

            var name = lines[i];
            var line1 = lines[i + 1];
            var line2 = lines[i + 2];
            result.Add(Parse(name, line1, line2));
            i += 3;
        }

        return result;
    }

    private static void ValidateChecksum(string line, string label)
    {
        var sum = 0;
        for (var i = 0; i < LineLength - 1; i++)
        {
            var c = line[i];
            if (c is >= '0' and <= '9')
                sum += c - '0';
            else if (c == '-')
                sum += 1;
        }

        var expected = sum % 10;
        var actualChar = line[LineLength - 1];
        if (actualChar < '0' || actualChar > '9' || (actualChar - '0') != expected)
        {
            throw new FormatException(
                $"TLE checksum mismatch on {label}: expected {expected}, found '{actualChar}'.");
        }
    }

    private static int ParseInt(string s, int start, int length) =>
        int.Parse(s.AsSpan(start, length), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ParseDecimal(string s, int start, int length) =>
        double.Parse(s.AsSpan(start, length), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    /// <summary>Parses a field with an implied leading "0." (e.g. eccentricity's 7 digits, no sign, no point).</summary>
    private static double ParseImpliedDecimal(string s, int start, int length)
    {
        var span = s.AsSpan(start, length);
        return double.Parse("0." + span.ToString(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses TLE exponential-notation fields: [sign|space] + 5-digit mantissa (implied "0." prefix)
    /// + [exponent sign] + [exponent digit], e.g. " 28098-4" == 0.28098e-4, "-11606-4" == -0.11606e-4.
    /// </summary>
    private static double ParseExponential(string s, int start, int length)
    {
        var field = s.AsSpan(start, length);
        var signChar = field[0];
        var mantissa = field[1..(length - 2)].ToString();
        var expSignChar = field[length - 2];
        var expDigits = field[(length - 1)..].ToString();

        var sign = signChar == '-' ? -1.0 : 1.0;
        var expSign = expSignChar == '-' ? -1 : 1;
        var value = sign * double.Parse("0." + mantissa, CultureInfo.InvariantCulture);
        var exponent = expSign * int.Parse(expDigits, CultureInfo.InvariantCulture);
        return value * Math.Pow(10.0, exponent);
    }

    private static DateTimeOffset ParseEpoch(string line1)
    {
        var year = ParseInt(line1, 18, 2);
        year += year < 57 ? 2000 : 1900;
        var dayOfYear = ParseDecimal(line1, 20, 12);

        var jan1 = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return jan1.AddDays(dayOfYear - 1.0);
    }
}
