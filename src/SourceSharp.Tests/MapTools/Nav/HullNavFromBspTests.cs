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
    public void AFitIsAnsweredAsTheClearanceRecordAnswersIt() => _ = FitsMatch(Both());

    /// <summary>
    /// Under and beside brushes whose faces slope downward (a slab with a
    /// sloped underside, a ramp a player stands on and passes under), a fit
    /// is the brush test's at any feet: on a floor inside the voxel below, a
    /// step up, or mid-voxel. The map must hold such asks whose answer
    /// differs from the voxel bottom's, which one ceiling per voxel (version
    /// 1) answered wrongly.
    /// </summary>
    [Fact]
    public void BesideAnOverhangingBrushAFitIsTheBrushTestsAtAnyFeet()
    {
        (Nav3dReader nav, NavHullReader hull) = Both(overhang.Bsp, overhang.Bytes);
        Assert.Contains(Enumerable.Range(0, nav.LeafCount), l => Nav3dClearance.BrushCount(nav.ClearanceRecord(l, Nav3dClipClass.Player)) > 0);
        int beyond = FitsMatch((nav, hull));
        Assert.True(beyond > 0, "no ask by a brush differs from the voxel bottom's");

        // A record names every overhanging brush within reach; a key keeps
        // only those a body meets below its ceiling. Both happen here: a
        // brush-naming voxel whose key keeps none, and one whose key keeps one.
        int pruned = 0;
        int kept = 0;
        for (int leaf = 0; leaf < nav.LeafCount; leaf++)
        {
            if (Nav3dClearance.BrushCount(nav.ClearanceRecord(leaf, Nav3dClipClass.Player)) == 0)
            {
                continue;
            }

            (int cell, int x, int y) = nav.LeafColumn(leaf);
            uint overhangs = hull.Key((int)hull.Box(hull.FindBox(cell, x, y, nav.Leaf(leaf).ZLo)).Key).OverhangCount;
            pruned += overhangs == 0 ? 1 : 0;
            kept += overhangs > 0 ? 1 : 0;
        }

        Assert.True(pruned > 0 && kept > 0, $"{pruned} pruned, {kept} kept");
    }

    /// <summary>
    /// Every voxel's fit, as the <c>.nav3d</c> reader gives it, at the feet a
    /// game asks with: the voxel's bottom, its middle, half a voxel under it,
    /// and on a grounded run's bottom the floor and the floor raised a step
    /// (a wide body on stairs). Answers how many asks of a brush-naming voxel
    /// came out differently at those feet than at the voxel's bottom: the
    /// asks one ceiling per voxel could not answer.
    /// </summary>
    private static int FitsMatch((Nav3dReader Nav, NavHullReader Hull) both)
    {
        (Nav3dReader nav, NavHullReader hull) = both;
        double s = nav.VoxelSize;
        int beyondABottom = 0;
        foreach ((int leaf, int cell, int x, int y, int z) in Voxels(nav))
        {
            int box = hull.FindBox(cell, x, y, z);
            Nav3dLeaf l = nav.Leaf(leaf);
            double bottom = nav.Origin.Z + (z * s);
            List<double> feet = [bottom, bottom + (s * 0.5), bottom - (s * 0.5)];
            if (z == l.ZLo && l.IsGrounded(Nav3dClipClass.Player))
            {
                feet.Add(l.PlayerFloorZ);
                feet.Add(l.PlayerFloorZ + nav.StepHeight);
            }

            bool brushes = Nav3dClearance.BrushCount(nav.ClearanceRecord(leaf, Nav3dClipClass.Player)) > 0;
            foreach (float width in hull.Widths)
            {
                foreach (float height in Heights)
                {
                    bool atBottom = nav.FitsAtCentre(leaf, z, width, height, bottom, Nav3dClipClass.Player);
                    foreach (double originZ in feet)
                    {
                        bool exact = nav.FitsAtCentre(leaf, z, width, height, originZ, Nav3dClipClass.Player);
                        Assert.True(exact == hull.FitsAt(box, originZ, width, height), $"{width} x {height} from {originZ} at ({cell}: {x}, {y}, {z})");
                        beyondABottom += brushes && exact != atBottom ? 1 : 0;
                    }
                }
            }
        }

        return beyondABottom;
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
/// The hall of <see cref="BspNavMap"/> with two more brushes, both
/// overhanging (a face sloping downward), which the clearance records name:
/// a slab over the open floor between the block and the ledge whose
/// underside slopes from 80 up to 112 going east, and a ramp 8 thick rising
/// from the floor between the ledge and the clip.
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
        world.Children.Add(SlopedSlab(9100, (104, 120), (152, 200), (80, 112), (128, 128)));

        // A ramp 8 thick between the ledge and the clip, rising east from the
        // floor: stood on along its top, passed under at its high end.
        world.Children.Add(SlopedSlab(9101, (244, 40), (296, 120), (16, 48), (24, 56)));
        Bytes = await BspNavMap.CompileAsync(document);
        Bsp = await BspFile.LoadAsync(new MemoryStream(Bytes, writable: false));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A slab whose bottom and top faces each run straight from a west height to an east height.</summary>
    private static VmfChunk SlopedSlab(int id, (float X, float Y) mins, (float X, float Y) maxs, (float West, float East) bottom, (float West, float East) top)
    {
        (float x0, float y0) = mins;
        (float x1, float y1) = maxs;
        (float z0, float eastBottom) = bottom;
        (float z1, float eastTop) = top;
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

        // A slab's six sides in the winding the room model writes, the top
        // and bottom tilted: each west edge at its west height, east edge at
        // its east height.
        Side((x0, y1, z1), (x1, y1, eastTop), (x1, y0, eastTop));
        Side((x0, y0, z0), (x1, y0, eastBottom), (x1, y1, eastBottom));
        Side((x1, y1, eastTop), (x1, y1, eastBottom), (x1, y0, eastBottom));
        Side((x0, y1, z0), (x0, y1, z1), (x0, y0, z1));
        Side((x1, y1, eastTop), (x0, y1, z1), (x0, y1, z0));
        Side((x0, y0, z1), (x1, y0, eastTop), (x1, y0, eastBottom));
        return solid;
    }

    private static string P((float, float, float) p) =>
        string.Create(CultureInfo.InvariantCulture, $"({p.Item1} {p.Item2} {p.Item3})");
}
