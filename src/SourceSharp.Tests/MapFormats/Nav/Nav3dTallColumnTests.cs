//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>
/// Version 4 of the <c>.nav3d</c> file: a column taller than 255 voxels, its
/// leaves' low voxels and heights 16 bits with the high bytes in the leaf
/// record's spare pair. Written only for a level that needs it, so every
/// level that fits a byte keeps its version and bytes; a version 2 or 3
/// file cannot claim a taller cell or a high byte.
/// </summary>
public sealed class Nav3dTallColumnTests
{
    private const uint Open = 16;

    /// <summary>
    /// One cell of four columns, 32 units a side and <paramref name="height"/>
    /// voxels of 16 up. Column 0 holds a floor run at the bottom and a run
    /// from <paramref name="highLo"/>, <paramref name="highRun"/> voxels
    /// high; the other three are solid.
    /// </summary>
    private static Nav3dLevel Tall(int height, ushort highLo, ushort highRun)
    {
        (byte[] blocked, byte[] open, _) = Nav3dFileTests.Records();
        const Nav3dLeafFlags Floor = Nav3dLeafFlags.GroundedPlayer | Nav3dLeafFlags.WalkablePlayer;
        return new Nav3dLevel
        {
            CellSize = 32,
            VoxelSize = 16,
            CellVoxels = 2,
            CellHeights = [height],
            Columns = 1,
            Rows = 1,
            FloorNormalZ = 0.7f,
            StepHeight = 18,
            JumpHeight = 56,
            JumpDistance = 100,
            Cells = [new Nav3dCell(null, 0, Nav3dRoomRole.None, 0, 0)],
            Presets = [new Nav3dPreset("standing", 8, 20, Nav3dClipClass.Player)],
            Roots = [0],
            ColumnStarts = [0, 2, 2, 2, 2],
            Leaves =
            [
                new Nav3dLeaf(0, 10, Floor, 256, Open, Open, 0, 0),
                new Nav3dLeaf(highLo, highRun, Floor, 256, Open, Open, highLo * 16f, 0),
            ],
            Clearance = [.. blocked, .. open],
        };
    }

    /// <summary>Where a section starts in a raw file's bytes.</summary>
    private static int Section(byte[] bytes, string tag)
    {
        int image = Nav3dFormat.EnvelopeBytes;
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(image + 4));
        for (int i = 0; i < count; i++)
        {
            int at = image + Nav3dFormat.HeaderBytes + (i * Nav3dFormat.DirectoryEntryBytes);
            if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == tag)
            {
                return image + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
            }
        }

        throw new InvalidOperationException(tag);
    }

    [Fact]
    public void TheVersionIsTheOldestThatHoldsTheLevel()
    {
        Assert.Equal(Nav3dFormat.CubeVersion, Nav3dWriter.VersionOf(Nav3dFileTests.Sample()));
        Assert.Equal(Nav3dFormat.ShapedVersion, Nav3dWriter.VersionOf(Tall(255, 200, 55)));
        Assert.Equal(Nav3dFormat.Version, Nav3dWriter.VersionOf(Tall(256, 200, 56)));

        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(Nav3dWriter.Write(Tall(255, 200, 55)).AsSpan(8)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(Nav3dWriter.Write(Tall(300, 280, 20)).AsSpan(8)));
        Assert.Throws<ArgumentNullException>(() => Nav3dWriter.VersionOf(null!));
    }

    [Fact]
    public void ALevelTallerThanAByteKeepsItsHighRunsWhole()
    {
        byte[] bytes = Nav3dWriter.Write(Tall(300, 280, 20));

        // The low bytes where they always were, the high bytes in the pair
        // that is zero in every version 2 and 3 record.
        int high = Section(bytes, Nav3dFormat.LeavesTag) + Nav3dFormat.LeafRecordBytes;
        Assert.Equal((280 & 255, 20, 280 >> 8, 0), (bytes[high], bytes[high + 1], bytes[high + 6], bytes[high + 7]));
        int low = Section(bytes, Nav3dFormat.LeavesTag);
        Assert.Equal((0, 0), (bytes[low + 6], bytes[low + 7]));

        Nav3dReader nav = Nav3dReader.Open(bytes);
        Assert.Equal((Nav3dFormat.Version, 300, 300), (nav.Version, nav.TallestCell, nav.CellHeight(0)));
        Assert.Equal(((ushort)280, (ushort)20, 299), (nav.Leaf(1).ZLo, nav.Leaf(1).Height, nav.Leaf(1).ZHi));
        Assert.Equal(1, nav.FindLeaf(new Vec3(8, 8, (290 * 16) + 8)));
        Assert.Equal(1, nav.FindLeaf(0, 0, 0, 280));
        Assert.Equal(-1, nav.FindLeaf(0, 0, 0, 279));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(8, 8, 300 * 16)));
        Assert.Equal(300 * 16f, nav.LeafBounds(1).Maxs.Z);
        Assert.True(nav.TryVoxel(new Vec3(8, 8, (299 * 16) + 1), out _, out _, out _, out _, out int z));
        Assert.Equal(299, z);

        // Read back and written again: the same bytes.
        Assert.Equal(bytes, Nav3dWriter.Write(nav.ToLevel()));
    }

    [Fact]
    public void AVersion3FileCannotClaimATallCellOrAHighByte()
    {
        byte[] shaped = Nav3dWriter.Write(Tall(255, 200, 55));
        _ = Nav3dReader.Open(shaped);

        byte[] tallCell = [.. shaped];
        BinaryPrimitives.WriteInt32LittleEndian(tallCell.AsSpan(Section(tallCell, Nav3dFormat.CellHeightsTag)), 256);
        Assert.Contains("a placed cell of a version 3 file is 1 to 255",
            Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(tallCell)).Message, StringComparison.Ordinal);

        byte[] highByte = [.. shaped];
        highByte[Section(highByte, Nav3dFormat.LeavesTag) + Nav3dFormat.LeafRecordBytes + 6] = 1;
        Assert.Contains("leaf 1 runs from voxel 456 for 55",
            Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(highByte)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersion4CellIsAtMostSixteenBitsTall()
    {
        byte[] tall = Nav3dWriter.Write(Tall(300, 280, 20));
        BinaryPrimitives.WriteInt32LittleEndian(tall.AsSpan(Section(tall, Nav3dFormat.CellHeightsTag)), 65536);
        Assert.Contains("a placed cell of a version 4 file is 1 to 65535",
            Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(tall)).Message, StringComparison.Ordinal);

        byte[] later = Nav3dWriter.Write(Tall(300, 280, 20));
        BinaryPrimitives.WriteInt32LittleEndian(later.AsSpan(8), 5);
        Assert.Contains("this build reads versions 2 to 4",
            Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(later)).Message, StringComparison.Ordinal);

        Assert.Contains("cell 0 is 65536 voxels tall",
            Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(Tall(65536, 280, 20))).Message, StringComparison.Ordinal);
    }
}
