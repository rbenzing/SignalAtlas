using System;
using System.Linq;
using SignalAtlas.Processing;
using Xunit;

namespace SignalAtlas.Tests.Unit;

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
        // IEND is the final chunk: type field sits at the last 8 bytes minus the 4-byte CRC.
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
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
