//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The navigation of an ordinary compiled map (<see cref="BspNavMap"/>):
/// its grid, what is outside, its floors, steps, jumps, ladders, clips,
/// props and obstacles, and that the file is the same bytes however it is
/// built.
/// </summary>
public sealed class LevelNavFromBspTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    private Nav3dReader Open(BspNavOptions? options = null, bool props = true) =>
        Nav3dReader.Open(Nav3dWriter.Write(map.Build(options, props).Level));

    private static int LeafAt(Nav3dReader nav, float x, float y, float z)
    {
        int leaf = nav.FindLeaf(new Vec3(x, y, z));
        Assert.True(leaf >= 0, $"no leaf at ({x}, {y}, {z})");
        return leaf;
    }

    /// <summary>
    /// The grid covers the world's bounds in 256-unit cells, as tall as the
    /// map; the two cells beside the corridor are outside the map throughout
    /// and hold no columns; the file names the map's bytes and no level.
    /// </summary>
    [Fact]
    public void TheGridCoversTheWorldAndLeavesTheOutsideEmpty()
    {
        Nav3dLevel level = map.Build().Level;
        Assert.Equal((2, 3, 256f, 16, new Vec3(0, 0, 0)), (level.Columns, level.Rows, level.CellSize, level.CellVoxels, level.Origin));
        Assert.Null(level.CellHeights);
        Assert.Equal([true, true, true, false, true, false], level.Roots.Select(r => r >= 0));
        Assert.Equal(Nav3dBspBinding.Of(map.Bytes), level.Bsp);
        Assert.Equal((Guid.Empty, Guid.Empty), (level.LevelId, level.PackId));
        Assert.All(level.Cells, c => Assert.Null(c.Room));

        // Outside the corridor's east wall, inside a placed cell: masked solid.
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(level));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(200, 400, 100)));
        Assert.True(nav.MatchesBsp(map.Bytes));
    }

    /// <summary>
    /// Floors stand where the map's surfaces are: the hall's floor, the step,
    /// the ledge and the displacement's surface, each walkable for a player.
    /// </summary>
    [Fact]
    public void FloorsStandOnTheFloorTheStepTheLedgeAndTheDisplacement()
    {
        Nav3dReader nav = Open();
        foreach ((float x, float y, float z, float floor) in new[]
        {
            (256f, 128f, 24f, BspNavMap.Floor),
            (64f, 64f, 40f, BspNavMap.StepTop),
            (200f, 128f, 64f, BspNavMap.LedgeTop),
        })
        {
            Nav3dLeaf leaf = nav.Leaf(LeafAt(nav, x, y, z));
            Assert.True(leaf.IsWalkable(Nav3dClipClass.Player), $"({x}, {y}, {z})");
            Assert.Equal(floor, leaf.FloorZ(Nav3dClipClass.Player));
        }

        Nav3dLeaf patch = nav.Leaf(LeafAt(nav, 64, 448, 40));
        Assert.True(patch.IsWalkable(Nav3dClipClass.Player));
        Assert.InRange(patch.FloorZ(Nav3dClipClass.Player), BspNavMap.PatchTop - 0.01f, BspNavMap.PatchTop + 0.01f);
        Assert.True(nav.Standable(LeafAt(nav, 64, 448, 40), nav.Leaf(LeafAt(nav, 64, 448, 40)).ZLo, 32, 72, Nav3dClipClass.Player));

        // Without the displacement read, the corridor's floor there is the
        // brush under it, which the compile discarded: the floor below.
        NavGeometry plain = NavGeometry.FromBsp(map.Bsp);
        Assert.DoesNotContain(plain.Brushes, b => b.MinY == 400 && b.MaxY == 496);
    }

    /// <summary>
    /// The step is a walk (no jump link to it from beside it), the ledge a
    /// jump: a player link of rise 40 joins the hall floor beside it to its
    /// top, one column apart.
    /// </summary>
    [Fact]
    public void TheStepIsAWalkAndTheLedgeAJump()
    {
        Nav3dReader nav = Open();
        List<Nav3dJump> jumps = [.. Enumerable.Range(0, nav.JumpCount).Select(nav.Jump)];
        float Floor(uint leaf) => nav.Leaf((int)leaf).FloorZ(Nav3dClipClass.Player);
        Assert.Contains(jumps, j => (j.ClassMask & 1) != 0 && j.Columns == 1 && Math.Abs(j.Rise) == 40
            && Math.Min(Floor(j.LeafA), Floor(j.LeafB)) == BspNavMap.Floor && Math.Max(Floor(j.LeafA), Floor(j.LeafB)) == BspNavMap.LedgeTop);
        Assert.DoesNotContain(jumps, j => j.Columns == 1 && Math.Abs(j.Rise) <= nav.StepHeight);

        // The hall's column beside the step, one voxel up, opens onto the
        // step's top; the rise from the hall's floor is the step's 16.
        int hall = LeafAt(nav, 104, 64, 24);
        int step = LeafAt(nav, 88, 64, 40);
        bool joined = false;
        foreach (Nav3dNeighbour n in nav.Neighbours(LeafAt(nav, 104, 64, 40)))
        {
            joined |= n.Leaf == step;
        }

        Assert.True(joined);
        Assert.Equal(16f, nav.StepUp(hall, step, Nav3dClipClass.Player));
    }

    /// <summary>
    /// Both ladder entities make ladder leaves: as many ladder volumes as the
    /// entity lump has ladder entities, each holding a ladder leaf.
    /// </summary>
    [Fact]
    public void LadderLeavesAreWhereTheMapsLadderEntitiesAre()
    {
        Nav3dReader nav = Open();
        int entities = EntityLump.Parse(map.Bsp[BspLump.Entities])
            .Count(e => e.ClassName is "info_ladder" or "func_useableladder");
        Assert.Equal(2, entities);
        Assert.Equal(entities, NavGeometry.FromBsp(map.Bsp).Ladders.Count);
        Assert.True((nav.Leaf(LeafAt(nav, 464, 30, 100)).Flags & Nav3dLeafFlags.Ladder) != 0);
        Assert.True((nav.Leaf(LeafAt(nav, 64, 600, 100)).Flags & Nav3dLeafFlags.Ladder) != 0);
        Assert.Equal(Nav3dLeafFlags.None, nav.Leaf(LeafAt(nav, 256, 128, 100)).Flags & Nav3dLeafFlags.Ladder);
    }

    /// <summary>
    /// Doors and physics props are the obstacles, named as the entity lump
    /// names them; a leaf in the door is open with the door open and blocked
    /// with it shut.
    /// </summary>
    [Fact]
    public void ObstaclesAreTheEntityLumpsDoorsAndPropsByName()
    {
        Nav3dReader nav = Open();
        string[] fromLump = [.. EntityLump.Parse(map.Bsp[BspLump.Entities])
            .Where(e => NavGeometry.KindOf(e.ClassName) is not null).Select(e => e.Get("targetname") ?? "").Order(StringComparer.Ordinal)];
        Assert.Equal(["crate", "gate"], fromLump);
        Assert.Equal(fromLump, Enumerable.Range(0, nav.ObstacleCount).Select(o => nav.Obstacle(o).Name ?? "").Order(StringComparer.Ordinal));

        int gate = Enumerable.Range(0, nav.ObstacleCount).Single(o => nav.Obstacle(o).Name == "gate");
        Assert.Equal(Nav3dObstacleKind.Door, nav.Obstacle(gate).Kind);
        Assert.Equal((new Vec3(400, 16, 16), new Vec3(416, 240, 240)), (nav.Obstacle(gate).Mins, nav.Obstacle(gate).Maxs));
        Assert.Equal(1u, nav.Obstacle(gate).Cell);
        Assert.Equal(Nav3dObstacleKind.Physics, nav.Obstacle(1 - gate).Kind);
        Assert.NotEmpty(nav.ObstacleLeaves(gate).ToArray());

        int inDoor = LeafAt(nav, 408, 128, 24);
        bool[] shut = new bool[nav.ObstacleCount];
        Assert.True(nav.Passable(inDoor, nav.Leaf(inDoor).ZLo, 8, 8, Nav3dClipClass.Player, shut));
        shut[gate] = true;
        Assert.False(nav.Passable(inDoor, nav.Leaf(inDoor).ZLo, 8, 8, Nav3dClipClass.Player, shut));
    }

    /// <summary>The player clip blocks the player class and not the NPC class.</summary>
    [Fact]
    public void PlayerClipIsSolidForThePlayerClassOnly()
    {
        Nav3dReader nav = Open();
        int clip = LeafAt(nav, 332, 128, 64);
        int z = (int)((64 - nav.Origin.Z) / nav.VoxelSize);
        Assert.False(nav.Passable(clip, z, 8, 8, Nav3dClipClass.Player));
        Assert.True(nav.Passable(clip, z, 8, 8, Nav3dClipClass.Npc));
    }

    /// <summary>A static prop is solid by its model's hull; without the content it is open, with a warning naming the model.</summary>
    [Fact]
    public void AStaticPropIsSolidByItsHull()
    {
        Assert.Equal(-1, Open().FindLeaf(new Vec3(64, 176, 24)));

        BspNavResult bare = map.Build(props: false);
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(bare.Level));
        Assert.True(nav.FindLeaf(new Vec3(64, 176, 24)) >= 0);
        Assert.Contains(bare.Warnings, w => w.Contains("static prop model \"models/props_test/box.mdl\"", StringComparison.Ordinal));
        Assert.Contains(bare.Warnings, w => w.Contains("prop_physics crate", StringComparison.Ordinal));
        Assert.Single(bare.Level.Obstacles);
    }

    /// <summary>
    /// Building cell by cell from the solids within reach answers the same as
    /// building the whole hall at once: with 512-unit cells the hall is one
    /// cell, with 256-unit cells two, and a standing player fits in exactly
    /// the same voxels.
    /// </summary>
    [Fact]
    public void CellsStitchToTheSameAnswersAsOneBigCell()
    {
        Nav3dReader small = Open();
        Nav3dReader big = Open(new BspNavOptions { CellSize = 512 });
        Assert.Equal((1, 2), (big.Columns, big.Rows));
        int checkedVoxels = 0;
        for (float x = 8; x < 512; x += 16)
        {
            for (float y = 8; y < 768; y += 16)
            {
                for (float z = 8; z < 256; z += 16)
                {
                    Vec3 p = new(x, y, z);
                    int a = small.FindLeaf(p);
                    int b = big.FindLeaf(p);
                    Assert.Equal(a < 0, b < 0);
                    if (a < 0)
                    {
                        continue;
                    }

                    int za = (int)((z - small.Origin.Z) / small.VoxelSize);
                    Assert.Equal(
                        small.Standable(a, za, 32, 72, Nav3dClipClass.Player),
                        big.Standable(b, za, 32, 72, Nav3dClipClass.Player));
                    Assert.Equal(
                        small.Passable(a, za, 32, 32, Nav3dClipClass.Npc),
                        big.Passable(b, za, 32, 32, Nav3dClipClass.Npc));
                    checkedVoxels++;
                }
            }
        }

        Assert.True(checkedVoxels > 1000, $"{checkedVoxels} open voxels compared");
    }

    /// <summary>
    /// The same map gives the same file byte for byte: twice in a row, at one
    /// thread and at four, and under four builds running at once in this
    /// process.
    /// </summary>
    [Fact]
    public async Task TheSameMapGivesTheSameBytesEveryTime()
    {
        byte[] first = Nav3dWriter.Write(map.Build().Level);
        Assert.Equal(first, Nav3dWriter.Write(map.Build().Level));
        Assert.Equal(first, Nav3dWriter.Write(map.Build(new BspNavOptions { MaxDegree = 4 }).Level));

        Nav3dBspBinding binding = Nav3dBspBinding.Of(map.Bytes);
        BspNavResult[] together = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            LevelNavFromBsp.BuildAsync(map.Bsp, binding, BspNavMap.Hull, new BspNavOptions { MaxDegree = 2 })));
        Assert.All(together, r => Assert.Equal(first, Nav3dWriter.Write(r.Level)));
    }

    /// <summary>A map taller than a column holds, and a cell the voxel does not divide, are refused by name.</summary>
    [Fact]
    public void AMapTheGridCannotHoldIsRefused()
    {
        NavBuildException tall = Assert.Throws<NavBuildException>(() => map.Build(new BspNavOptions
        {
            Settings = NavSettings.Default with { VoxelSize = 1 },
            CellSize = 64,
        }));
        Assert.Contains("256 voxels of 1; a .nav3d column holds at most 255", tall.Message, StringComparison.Ordinal);

        Assert.Throws<NavBuildException>(() => map.Build(new BspNavOptions { CellSize = 250 }));
        Assert.Throws<NavBuildException>(() => LevelNavFromBsp.Build(new BspData(), default, null));
    }

    /// <summary>The inspector counts a grid's placed cells by their columns (its cells name no room) and names the map it is bound to.</summary>
    [Fact]
    public void TheInspectorCountsTheGridsCellsAndNamesTheMap()
    {
        string text = NavInspector.Describe(Open());
        Assert.Contains("grid 2 x 3 cells of 256 units, 4 placed;", text, StringComparison.Ordinal);
        Assert.Contains($"built from the map with {Nav3dBspBinding.Of(map.Bytes)}", text, StringComparison.Ordinal);
    }

    /// <summary>The models a build asks for: the obstacle props' and the solid static props'.</summary>
    [Fact]
    public void TheModelsAskedForAreThePropsModels() =>
        Assert.Equal([RoomPropHarness.BoxModel], LevelNavFromBsp.Models(map.Bsp));
}
