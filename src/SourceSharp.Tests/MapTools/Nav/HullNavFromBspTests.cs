//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A compiled map's hull navigation (<see cref="HullNavFromBsp"/>) against
/// its <c>.nav3d</c>: the same grid, every voxel a leaf holds in exactly one
/// box, and every fit, stand, climb, shut door and player jump link answered
/// as the clearance records answer it, over the hall with its step, ledge,
/// clip, door, crate, prop, ladders and displacement.
/// </summary>
public sealed class HullNavFromBspTests(BspNavMap map, OverhangHallMap overhang) : IClassFixture<BspNavMap>, IClassFixture<OverhangHallMap>
{
    private static readonly float[] Heights = [20, 62, 72, 82, 200];

    private (Nav3dReader Nav, NavHullReader Hull) Both() => Both(map.Bsp, map.Bytes);

    private static (Nav3dReader Nav, NavHullReader Hull) Both(BspData bsp, byte[] bytes)
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(LevelNavFromBsp.Build(bsp, Nav3dBspBinding.Of(bytes), BspNavMap.Hull).Level));
        HullNavResult hull = HullNavFromBsp.Build(bsp, Nav3dBspBinding.Of(bytes), BspNavMap.Hull);
        return (nav, NavHullReader.Open(NavHullWriter.Write(hull.Level)));
    }

    /// <summary>Every (leaf, voxel) of the <c>.nav3d</c>: its cell, column and voxel.</summary>
    private static IEnumerable<(int Leaf, int Cell, int X, int Y, int Z)> Voxels(Nav3dReader nav)
    {
        for (int leaf = 0; leaf < nav.LeafCount; leaf++)
        {
            (int cell, int x, int y) = nav.LeafColumn(leaf);
            Nav3dLeaf l = nav.Leaf(leaf);
            for (int z = l.ZLo; z <= l.ZHi; z++)
            {
                yield return (leaf, cell, x, y, z);
            }
        }
    }

    [Fact]
    public void TheGridIsTheSameAndEveryLeafVoxelIsInOneBox()
    {
        (Nav3dReader nav, NavHullReader hull) = Both();
        Assert.Equal((nav.Origin, nav.Columns, nav.Rows, nav.CellVoxels, nav.TallestCell, nav.VoxelSize),
            (hull.Origin, hull.Columns, hull.Rows, hull.CellVoxels, hull.ColumnVoxels, hull.VoxelSize));
        Assert.True(hull.BoxCount * 2 < nav.LeafCount, $"{hull.BoxCount} boxes for {nav.LeafCount} leaves");

        long leafVoxels = 0;
        foreach ((_, int cell, int x, int y, int z) in Voxels(nav))
        {
            leafVoxels++;
            Assert.True(hull.FindBox(cell, x, y, z) >= 0, $"voxel ({cell}: {x}, {y}, {z})");
        }

        long boxVoxels = Enumerable.Range(0, hull.BoxCount).Sum(b =>
        {
            NavHullBox box = hull.Box(b);
            return (long)(box.X1 - box.X0) * (box.Y1 - box.Y0) * box.Height;
        });
        Assert.Equal(leafVoxels, boxVoxels);
    }

    [Fact]
    public void AFitIsAnsweredAsTheClearanceRecordAnswersIt() => FitsMatch(Both());

    /// <summary>
    /// Under a brush whose underside slopes, the ceiling is the head room the
    /// brush leaves a centred body on the voxel's bottom: the fit there is
    /// the one the brush test gives.
    /// </summary>
    [Fact]
    public void UnderAnOverhangingBrushAFitIsTheBrushTests()
    {
        (Nav3dReader nav, NavHullReader hull) = Both(overhang.Bsp, overhang.Bytes);
        Assert.Contains(Enumerable.Range(0, nav.LeafCount), l => Nav3dClearance.BrushCount(nav.ClearanceRecord(l, Nav3dClipClass.Player)) > 0);
        FitsMatch((nav, hull));
    }

    private static void FitsMatch((Nav3dReader Nav, NavHullReader Hull) both)
    {
        (Nav3dReader nav, NavHullReader hull) = both;
        double s = nav.VoxelSize;
        foreach ((int leaf, int cell, int x, int y, int z) in Voxels(nav))
        {
            int box = hull.FindBox(cell, x, y, z);
            double originZ = nav.Origin.Z + (z * s);
            foreach (float width in hull.Widths)
            {
                foreach (float height in Heights)
                {
                    Assert.True(nav.FitsAtCentre(leaf, z, width, height, originZ, Nav3dClipClass.Player) == hull.FitsAt(box, originZ, width, height),
                        $"{width} x {height} at ({cell}: {x}, {y}, {z})");
                }
            }
        }
    }

    [Fact]
    public void StandingClimbingAndShutDoorsAreAnsweredAsTheRecordsAnswerThem()
    {
        (Nav3dReader nav, NavHullReader hull) = Both();
        Assert.True(nav.ObstacleCount > 0);
        int stands = 0;
        int climbs = 0;
        for (int leaf = 0; leaf < nav.LeafCount; leaf++)
        {
            (int cell, int x, int y) = nav.LeafColumn(leaf);
            Nav3dLeaf l = nav.Leaf(leaf);
            int box = hull.FindBox(cell, x, y, l.ZLo);
            if (hull.Box(box).ZLo != l.ZLo)
            {
                // A leaf a box continues has no floor of its own.
                Assert.False(l.IsWalkable(Nav3dClipClass.Player));
                continue;
            }

            foreach (float width in hull.Widths)
            {
                foreach (float height in Heights)
                {
                    bool exact = nav.StandableAtCentre(leaf, width, height, Nav3dClipClass.Player);
                    Assert.Equal(exact, hull.Stands(box, width, height));
                    stands += exact ? 1 : 0;

                    bool climb = (l.Flags & Nav3dLeafFlags.Ladder) != 0 && nav.PassableAtCentre(leaf, l.ZLo, width, height, Nav3dClipClass.Player);
                    Assert.Equal(climb, hull.Climbs(box, width, height));
                    climbs += climb ? 1 : 0;

                    for (int o = 0; o < nav.ObstacleCount; o++)
                    {
                        bool[] blocking = new bool[nav.ObstacleCount];
                        blocking[o] = true;
                        Assert.Equal(nav.StandableAtCentre(leaf, width, height, Nav3dClipClass.Player, blocking), hull.StandsWithShut(box, o, width, height));
                    }
                }
            }
        }

        Assert.True(stands > 0 && climbs > 0, $"{stands} stands, {climbs} climbs");
        Assert.Contains(Enumerable.Range(0, hull.ObstacleCount), o => hull.ObstacleBoxes(o).Length > 0);
    }

    [Fact]
    public void ThePlayersJumpLinksAreTheNav3dsColumnForColumn()
    {
        (Nav3dReader nav, NavHullReader hull) = Both();
        HashSet<(int, int, int, int, int, int, float)> exact = [];
        for (int j = 0; j < nav.JumpCount; j++)
        {
            Nav3dJump jump = nav.Jump(j);
            if ((jump.ClassMask & 1) == 0)
            {
                continue;
            }

            (int ca, int xa, int ya) = nav.LeafColumn((int)jump.LeafA);
            (int cb, int xb, int yb) = nav.LeafColumn((int)jump.LeafB);
            exact.Add(ca < cb || (ca == cb && (ya * 100) + xa <= (yb * 100) + xb)
                ? (ca, xa, ya, cb, xb, yb, jump.Rise)
                : (cb, xb, yb, ca, xa, ya, -jump.Rise));
        }

        HashSet<(int, int, int, int, int, int, float)> boxes = [];
        for (int j = 0; j < hull.JumpCount; j++)
        {
            NavHullJump jump = hull.Jump(j);
            (int ca, int xa, int ya) = (hull.BoxCell((int)jump.BoxA), jump.AX, jump.AY);
            (int cb, int xb, int yb) = (hull.BoxCell((int)jump.BoxB), jump.BX, jump.BY);
            boxes.Add(ca < cb || (ca == cb && (ya * 100) + xa <= (yb * 100) + xb)
                ? (ca, xa, ya, cb, xb, yb, jump.Rise)
                : (cb, xb, yb, ca, xa, ya, -jump.Rise));
        }

        Assert.NotEmpty(exact);
        Assert.Equal(exact.Order(), boxes.Order());
    }

    [Fact]
    public void TheFileIsTheSameBytesAtAnyThreadCount()
    {
        byte[] one = NavHullWriter.Write(HullNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), BspNavMap.Hull, null, new BspNavOptions { MaxDegree = 1 }).Level);
        byte[] four = NavHullWriter.Write(HullNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), BspNavMap.Hull, null, new BspNavOptions { MaxDegree = 4 }).Level);
        Assert.Equal(one, four);
    }

    [Fact]
    public async Task TheWidthsAreSortedAndOnlyPositiveOnesAreBuilt()
    {
        Assert.Equal([32f, 48f], HullNavFromBsp.DefaultWidths);
        HullNavResult result = await HullNavFromBsp.BuildAsync(map.Bsp, Nav3dBspBinding.Of(map.Bytes), BspNavMap.Hull, [48, 16, 48]);
        Assert.Equal([16f, 48f], result.Level.Widths);

        Assert.Throws<NavBuildException>(() => HullNavFromBsp.Build(map.Bsp, default, null, [0]));
        Assert.Throws<NavBuildException>(() => HullNavFromBsp.Build(map.Bsp, default, null, []));
        Assert.Throws<NavBuildException>(() => HullNavFromBsp.Build(map.Bsp, default, null, [.. Enumerable.Range(1, 17).Select(i => (float)i)]));
    }
}

/// <summary>
/// The hall of <see cref="BspNavMap"/> with one more brush: a slab over the
/// open floor between the block and the ledge whose underside slopes from 80
/// up to 112 going east, an overhanging brush the clearance records name.
/// </summary>
public sealed class OverhangHallMap : IAsyncLifetime
{
    /// <summary>The compiled map's bytes.</summary>
    public byte[] Bytes { get; private set; } = [];

    /// <summary>The map as read back from <see cref="Bytes"/>.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        VmfDocument document = BspNavMap.Vmf();
        VmfChunk world = document.Chunks.First(c => c.Name == MapFileLoader.WorldChunk);
        world.Children.Add(SlopedSlab(9100, (104, 120, 80), (152, 200, 128), 112));
        Bytes = await BspNavMap.CompileAsync(document);
        Bsp = await BspFile.LoadAsync(new MemoryStream(Bytes, writable: false));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A box whose bottom face rises from <c>mins.Z</c> at its west edge to <paramref name="eastBottom"/> at its east edge.</summary>
    private static VmfChunk SlopedSlab(int id, (float X, float Y, float Z) mins, (float X, float Y, float Z) maxs, float eastBottom)
    {
        (float x0, float y0, float z0) = mins;
        (float x1, float y1, float z1) = maxs;
        VmfChunk solid = new("solid");
        solid.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        void Side((float, float, float) a, (float, float, float) b, (float, float, float) c)
        {
            VmfChunk side = solid.AddChunk("side");
            side.AddKey("id", (id * 8).ToString(CultureInfo.InvariantCulture));
            side.AddKey("plane", $"{P(a)} {P(b)} {P(c)}");
            side.AddKey("material", RoomHarness.Plain);
            side.AddKey("uaxis", "[1 0 0 0] 0.25");
            side.AddKey("vaxis", "[0 -1 0 0] 0.25");
            side.AddKey("rotation", "0");
            side.AddKey("lightmapscale", "16");
            side.AddKey("smoothing_groups", "0");
        }

        // A slab's six sides in the winding the room model writes, the bottom
        // tilted: its west edge at z0, its east edge at the east bottom.
        Side((x0, y1, z1), (x1, y1, z1), (x1, y0, z1));
        Side((x0, y0, z0), (x1, y0, eastBottom), (x1, y1, eastBottom));
        Side((x1, y1, z1), (x1, y1, eastBottom), (x1, y0, eastBottom));
        Side((x0, y1, z0), (x0, y1, z1), (x0, y0, z1));
        Side((x1, y1, z1), (x0, y1, z1), (x0, y1, z0));
        Side((x0, y0, z1), (x1, y0, z1), (x1, y0, z0));
        return solid;
    }

    private static string P((float, float, float) p) =>
        string.Create(CultureInfo.InvariantCulture, $"({p.Item1} {p.Item2} {p.Item3})");
}
