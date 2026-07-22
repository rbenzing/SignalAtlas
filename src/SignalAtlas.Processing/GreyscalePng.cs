using System;
using System.Collections.Generic;

namespace SignalAtlas.Processing;

/// <summary>
/// Dependency-free, deterministic 8-bit greyscale PNG encoder. Uses zlib "stored" (uncompressed)
/// deflate blocks so it needs no System.Drawing/ImageSharp and is byte-for-byte deterministic (P5).
/// </summary>
public static class GreyscalePng
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Encode row-major 8-bit greyscale pixels (length == width*height) as an 8-bit
    /// greyscale PNG. Dependency-free: zlib "stored" (uncompressed) deflate blocks + Adler-32, so it
    /// needs no System.Drawing/ImageSharp and is byte-for-byte deterministic (P5). Each PNG row is
    /// prefixed with filter byte 0 (None).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0) throw new ArgumentException("width must be positive.", nameof(width));
        if (height <= 0) throw new ArgumentException("height must be positive.", nameof(height));
        if (pixels.Length != width * height)
            throw new ArgumentException(
                $"pixels.Length ({pixels.Length}) must equal width*height ({width * height}).",
                nameof(pixels));

        // Raw scanline stream: one 0x00 filter byte + `width` pixel bytes per row.
        var raw = new byte[(long)height * (width + 1)];
        int rawOffset = 0;
        for (int y = 0; y < height; y++)
        {
            raw[rawOffset++] = 0x00; // filter type: None
            pixels.Slice(y * width, width).CopyTo(raw.AsSpan(rawOffset));
            rawOffset += width;
        }

        byte[] idatData = ZlibStore(raw);

        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);

        WriteChunk(ms, "IHDR", BuildIhdr(width, height));
        WriteChunk(ms, "IDAT", idatData);
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, 0, (uint)width);
        WriteUInt32BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // colour type: greyscale
        ihdr[10] = 0; // compression method
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // interlace method
        return ihdr;
    }

    /// <summary>Wrap `raw` in a minimal zlib stream using DEFLATE stored (uncompressed) blocks.</summary>
    private static byte[] ZlibStore(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); // CMF: CM=8 (deflate), CINFO=7 (32K window)
        ms.WriteByte(0x01); // FLG: chosen so (CMF*256+FLG) % 31 == 0, no dict, level 0

        const int maxBlock = 65535;
        int offset = 0;
        int remaining = raw.Length;

        if (remaining == 0)
        {
            // Emit a single empty final stored block.
            WriteStoredBlock(ms, raw, 0, 0, isFinal: true);
        }
        else
        {
            while (remaining > 0)
            {
                int blockLen = Math.Min(maxBlock, remaining);
                bool isFinal = blockLen == remaining;
                WriteStoredBlock(ms, raw, offset, blockLen, isFinal);
                offset += blockLen;
                remaining -= blockLen;
            }
        }

        uint adler = Adler32(raw);
        Span<byte> adlerBytes = stackalloc byte[4];
        WriteUInt32BigEndian(adlerBytes, 0, adler);
        ms.Write(adlerBytes);

        return ms.ToArray();
    }

    private static void WriteStoredBlock(MemoryStream ms, byte[] data, int offset, int length, bool isFinal)
    {
        ms.WriteByte((byte)(isFinal ? 0x01 : 0x00)); // BFINAL bit0, BTYPE=00 (stored) in remaining bits
        ushort len = (ushort)length;
        ushort nlen = (ushort)~len;
        ms.WriteByte((byte)(len & 0xFF));
        ms.WriteByte((byte)((len >> 8) & 0xFF));
        ms.WriteByte((byte)(nlen & 0xFF));
        ms.WriteByte((byte)((nlen >> 8) & 0xFF));
        if (length > 0)
            ms.Write(data, offset, length);
    }

    private static void WriteChunk(MemoryStream ms, string type, byte[] data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        WriteUInt32BigEndian(lenBytes, 0, (uint)data.Length);
        ms.Write(lenBytes);

        var typeBytes = new byte[4];
        typeBytes[0] = (byte)type[0];
        typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2];
        typeBytes[3] = (byte)type[3];
        ms.Write(typeBytes, 0, 4);

        if (data.Length > 0)
            ms.Write(data, 0, data.Length);

        var crcInput = new byte[4 + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, 4);
        uint crc = Crc32(crcInput);

        Span<byte> crcBytes = stackalloc byte[4];
        WriteUInt32BigEndian(crcBytes, 0, crc);
        ms.Write(crcBytes);
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, int offset, uint value)
    {
        destination[offset] = (byte)((value >> 24) & 0xFF);
        destination[offset + 1] = (byte)((value >> 16) & 0xFF);
        destination[offset + 2] = (byte)((value >> 8) & 0xFF);
        destination[offset + 3] = (byte)(value & 0xFF);
    }

    // --- CRC-32 (poly 0xEDB88320), inline, no System.IO.Hashing dependency ---

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }

    // --- Adler-32, inline ---

    private const uint AdlerMod = 65521;

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % AdlerMod;
            b = (b + a) % AdlerMod;
        }
        return (b << 16) | a;
    }
}
