//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>
/// The optional <c>BSPB</c> section: a navigation built from a compiled map
/// names that map's checksum and hash, the reader says whether a map is that
/// one, and a file without the section is still read, as bound to no map.
/// </summary>
public sealed class Nav3dBspBindingTests
{
    private const string World = "{\n\"classname\" \"worldspawn\"\n}\n";

    private static async Task<byte[]> MapAsync(string entities, byte[] planes)
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.Entities, EntityLump.Write(EntityLump.ParseText(entities)).Data, 0);
        bsp.SetLump(BspLump.Planes, planes);
        bsp.SetLump(BspLump.Vertexes, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }

    private static byte[] Bound(Nav3dBspBinding binding) => Nav3dWriter.Write(new Nav3dLevel
    {
        CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 1, Rows = 1, Cells = [new Nav3dCell(null, 0, 0, 0, 0)], Roots = [-1],
        Bsp = binding,
    });

    private static int Entry(byte[] file, string tag)
    {
        int image = Nav3dFormat.EnvelopeBytes;
        int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(image + 4));
        for (int i = 0; i < count; i++)
        {
            int at = image + Nav3dFormat.HeaderBytes + (i * Nav3dFormat.DirectoryEntryBytes);
            if (Encoding.ASCII.GetString(file, at, 4) == tag)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>A level with a binding writes it as the last section, 40 bytes, keeps its version, and reads it back.</summary>
    [Fact]
    public async Task TheBindingIsAnOptionalSectionThatRoundTrips()
    {
        byte[] map = await MapAsync(World, new byte[20]);
        Nav3dBspBinding binding = Nav3dBspBinding.Of(map);
        Assert.Equal(BspMapChecksum.Compute(map), binding.MapChecksum);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(map)), binding.Sha256);

        byte[] file = Bound(binding);
        int entry = Entry(file, Nav3dFormat.BspBindingTag);
        Assert.True(entry > 0);
        int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(Nav3dFormat.EnvelopeBytes + 4));
        Assert.Equal(Nav3dFormat.EnvelopeBytes + Nav3dFormat.HeaderBytes + ((count - 1) * Nav3dFormat.DirectoryEntryBytes), entry);
        Assert.Equal((uint)Nav3dFormat.BspBindingBytes, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(entry + 12)));
        Assert.Equal(Nav3dFormat.CubeVersion, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8)));

        Nav3dReader nav = Nav3dReader.Open(file);
        Assert.Equal(binding, nav.Bsp);
        Assert.Equal(file, Nav3dWriter.Write(nav.ToLevel()));
    }

    /// <summary>
    /// MatchesBsp is true for the map the file names and false for a map one
    /// byte away: in a lump the checksum covers, and in the entity lump the
    /// checksum leaves out but the hash does not. Bytes that are not a map
    /// are no match, not an exception.
    /// </summary>
    [Fact]
    public async Task MatchesBspIsTheMapAndNotAMapOneByteAway()
    {
        byte[] planes = new byte[20];
        byte[] map = await MapAsync(World, planes);
        Nav3dReader nav = Nav3dReader.Open(Bound(Nav3dBspBinding.Of(map)));
        Assert.True(nav.MatchesBsp(map));
        Assert.True(nav.MatchesBsp(Nav3dBspBinding.Of(map)));

        planes[3] = 1;
        byte[] geometry = await MapAsync(World, planes);
        Assert.Equal(map.Length, geometry.Length);
        Assert.Equal(1, map.Zip(geometry).Count(p => p.First != p.Second));
        Assert.False(nav.MatchesBsp(geometry));

        byte[] entities = (byte[])map.Clone();
        int entityOffset = BinaryPrimitives.ReadInt32LittleEndian(entities.AsSpan(8));
        int at = Array.IndexOf(entities, (byte)'w', entityOffset);
        entities[at] = (byte)'W';
        Assert.Equal(BspMapChecksum.Compute(map), BspMapChecksum.Compute(entities));
        Assert.False(nav.MatchesBsp(entities));

        Assert.False(nav.MatchesBsp(new byte[16]));
    }

    /// <summary>
    /// A file without the section (every file before it existed, and every
    /// linked level) is read as before and bound to no map; one whose section
    /// is malformed is refused; and a reader that does not know the tag (here,
    /// the section under another name) skips it, as the format's rule for new
    /// tags requires.
    /// </summary>
    [Fact]
    public async Task AFileWithoutTheSectionReadsAsBoundToNothing()
    {
        byte[] map = await MapAsync(World, new byte[20]);
        Nav3dReader plain = Nav3dReader.Open(Nav3dWriter.Write(Nav3dFileTests.Sample()));
        Assert.Null(plain.Bsp);
        Assert.False(plain.MatchesBsp(map));
        Assert.False(plain.MatchesBsp(Nav3dBspBinding.Of(map)));
        Assert.Null(plain.ToLevel().Bsp);

        byte[] bound = Bound(Nav3dBspBinding.Of(map));
        byte[] renamed = (byte[])bound.Clone();
        "ZZZZ"u8.CopyTo(renamed.AsSpan(Entry(renamed, Nav3dFormat.BspBindingTag)));
        Nav3dReader unknown = Nav3dReader.Open(renamed);
        Assert.Null(unknown.Bsp);
        Assert.False(unknown.MatchesBsp(map));

        byte[] shortened = (byte[])bound.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shortened.AsSpan(Entry(shortened, Nav3dFormat.BspBindingTag) + 12), 36);
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(shortened));
        Assert.Contains("\"BSPB\" section is 36 bytes; a map binding is 40", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingWhoseHashIsNotSixtyFourHexDigitsIsRefusedByTheWriter()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => Bound(new Nav3dBspBinding(1, "abc")));
        Assert.Contains("64 hex digits", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Bound(new Nav3dBspBinding(1, new string('g', 64))));
    }
}
