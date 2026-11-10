//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapFormats.Compression;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// Reading compressed content as shipped maps carry it: LZMA lumps and game
/// lump entries unpacked by the loader, and method-14 pak entries unpacked by
/// the zip reader and the pak mount.
/// </summary>
/// <remarks>
/// The compressed payload throughout is the LZMA facts' sample, encoded by an
/// independent encoder, so these facts test the plumbing, not the decoder.
/// </remarks>
public sealed class BspCompressionTests
{
    private static readonly byte[] Sample = LzmaDecoderTests.Sample();

    private static byte[] Container() =>
        LzmaDecoderTests.Container(Convert.FromBase64String(LzmaDecoderTests.Stream0), Sample.Length);

    private static async Task<BspData> LoadAsync(byte[] file)
    {
        using MemoryStream stream = new(file, writable: false);
        return await BspFile.LoadAsync(stream);
    }

    [Fact]
    public async Task ACompressedLumpIsUnpackedOnLoadAndRecordedAsPlain()
    {
        BspData bsp = await LoadAsync(new BspTestBuilder()
            .Lump(BspLump.TexDataStringData, Container(), Sample.Length)
            .Lump(BspLump.Entities, "\0"u8.ToArray())
            .Build());

        Assert.Equal(Sample, bsp[BspLump.TexDataStringData].Data.ToArray());
        Assert.Equal(0, bsp[BspLump.TexDataStringData].UncompressedSize);
        Assert.Equal(1, bsp[BspLump.Entities].Length);
    }

    [Fact]
    public async Task ACompressedLumpThatUnpacksToTheWrongSizeIsRefused()
    {
        byte[] file = new BspTestBuilder().Lump(BspLump.TexDataStringData, Container(), Sample.Length + 1).Build();

        InvalidBspException error = await Assert.ThrowsAsync<InvalidBspException>(() => LoadAsync(file));
        Assert.Contains("not the", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompressedLumpThatIsNotAnLzmaContainerIsRefused()
    {
        byte[] file = new BspTestBuilder().Lump(BspLump.TexDataStringData, [1, 2, 3, 4], 100).Build();

        InvalidBspException error = await Assert.ThrowsAsync<InvalidBspException>(() => LoadAsync(file));
        Assert.Contains("does not unpack", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Many compressed lumps, unpacked together, each land in their own slot with their own bytes, beside a plain lump.</summary>
    [Fact]
    public async Task ManyCompressedLumpsEachUnpackIntoTheirOwnSlot()
    {
        BspLump[] lumps = [BspLump.TexDataStringData, BspLump.Planes, BspLump.Vertexes, BspLump.Edges, BspLump.SurfEdges, BspLump.Leafs];
        BspTestBuilder builder = new BspTestBuilder().Lump(BspLump.Entities, "\0"u8.ToArray());
        foreach (BspLump lump in lumps)
        {
            builder.Lump(lump, Container(), Sample.Length);
        }

        BspData bsp = await LoadAsync(builder.Build());

        Assert.All(lumps, lump => Assert.Equal(Sample, bsp[lump].Data.ToArray()));
        Assert.Equal(1, bsp[BspLump.Entities].Length);
    }

    /// <summary>
    /// When several compressed lumps fail, the lowest lump's error is the one
    /// thrown, as a lump-by-lump load would stop at it, though the larger
    /// broken lump after it is unpacked first.
    /// </summary>
    [Fact]
    public async Task TheLowestBrokenLumpsErrorIsThrown()
    {
        byte[] file = new BspTestBuilder()
            .Lump(BspLump.Planes, [1, 2, 3, 4], 100)
            .Lump(BspLump.Vertexes, Container(), Sample.Length)
            .Lump(BspLump.TexDataStringData, Container(), Sample.Length + 1)
            .Build();

        InvalidBspException error = await Assert.ThrowsAsync<InvalidBspException>(() => LoadAsync(file));
        Assert.Equal($"lump {(int)BspLump.Planes} is compressed but does not unpack", error.Message.Split(':')[0]);
    }

    /// <summary>
    /// A compressed lump that does not unpack is reported before a later
    /// lump's header error, the order a lump-by-lump load meets them in.
    /// </summary>
    [Fact]
    public async Task ABrokenCompressedLumpIsReportedBeforeALaterLumpsHeaderError()
    {
        byte[] file = new BspTestBuilder()
            .Lump(BspLump.Planes, [1, 2, 3, 4], 100)
            .Lump(BspLump.TexDataStringData, "abcd"u8.ToArray())
            .Build();
        BitConverter.GetBytes(1 << 20).CopyTo(file, 8 + ((int)BspLump.TexDataStringData * 16) + 4);

        InvalidBspException error = await Assert.ThrowsAsync<InvalidBspException>(() => LoadAsync(file));
        Assert.Contains("does not unpack", error.Message, StringComparison.Ordinal);

        BitConverter.GetBytes(0).CopyTo(file, 8 + ((int)BspLump.Planes * 16) + 12);
        error = await Assert.ThrowsAsync<InvalidBspException>(() => LoadAsync(file));
        Assert.Contains("runs past the end of the file", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLastCompressedEntryRunsToTheLumpsEndAndTheTerminatorIsDropped(bool terminatorAtEnd)
    {
        // Shipped maps write the terminator all zero, so the LAST compressed
        // entry has no next offset and runs to the end of the lump.
        BspData bsp = await LoadAsync(new BspTestBuilder()
            .GameLump(0x64707270, 0, 4, [1, 2, 3])
            .GameLump(0x70727073, 1, 10, Container(), declaredLength: Sample.Length)
            .Terminator(terminatorAtEnd)
            .Build());

        Assert.Equal(2, bsp.GameLumps.Count);
        Assert.Equal([1, 2, 3], bsp.GameLumps[0].Data.ToArray());
        Assert.Equal(Sample, bsp.GameLumps[1].Data.ToArray());
        Assert.Equal((0, 10), (bsp.GameLumps[1].Flags, bsp.GameLumps[1].Version));
    }

    [Fact]
    public async Task ACompressedEntryBeforeAnotherRunsOnlyToThatEntrysOffset()
    {
        BspData bsp = await LoadAsync(new BspTestBuilder()
            .GameLump(0x70727073, 1, 10, Container(), declaredLength: Sample.Length)
            .GameLump(0x64707270, 0, 4, [1, 2, 3])
            .Terminator()
            .Build());

        Assert.Equal(Sample, bsp.GameLumps[0].Data.ToArray());
        Assert.Equal([1, 2, 3], bsp.GameLumps[1].Data.ToArray());
    }

    [Fact]
    public async Task AnEmptyEntryInAnUncompressedGameLumpIsKept()
    {
        // Only a directory with compressed entries carries the terminator; an
        // uncompressed map's entries, an empty one included, all round-trip
        // as they are.
        BspData bsp = await LoadAsync(new BspTestBuilder()
            .GameLump(0x64707270, 0, 4, [1, 2, 3])
            .Terminator(atEnd: true)
            .Build());

        Assert.Equal(2, bsp.GameLumps.Count);
    }

    [Fact]
    public async Task ACompressedGameLumpEntryThatDoesNotUnpackIsRefused()
    {
        byte[] file = new BspTestBuilder()
            .GameLump(0x70727073, 1, 10, Container(), declaredLength: Sample.Length + 5)
            .Terminator()
            .Build();

        await Assert.ThrowsAsync<InvalidBspException>(() => LoadAsync(file));
    }

    [Fact]
    public async Task ACompressedMapSavesUncompressedAndReloadsTheSame()
    {
        BspData loaded = await LoadAsync(new BspTestBuilder()
            .Lump(BspLump.TexDataStringData, Container(), Sample.Length)
            .GameLump(0x70727073, 1, 10, Container(), declaredLength: Sample.Length)
            .Terminator()
            .Build());

        using MemoryStream saved = new();
        await BspFile.SaveAsync(loaded, saved);
        byte[] file = saved.ToArray();
        BspData reloaded = await LoadAsync(file);

        // Every directory entry's uncompressed-size field is zero: plain.
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            Assert.Equal(0, BitConverter.ToInt32(file, 8 + (i * 16) + 12));
        }

        Assert.Equal(Sample, reloaded[BspLump.TexDataStringData].Data.ToArray());
        Assert.Equal(Sample, Assert.Single(reloaded.GameLumps).Data.ToArray());
        Assert.Equal(0, reloaded.GameLumps[0].Flags);
    }

    private static ZipEntry LzmaEntry() => new(
        "materials/big.vtf",
        LzmaDecoderTests.ZipPayload(Convert.FromBase64String(LzmaDecoderTests.Stream0)),
        ZipCompressionMethod.Lzma,
        crc: SourceSharp.MapFormats.Zip.Crc32.Compute(Sample),
        uncompressedSize: (uint)Sample.Length);

    [Fact]
    public void AnLzmaZipEntryReadsItsContentsAndAStoredOneIsItsData()
    {
        ZipEntry stored = new("a.txt", [1, 2, 3]);

        Assert.Equal(Sample, LzmaEntry().ReadContents());
        Assert.Same(stored.Data, stored.ReadContents());
    }

    [Fact]
    public void AnLzmaZipEntryThatDoesNotUnpackSaysWhich()
    {
        ZipEntry broken = new("bad.vtf", [9, 20, 5, 0, 0x5D, 0, 0, 1, 0, 9, 9], ZipCompressionMethod.Lzma, crc: 0, uncompressedSize: 10);

        InvalidZipException error = Assert.Throws<InvalidZipException>(() => broken.ReadContents());
        Assert.Contains("bad.vtf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APakMountReadsAnLzmaEntryWholeAndByRange()
    {
        ZipArchiveWriter writer = new();
        writer.Add(LzmaEntry());
        writer.Add(new ZipEntry("plain.txt", [7, 8, 9]));
        PakArchive pak = PakArchive.Open(writer.ToBytes(), "test.bsp");

        using IMemoryOwner<byte>? whole = await pak.ReadAsync(VPath.Create("materials/big.vtf"));
        using FileRange? range = await pak.ReadRangeAsync(VPath.Create("materials/big.vtf"), 100, 50);

        Assert.Equal(Sample, whole!.Memory.ToArray());
        Assert.Equal(Sample[100..150], range!.Memory.ToArray());
    }
}
