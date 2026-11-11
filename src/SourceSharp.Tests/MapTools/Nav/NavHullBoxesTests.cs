//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The hull build's two merges: a run continues the one under it only with
/// the same answers and no floor of its own
/// (<see cref="HullNavFromBsp.Continues"/>), and a cell's runs merge east,
/// then north, into boxes (<see cref="HullNavFromBsp.Boxes"/>).
/// </summary>
public sealed class NavHullBoxesTests
{
    private static byte[] Key(NavHullFlags flags, float floor, float ceiling, ushort cost = 256)
    {
        byte[] k = new byte[8 + 4 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(k, (ushort)flags);
        BinaryPrimitives.WriteUInt16LittleEndian(k.AsSpan(2), cost);
        BinaryPrimitives.WriteSingleLittleEndian(k.AsSpan(4), floor);
        BinaryPrimitives.WriteSingleLittleEndian(k.AsSpan(8), ceiling);
        return k;
    }

    [Fact]
    public void ARunContinuesTheOneUnderItOnlyWithTheSameAnswersAndNoFloor()
    {
        byte[] floor = Key(NavHullFlags.Grounded | NavHullFlags.Walkable, 16, 200);
        Assert.True(HullNavFromBsp.Continues(floor, Key(NavHullFlags.None, 0, 200)));
        Assert.True(HullNavFromBsp.Continues(Key(NavHullFlags.Water, 0, 200), Key(NavHullFlags.Water, 0, 200)));

        Assert.False(HullNavFromBsp.Continues(floor, Key(NavHullFlags.Grounded, 32, 200)));
        Assert.False(HullNavFromBsp.Continues(floor, Key(NavHullFlags.None, 0, 180)));
        Assert.False(HullNavFromBsp.Continues(floor, Key(NavHullFlags.Water, 0, 200)));
        Assert.False(HullNavFromBsp.Continues(floor, Key(NavHullFlags.None, 0, 200, 512)));
        Assert.False(HullNavFromBsp.Continues(floor, [.. Key(NavHullFlags.None, 0, 200), 0, 0, 0, 0]));
    }

    [Fact]
    public void RunsMergeEastThenNorthIntoBoxes()
    {
        // A 3 x 3 cell: the same run in the south-west 2 x 2, an L of
        // another run's in the rest, and a lone run over the centre.
        HullNavFromBsp.HullRun a = new(0, 4, 0);
        HullNavFromBsp.HullRun b = new(0, 4, 1);
        HullNavFromBsp.HullRun c = new(6, 2, 2);
        List<HullNavFromBsp.HullRun>[] columns =
        [
            [a], [a], [b],
            [a], [a, c], [b],
            [b], [b], [b],
        ];

        NavHullBox[] boxes = HullNavFromBsp.Boxes(columns, 3);

        Assert.Equal(
            [
                new NavHullBox(0, 0, 2, 2, 0, 4, 0),
                new NavHullBox(2, 0, 3, 3, 0, 4, 1),
                new NavHullBox(1, 1, 2, 2, 6, 2, 2),
                new NavHullBox(0, 2, 2, 3, 0, 4, 1),
            ],
            boxes);
    }

    [Fact]
    public void ARunTakenByOneBoxIsNotTakenAgain()
    {
        // Two equal runs stacked apart in one column, beside a column with
        // one: the lower ones merge, the upper stays its own.
        HullNavFromBsp.HullRun low = new(0, 2, 0);
        HullNavFromBsp.HullRun high = new(5, 2, 0);
        List<HullNavFromBsp.HullRun>[] columns = [[low, high], [low], [], []];

        NavHullBox[] boxes = HullNavFromBsp.Boxes(columns, 2);

        Assert.Equal([new NavHullBox(0, 0, 2, 1, 0, 2, 0), new NavHullBox(0, 0, 1, 1, 5, 2, 0)], boxes);
    }

    /// <summary>
    /// The jump search over the sample's boxes: its floor at 0 and the
    /// ladder's at 32, one column apart east, are a link; a jump distance
    /// shorter than a voxel reaches no column, and the thread count is at
    /// least one.
    /// </summary>
    [Fact]
    public void JumpLinksJoinFloorsInReachAndNoneWhenNoColumnIsInReach()
    {
        NavHullLevel sample = NavHullFileTests.Sample();
        NavHullReader nav = NavHullReader.Open(NavHullWriter.Write(sample));
        IReadOnlyList<NavHullJump> links = NavHullJumps.Find(nav, 2);
        Assert.Contains(new NavHullJump(0, 3, 1, 0, 0, 0, 32, Nav3dDirection.East, 1), links);
        Assert.All(links, j => Assert.True(j.BoxA < j.BoxB));

        NavHullReader near = NavHullReader.Open(NavHullWriter.Write(new NavHullLevel
        {
            VoxelSize = sample.VoxelSize, CellVoxels = sample.CellVoxels, Columns = sample.Columns, Rows = sample.Rows,
            ColumnVoxels = sample.ColumnVoxels, StepHeight = 18, JumpHeight = 56, JumpDistance = 8, Widths = sample.Widths,
            CellStarts = sample.CellStarts, Boxes = sample.Boxes, Keys = sample.Keys, Shuts = sample.Shuts, Overhangs = sample.Overhangs, Obstacles = sample.Obstacles,
        }));
        Assert.Empty(NavHullJumps.Find(near, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavHullJumps.Find(nav, 0));
    }
}
