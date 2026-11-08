//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using SourceSharp.AssetDb.Imaging;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// Facts for <see cref="PngEncoder"/>, including a deliberately independent
/// decoder: the roundtrip facts walk the chunk stream, verify every CRC,
/// inflate the image data and unfilter the scanlines with code written here
/// rather than shared with the encoder, so a bug in one side cannot hide
/// behind the other.
/// </summary>
public sealed class PngEncoderTests
{
    [Fact]
    public void TwoByTwoProducesSignatureThenIhdrIdatIend()
    {
        var png = PngEncoder.Encode(MakeGradient(2, 2), 2, 2);

        // The PNG signature, byte for byte.
        byte[] expectedSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(expectedSignature, png[..8]);

        // Exactly three chunks follow, in the order the format's minimal
        // colour-type-six layout requires, each with a self-consistent CRC
        // and no trailing padding beyond IEND.
        var chunks = WalkChunks(png);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks.Select(c => c.Type));

        var ihdr = Assert.Single(chunks, c => c.Type == "IHDR");
        Assert.Equal(13, ihdr.Data.Length);

        var idat = Assert.Single(chunks, c => c.Type == "IDAT");
        Assert.NotEmpty(idat.Data);

        var iend = Assert.Single(chunks, c => c.Type == "IEND");
        Assert.Empty(iend.Data);

        Assert.Equal(png.Length, 8 + chunks.Sum(c => 12 + c.Data.Length));
    }

    [Fact]
    public void IhdrCarriesTheEightBitRgbaFields()
    {
        var png = PngEncoder.Encode(MakeGradient(3, 2), 3, 2);

        var ihdr = WalkChunks(png).Single(c => c.Type == "IHDR").Data;

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(ihdr));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]));
        Assert.Equal(8, ihdr[8]);  // bit depth
        Assert.Equal(6, ihdr[9]);  // colour type: truecolour with alpha
        Assert.Equal(0, ihdr[10]); // deflate
        Assert.Equal(0, ihdr[11]); // adaptive filtering
        Assert.Equal(0, ihdr[12]); // no interlace
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(17, 5)]
    [InlineData(64, 64)]
    public void RoundTripsPixelIdenticalThroughAnIndependentDecoder(int width, int height)
    {
        var rgba = PseudoRandomRgba(width, height, seed: (uint)(width * 131 + height));
        var png = PngEncoder.Encode(rgba, width, height);

        Assert.Equal(rgba, DecodeRgba(png));
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        var rgba = PseudoRandomRgba(23, 7, seed: 99);
        var first = PngEncoder.Encode(rgba, 23, 7);
        var second = PngEncoder.Encode(rgba, 23, 7);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EmptyImageIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode([], 0, 1));

    [Fact]
    public void NegativeHeightIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode(new byte[4], 1, -1));

    [Fact]
    public void TruncatedPixelBufferIsRejected() =>
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(new byte[7], 2, 2));

    // ------------------------------------------------------------------
    // The independent decoder, plus test imagery helpers.
    // ------------------------------------------------------------------

    private readonly record struct Chunk(string Type, byte[] Data);

    private static Chunk[] WalkChunks(ReadOnlySpan<byte> png)
    {
        int offset = 8; // signature
        var chunks = new List<Chunk>();
        Span<byte> typed = stackalloc byte[4];

        while (offset < png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            string type = Encoding.ASCII.GetString(png[(offset + 4)..(offset + 8)]);
            var data = png[(offset + 8)..(int)(offset + 8 + length)].ToArray();
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png[(int)(offset + 8 + length)..]);

            Encoding.ASCII.GetBytes(type, typed);
            Assert.Equal(crc, Crc32(typed, data));

            chunks.Add(new Chunk(type, data));
            offset = (int)(offset + 12 + length);
        }

        return [.. chunks];
    }

    private static byte[] DecodeRgba(ReadOnlySpan<byte> png)
    {
        var ihdr = WalkChunks(png).First(c => c.Type == "IHDR").Data;
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        int stride = width * 4;

        var idat = WalkChunks(png).Single(c => c.Type == "IDAT").Data;
        using var source = new MemoryStream(idat);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        var filtered = ReadAll(zlib);

        Assert.Equal(height * (stride + 1), filtered.Length);

        var prior = new byte[stride];
        var current = new byte[stride];
        var rows = new byte[height][];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            byte filter = filtered[rowStart];

            for (int x = 0; x < stride; x++)
            {
                byte left = x >= 4 ? current[x - 4] : (byte)0;
                byte up = prior[x];
                byte upLeft = x >= 4 ? prior[x - 4] : (byte)0;
                byte raw = filtered[rowStart + 1 + x];

                current[x] = filter switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + ((left + up) >> 1)),
                    4 => (byte)(raw + PaethPredict(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown filter {filter}."),
                };
            }

            rows[y] = (byte[])current.Clone();
            (prior, current) = (current, prior);
        }

        return rows.SelectMany(r => r).ToArray();
    }

    private static byte PaethPredict(byte left, byte up, byte upLeft)
    {
        int p = left + up - upLeft;
        int pa = Math.Abs(p - left);
        int pb = Math.Abs(p - up);
        int pc = Math.Abs(p - upLeft);

        if (pa <= pb && pa <= pc)
        {
            return left;
        }

        return pb <= pc ? up : upLeft;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var sink = new MemoryStream();
        stream.CopyTo(sink);
        return sink.ToArray();
    }

    /// <summary>
    /// The CRC Paul wrote for Ethernet, computed bitwise so the test keeps its
    /// own copy rather than sharing the encoder's table.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFF_FFFF;

        crc = Step(crc, first);

        return ~Step(crc, second);

        static uint Step(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                crc ^= b;

                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (crc & 1) != 0 ? 0xEDB88320 : 0;
                    crc = (crc >> 1) ^ mask;
                }
            }

            return crc;
        }
    }

    private static byte[] MakeGradient(int width, int height)
    {
        var rgba = new byte[width * height * 4];

        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i / 4);
            rgba[i + 1] = (byte)(i / 2);
            rgba[i + 2] = 0x40;
            rgba[i + 3] = 0xFF;
        }

        return rgba;
    }

    private static byte[] PseudoRandomRgba(int width, int height, uint seed)
    {
        var rng = seed;
        var rgba = new byte[width * height * 4];

        for (int i = 0; i < rgba.Length; i++)
        {
            rng = (rng * 1_664_525 + 1_013_904_223) & 0xFFFFFFFF;
            rgba[i] = (byte)(rng >> 24);
        }

        return rgba;
    }
}
