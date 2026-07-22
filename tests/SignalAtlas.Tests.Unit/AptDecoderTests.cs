using System.IO.Compression;
using System.Text;
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

    // --- Anti-scramble guard: a syntactically valid but content-scrambled PNG must NOT pass this. ---

    [Fact]
    public void Accept_SyntheticPass_DecodedImageMatchesSource()
    {
        var rows = Gradient(8);
        var block = AptModulator.Modulate(rows, centerFreqHz: 137_100_000, sampleRateHz: 2_000_000);
        var pass = new AptDecoder().Accept(block, Features(137_100_000), DateTimeOffset.UnixEpoch);

        Assert.NotNull(pass);
        Assert.True(pass!.Lines >= 4);
        AssertDecodedMatchesSource(pass.PngImage, rows);
    }

    // --- Multi-block continuity: word-position fraction, in-progress line buffer, and sync ring
    // must all survive an IqBlock boundary landing mid-pass on a single decoder instance. ---

    [Fact]
    public void Accept_SplitAcrossTwoBlocks_StillDecodes()
    {
        var rows = Gradient(8);
        var block = AptModulator.Modulate(rows, centerFreqHz: 137_100_000, sampleRateHz: 2_000_000);

        // Split on a whole multiple of the FM discriminator's decimation factor (SampleRateHz/20000)
        // so block1's tail isn't a partial decimation window -- FmDiscriminator drops any remainder
        // samples that don't fill a full window rather than carrying them to the next block, which
        // is an accepted characteristic of its own (Task 2) design, not something this test means to
        // exercise. This test's job is to prove AptDecoder's OWN word-position accumulator, envelope
        // low-pass, in-progress line buffer, and sync ring survive the block boundary.
        int decimation = block.SampleRateHz / 20_000;
        int half = (block.SampleCount / 2 / decimation) * decimation;
        var block1 = new IqBlock(block.CenterFreqHz, block.SampleRateHz,
            block.I.Take(half).ToArray(), block.Q.Take(half).ToArray());
        var block2 = new IqBlock(block.CenterFreqHz, block.SampleRateHz,
            block.I.Skip(half).ToArray(), block.Q.Skip(half).ToArray());

        var d = new AptDecoder();
        var pass1 = d.Accept(block1, Features(137_100_000), DateTimeOffset.UnixEpoch);
        var pass2 = d.Accept(block2, Features(137_100_000), DateTimeOffset.UnixEpoch);

        var pass = pass2 ?? pass1;
        Assert.NotNull(pass);
        Assert.True(pass!.Lines >= 4);
        AssertDecodedMatchesSource(pass.PngImage, rows);
    }

    /// <summary>Decode an 8-bit greyscale PNG (as produced by GreyscalePng.Encode) back to pixels,
    /// then assert it matches the source gradient: low mean-absolute-error AND every decoded row
    /// monotonically non-decreasing left-to-right (the gradient's defining property -- a scrambled
    /// image, even a byte-valid PNG, cannot satisfy this).</summary>
    private static void AssertDecodedMatchesSource(byte[] png, byte[][] sourceRows)
    {
        var (width, height, pixels) = DecodePng(png);
        Assert.Equal(2080, width);

        int linesToCompare = Math.Min(height, sourceRows.Length);
        Assert.True(linesToCompare >= 4, $"expected >= 4 comparable lines, got {linesToCompare}");

        double totalAbsError = 0.0;
        long count = 0;
        for (int r = 0; r < linesToCompare; r++)
        {
            for (int c = 0; c < width; c++)
            {
                totalAbsError += Math.Abs(pixels[r * width + c] - sourceRows[r][c]);
                count++;
            }

            for (int c = 1; c < width; c++)
                Assert.True(pixels[r * width + c] >= pixels[r * width + c - 1],
                    $"row {r} not monotonic non-decreasing at column {c}: {pixels[r * width + c - 1]} -> {pixels[r * width + c]}");
        }

        double mae = totalAbsError / count;
        Assert.True(mae < 50.0, $"mean absolute error {mae} >= 50.0/255 threshold");
    }

    private static (int Width, int Height, byte[] Pixels) DecodePng(byte[] png)
    {
        int pos = 8; // skip the 8-byte PNG signature
        int width = 0, height = 0;
        using var idat = new MemoryStream();

        while (pos < png.Length)
        {
            int len = ReadUInt32BigEndian(png, pos); pos += 4;
            string type = Encoding.ASCII.GetString(png, pos, 4); pos += 4;

            if (type == "IHDR")
            {
                width = ReadUInt32BigEndian(png, pos);
                height = ReadUInt32BigEndian(png, pos + 4);
            }
            else if (type == "IDAT")
            {
                idat.Write(png, pos, len);
            }

            pos += len; // chunk data
            pos += 4;   // CRC
            if (type == "IEND") break;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] rawBytes = raw.ToArray();

        var pixels = new byte[height * width];
        int rowStride = width + 1; // filter byte + width pixel bytes
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowStride;
            // rowStart is the PNG filter-type byte (0 = None, per GreyscalePng.Encode); skip it.
            Array.Copy(rawBytes, rowStart + 1, pixels, y * width, width);
        }

        return (width, height, pixels);
    }

    private static int ReadUInt32BigEndian(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
