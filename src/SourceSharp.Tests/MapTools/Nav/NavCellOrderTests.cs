//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The order a compiled map's cells are built in (<see cref="LevelNavFromBsp.CostliestFirst"/>):
/// the cells with the most slanted solids within reach first, ties in cell
/// order, boxes not counted, every cell once.
/// </summary>
public sealed class NavCellOrderTests
{
    /// <summary>A slanted solid of a 16-unit edge standing at a point, tilted so it is not a box.</summary>
    private static NavBrush Slanted(float x, float y)
    {
        Vec3 n = new Vec3(0.3f, 0.1f, 1f).Normalise().Normalised;
        return NavBrush.FromPlanes([
            (n, Vec3.Dot(n, new Vec3(x, y, 16))), (new Vec3(0, 0, -1), 0f),
            (new Vec3(1, 0, 0), x + 8), (new Vec3(-1, 0, 0), -(x - 8)), (new Vec3(0, 1, 0), y + 8), (new Vec3(0, -1, 0), -(y - 8))], 1)!;
    }

    /// <summary>
    /// On a 4 x 3 grid of 256-unit cells with no reach: three slanted solids in
    /// cell 6, two in cell 1, one in cell 9 and a box in cell 11 give 6, 1, 9,
    /// then the rest in cell order, the box's cell among them.
    /// </summary>
    [Fact]
    public void TheCellsWithTheMostSlantedSolidsComeFirstThenCellOrder()
    {
        NavBrush[] brushes =
        [
            Slanted(600, 300), Slanted(560, 330), Slanted(620, 280),
            Slanted(300, 100), Slanted(320, 120),
            Slanted(330, 600),
            NavBrush.Box(new Vec3(800, 600, 0), new Vec3(830, 640, 40), 1),
        ];
        Assert.False(brushes[0].IsAxial);

        int[] order = LevelNavFromBsp.CostliestFirst(brushes, 4, 3, 0, 0, 256, 0);

        Assert.Equal([6, 1, 9, 0, 2, 3, 4, 5, 7, 8, 10, 11], order);
    }

    /// <summary>A solid counts in every cell its footprint grown by the reach touches, clamped to the grid.</summary>
    [Fact]
    public void ASolidCountsInEveryCellItsReachTouches()
    {
        NavBrush[] brushes = [Slanted(250, 250), Slanted(5, 5)];

        int[] order = LevelNavFromBsp.CostliestFirst(brushes, 3, 3, 0, 0, 256, 64);

        // (250, 250) reaches cells 0, 1, 3 and 4; (5, 5) reaches cell 0 alone.
        Assert.Equal([0, 1, 3, 4, 2, 5, 6, 7, 8], order);
    }
}
