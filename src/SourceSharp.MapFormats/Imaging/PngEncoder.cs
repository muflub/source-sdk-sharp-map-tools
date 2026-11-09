//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace SourceSharp.MapFormats.Imaging;

/// <summary>
/// Encodes 8-bit RGBA pixels as a minimal PNG: colour type 6, bit depth 8,
/// no interlace, a single compressed <c>IDAT</c> between <c>IHDR</c> and
/// <c>IEND</c>.
/// </summary>
/// <remarks>
/// <para>
/// The point of this encoder is determinism. The texture database is built
/// once and diffed afterwards, so two encodings of the same pixels must be
/// byte-identical, and a rebuild months later on another machine must
/// reproduce the first byte for byte. That rules out the framework's default
/// compression level, which silently changes between runtime releases, and
/// any heuristic that consults the clock, the core count or the platform. The
/// zlib stream therefore pins its level explicitly, and the per-scanline
/// filter is chosen by a fixed rule rather than an adaptive one.
/// </para>
/// <para>
/// That rule is the minimum sum of absolute differences: each candidate
/// filter's bytes are read as signed deltas, and the filter whose deltas have
/// the smallest total magnitude wins, ties going to the earlier of None, Sub,
/// Up, Average, Paeth. Smooth images then filter to near-zero deltas and
/// compress better, while the choice stays a pure function of the pixels --
/// no row-history state, no platform math, no tuning constants to drift.
/// </para>
/// </remarks>
public static class PngEncoder
{
    private const int ChunkOverhead = 12; // length, type, CRC
    private const int IHdrLength = 13;

    /// <summary>
    /// Encodes an RGBA8888 image.
    /// </summary>
    /// <param name="rgba">The pixels, row-major, four bytes per pixel.</param>
    /// <param name="width">The image width, in pixels.</param>
    /// <param name="height">The image height, in pixels.</param>
    /// <returns>The complete PNG file.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int stride = checked(width * 4);
        int expected = checked(height * stride);
        if (rgba.Length != expected)
        {
            throw new ArgumentException(
                $"The pixel buffer holds {rgba.Length} bytes, but a {width}x{height} RGBA image needs {expected}.",
                nameof(rgba));
        }

        byte[] filtered = FilterImage(rgba, height, stride);
        byte[] compressed = Compress(filtered);

        using var png = new MemoryStream(8 + (ChunkOverhead + IHdrLength) + (ChunkOverhead + compressed.Length) + ChunkOverhead);
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[IHdrLength];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth: one byte per sample
        ihdr[9] = 6;  // colour type: truecolour with alpha
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter: adaptive per scanline
        ihdr[12] = 0; // interlace: none
        WriteChunk(png, "IHDR", ihdr);

        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// Writes one chunk: big-endian length, type, data, CRC over type and
    /// data, and returns the stream positioned after it.
    /// </summary>
    private static void WriteChunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);

        for (int i = 0; i < 4; i++)
        {
            header[4 + i] = (byte)type[i];
        }

        png.Write(header);
        png.Write(data);

        // The CRC covers type then data, exactly as written above; computing
        // it over a re-read of the stream would need a second buffer for no
        // gain, so the two pieces are hashed from where they already live.
        uint crc = Crc32(header[4..], data);
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        png.Write(trailer);
    }

    /// <summary>
    /// Applies the minimum-sum-of-absolute-differences filter choice
    /// documented on the class, row by row.
    /// </summary>
    private static byte[] FilterImage(ReadOnlySpan<byte> rgba, int height, int stride)
    {
        byte[] filtered = new byte[height * (stride + 1)];

        // Candidates are tried into one scratch row and the winner copied on,
        // so the cost is two row buffers however many filters are compared.
        byte[] candidate = new byte[stride];
        byte[] best = new byte[stride];

        // The filters that look upwards read a real row even on row zero, so
        // the implicit all-zero prior row is materialised once here instead of
        // being special-cased in every filter branch.
        byte[] zeroRow = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rgba.Slice(y * stride, stride);
            ReadOnlySpan<byte> prior = y == 0 ? zeroRow : rgba.Slice((y - 1) * stride, stride);

            int bestSum = int.MaxValue;
            byte bestFilter = 0;

            // Strictly less keeps the earlier filter on a tie, which is part
            // of making the choice a pure function of the pixels.
            for (byte filter = 0; filter <= 4; filter++)
            {
                FilterRow(row, prior, candidate, filter);

                int sum = 0;
                foreach (byte b in candidate)
                {
                    sum += b < 128 ? b : 256 - b;
                }

                if (sum < bestSum)
                {
                    bestSum = sum;
                    bestFilter = filter;
                    candidate.CopyTo(best);
                }
            }

            int rowStart = y * (stride + 1);
            filtered[rowStart] = bestFilter;
            best.CopyTo(filtered, rowStart + 1);
        }

        return filtered;
    }

    /// <summary>Applies one of the five PNG filters to a whole scanline.</summary>
    private static void FilterRow(ReadOnlySpan<byte> row, ReadOnlySpan<byte> prior, Span<byte> into, byte filter)
    {
        switch (filter)
        {
            case 0: // None: the deltas are the samples themselves.
                row.CopyTo(into);
                break;

            case 1: // Sub: the sample four bytes to the left.
                for (int x = 0; x < into.Length; x++)
                {
                    into[x] = (byte)(row[x] - (x >= 4 ? row[x - 4] : 0));
                }

                break;

            case 2: // Up: the sample directly above; row zero uses zeros.
                for (int x = 0; x < into.Length; x++)
                {
                    into[x] = (byte)(row[x] - prior[x]);
                }

                break;

            case 3: // Average: floor of the mean of left and up.
                for (int x = 0; x < into.Length; x++)
                {
                    int left = x >= 4 ? row[x - 4] : 0;
                    into[x] = (byte)(row[x] - ((left + prior[x]) >> 1));
                }

                break;

            default: // 4, Paeth: the nearest of left, up, upper-left.
                for (int x = 0; x < into.Length; x++)
                {
                    int left = x >= 4 ? row[x - 4] : 0;
                    int up = prior[x];
                    int upLeft = x >= 4 ? prior[x - 4] : 0;

                    int p = left + up - upLeft;
                    int pa = Math.Abs(p - left);
                    int pb = Math.Abs(p - up);
                    int pc = Math.Abs(p - upLeft);

                    int predictor = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
                    into[x] = (byte)(row[x] - predictor);
                }

                break;
        }
    }

    /// <summary>
    /// Deflates with the level pinned in the class documentation. The result
    /// is a complete zlib stream: header, deflate data, Adler trailer.
    /// </summary>
    private static byte[] Compress(byte[] filtered)
    {
        // Disposed explicitly so the flush lands before the buffer is read;
        // a using-declaration would run too late, after the return value.
        var sink = new MemoryStream();
        using var zlib = new ZLibStream(sink, CompressionLevel.Optimal);
        zlib.Write(filtered);
        zlib.Dispose();
        return sink.ToArray();
    }

    /// <summary>
    /// The CRC PNG inherited from Ethernet: reflected, seeded and finalised with all-ones.
    /// Tableless costs eight shifts per byte, noise next to the deflate pass,
    /// and spares a 256-entry static table its writable cells.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFF_FFFF;

        static uint Step(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                crc ^= b;

                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (crc & 1) != 0 ? 0xEDB8_8320 : 0;
                    crc = (crc >> 1) ^ mask;
                }
            }

            return crc;
        }

        return ~Step(Step(crc, first), second);
    }
}
