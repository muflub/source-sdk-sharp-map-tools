//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The voxel heights of each cell a play leaf's box reaches
/// (<see cref="LevelNavFromBsp.PlaySpans"/>): each box padded by a voxel,
/// clipped to the grid, and merged per cell, so space no box reaches is
/// outside without the tree being asked.
/// </summary>
public sealed class NavPlaySpansTests
{
    private static Box B(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

    [Fact]
    public void ABoxReachesTheCellsItsPaddedFootprintCoversAtItsPaddedHeights()
    {
        // A grid of 2 x 2 cells of 256, 100 voxels of 16 up from z 0.
        int[][] spans = LevelNavFromBsp.PlaySpans([B(32, 32, 160, 224, 224, 320)], 2, 2, 100, 0, 0, 0, 256, 16);

        // Padded by a voxel: 144..336, voxels 9 to 21.
        Assert.Equal([9, 21], spans[0]);
        Assert.Empty(spans[1]);
        Assert.Empty(spans[2]);
        Assert.Empty(spans[3]);
    }

    [Fact]
    public void ABoxNearACellFaceReachesTheNextCellByItsPadding()
    {
        int[][] spans = LevelNavFromBsp.PlaySpans([B(32, 32, 0, 248, 100, 16)], 2, 1, 10, 0, 0, 0, 256, 16);

        Assert.Equal([0, 2], spans[0]);
        Assert.Equal([0, 2], spans[1]);
    }

    [Fact]
    public void OverlappingAndTouchingRangesMergeAndApartOnesStayApart()
    {
        Box[] boxes =
        [
            B(16, 16, 640, 64, 64, 800),
            B(16, 16, 160, 64, 64, 320),
            B(16, 16, 320, 64, 64, 480),
            B(16, 16, 496, 64, 64, 560),
        ];
        int[][] spans = LevelNavFromBsp.PlaySpans(boxes, 1, 1, 100, 0, 0, 0, 256, 16);

        // 9..21 and 19..31 overlap, 30..36 touches the merged 31; 39..51 stands apart.
        Assert.Equal([9, 36, 39, 51], spans[0]);
    }

    [Fact]
    public void ABoxIsClippedToTheGridAndOneWhollyOutsideIsIgnored()
    {
        Box[] boxes =
        [
            B(-512, -512, -500, 128, 128, 40),
            B(5000, 5000, 0, 5100, 5100, 100),
            B(16, 16, 9000, 64, 64, 9100),
            B(16, 16, 50, 64, 64, 50),
        ];
        int[][] spans = LevelNavFromBsp.PlaySpans(boxes, 1, 1, 10, 0, 0, 0, 256, 16);

        // Clipped to voxels 0..10; the far box, the one above the grid, and
        // nothing else; the flat box at 50 still reaches 2..5 once padded.
        Assert.Equal([0, 5], spans[0]);
    }

    [Fact]
    public void TheGridsOriginShiftsEveryRange()
    {
        int[][] spans = LevelNavFromBsp.PlaySpans([B(1040, 16, 5016, 1100, 64, 5100)], 2, 1, 20, 768, 0, 4992, 256, 16);

        // x 1024..1116 padded is cell 1 (768 + 256); z 5000..5116 from 4992 is voxels 0..8.
        Assert.Empty(spans[0]);
        Assert.Equal([0, 8], spans[1]);
    }
}
