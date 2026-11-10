//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.MapTools.Vis;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.MultiCellHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The link of rooms larger than one cell (the rooms design, 17.8): the top
/// tree routes every covered cell to that cell's own subtree, the room's
/// block nodes are left out, joints are per cell edge, and the linked level
/// holds every sample to the flattened level's compile at every turn.
/// </summary>
public sealed class MultiCellLinkTests
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The 2 x 2 hall at each turn in the south-west of a 3 x 3 grid, hubs
    /// along its east and north: the link and the flattened compile put
    /// every sample in the same space; the top tree reaches every node once
    /// and no block node of the hall; every open leaf is inside its nodes'
    /// bounds; the map passes the loader checks; and the link is the same
    /// bytes at one thread and four.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task AHallLinksAsItFlattensAtEveryTurn(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall, Hub);
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("hub, hub, hub", "+, +, hub", $"hall@{rotation}, +, hub");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await RoomHeightHarness.CompileFlatAsync(library, level);

        Assert.True(RoomHeightHarness.SameSpace(linked.Bsp, flat, level) > 0);
        Assert.Null(RoomHeightHarness.BoundsProblem(linked.Bsp));
        Assert.Equal(BspStructView.Count<DNode>(linked.Bsp[BspLump.Nodes]), ReachedOnce(linked.Bsp));

        // Inside the hall, across its internal cell faces, a line is clear.
        foreach (BspData map in new[] { linked.Bsp, flat })
        {
            LevelProbe probe = new(map);
            Assert.Equal(1f, probe.Trace(new Vec3(100, 100, 60), new Vec3(400, 400, 60), (int)BrushContents.Solid));
        }

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
        LinkedLevel four = await RoomPropHarness.LinkAsync(rooms, level, degree: 4);
        Assert.Equal(await MultiLibraryHarness.BytesAsync(linked.Bsp), await MultiLibraryHarness.BytesAsync(four.Bsp));
    }

    /// <summary>
    /// A W x D placement's transform is exact at every turn: the turn and the
    /// move done apart (<c>Translate(Rotate(p))</c>) are <c>Apply(p)</c> bit
    /// for bit; a room-local cell's centre lands in the world cell
    /// <see cref="RoomPlacement.WorldCell"/> names, which
    /// <see cref="RoomPlacement.LocalCell"/> undoes; and the footprint's
    /// box lands on the cells <see cref="RoomPlacement.Covered"/> lists.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public void AFootprintsTransformIsExact(int rotation)
    {
        RoomPlacement placement = new("hall", 3, 5, rotation / 90) { CellsX = 2, CellsY = 3 };
        RoomTransform transform = new(placement, 256);
        float[] coords = [0, 0.1f, 17.25f, 255.99f, 256, 300.5f, 511, 512, 700.125f, 768, -0f];
        foreach (float x in coords)
        {
            foreach (float y in coords)
            {
                Vec3 p = new(x, y, 33.3f);
                Vec3 apply = transform.Apply(p);
                Vec3 apart = transform.Translate(RoomTransform.Rotate(p, rotation / 90));
                Assert.Equal(BitConverter.SingleToInt32Bits(apply.X), BitConverter.SingleToInt32Bits(apart.X));
                Assert.Equal(BitConverter.SingleToInt32Bits(apply.Y), BitConverter.SingleToInt32Bits(apart.Y));
            }
        }

        List<(int X, int Y)> world = [];
        foreach ((int wx, int wy, int lx, int ly) in placement.Covered())
        {
            Vec3 centre = transform.Apply(new Vec3((lx * 256) + 128, (ly * 256) + 128, 0));
            Assert.Equal((wx, wy), ((int)MathF.Floor(centre.X / 256), (int)MathF.Floor(centre.Y / 256)));
            Assert.Equal((lx, ly), placement.LocalCell(wx, wy));
            world.Add((wx, wy));
        }

        Assert.Equal(6, world.Distinct().Count());
        Assert.Equal((rotation / 90) % 2 == 0 ? (2, 3) : (3, 2), (placement.SpanCellsX, placement.SpanCellsY));
        Assert.Equal(3, world.Min(c => c.X));
        Assert.Equal(5, world.Min(c => c.Y));
        Assert.Equal(3 + placement.SpanCellsX - 1, world.Max(c => c.X));
        Assert.Equal(5 + placement.SpanCellsY - 1, world.Max(c => c.Y));
    }

    /// <summary>
    /// A room of one cell moves by the cell size exactly as it always did:
    /// <c>Apply</c> adds the very value it added before footprints, bit for
    /// bit, negative zero included.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public void AOneCellRoomsTransformIsUnchanged(int rotation)
    {
        const float c = 256;
        RoomPlacement placement = new("hub", 4, 7, rotation / 90);
        RoomTransform transform = new(placement, c);
        float tx = 4 * c, ty = 7 * c;
        foreach (float v in new[] { -0f, 0f, 1.5f, 255.75f, 256f })
        {
            Vec3 p = new(v, 256 - v, v);
            Vec3 expected = (rotation / 90) switch
            {
                0 => new Vec3(p.X + tx, p.Y + ty, p.Z),
                1 => new Vec3(-p.Y + tx + c, p.X + ty, p.Z),
                2 => new Vec3(-p.X + tx + c, -p.Y + ty + c, p.Z),
                _ => new Vec3(p.Y + tx, -p.X + ty + c, p.Z),
            };
            Vec3 actual = transform.Apply(p);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        }
    }

    /// <summary>
    /// A 1 x 2 gallery beside the hall shares two cell edges with it, with a
    /// door at one: the two join at that edge alone (the gallery's east1 and
    /// the hall's west1), the other edge's wall stays shut, and the link
    /// holds every sample to the flattened compile.
    /// </summary>
    [Fact]
    public async Task RoomsSharingSeveralCellEdgesJoinAtTheirDoor()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall, Gallery, Hub);
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("+, +, +", "gallery, hall, +");
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Assert.Equal([("east1", "west1")], layout.Rooms.Single(r => r.Placement.Room == "gallery").Joints);
        Assert.Equal([("west1", "east1")], layout.Rooms.Single(r => r.Placement.Room == "hall").Joints);

        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await RoomHeightHarness.CompileFlatAsync(library, level);
        Assert.True(RoomHeightHarness.SameSpace(linked.Bsp, flat, level) > 0);
        Assert.Equal(BspStructView.Count<DNode>(linked.Bsp[BspLump.Nodes]), ReachedOnce(linked.Bsp));
        foreach (BspData map in new[] { linked.Bsp, flat })
        {
            LevelProbe probe = new(map);
            Assert.Equal(1f, probe.Trace(new Vec3(128, 256 + 128, 60), new Vec3(256 + 128, 256 + 128, 60), (int)BrushContents.Solid));
            Assert.True(probe.Trace(new Vec3(128, 128, 60), new Vec3(256 + 128, 128, 60), (int)BrushContents.Solid) < 1f);
        }
    }

    /// <summary>
    /// The superset gate over larger rooms: the flattened level compiled and
    /// vis'd whole, every cluster pair it sees is seen by the link, which
    /// composes the hall's own exact vis with the door graph over its cell
    /// edges; and the link sees across the hall's internal cell faces, from
    /// one of its cells to the cell diagonally opposite.
    /// </summary>
    /// <remarks>
    /// The two maps cut their rooms into clusters differently (the link's
    /// larger rooms by cell, the flattened compile by its own blocks), so a
    /// cluster of the flattened map can cover several of the link's. A line
    /// the flattened map sees from one cluster to another runs between one
    /// of the link's clusters under each, so the gate asks that some pair
    /// of them see each other: every pair would ask the link to see what no
    /// line does.
    /// </remarks>
    [Fact]
    public async Task TheLinkedPvsIsASupersetOfTheFlattenedCompiles()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall, Gallery, Hub);
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("+, +, +, hub", "gallery, hall, +, hub");
        LinkedLevel link = await RoomPropHarness.LinkAsync(rooms, level);
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomLightHarness.ContextAsync("flat"));
        VisResult monoVis = await RoomHarness.VisAsync(whole.Bsp!, PortalSet.FromPortalFile(whole.Portals!));
        int[][] map = LevelLinkerTests.MapClusters(whole.Bsp!, link.Bsp);
        int pairs = 0;
        for (int from = 0; from < monoVis.ClusterCount; from++)
        {
            for (int to = 0; to < monoVis.ClusterCount; to++)
            {
                if (!monoVis.CanSee(from, to) || map[from].Length == 0 || map[to].Length == 0)
                {
                    continue;
                }

                Assert.True(
                    map[from].Any(a => map[to].Any(b => link.Vis.CanSee(a, b))),
                    $"the flattened compile sees {from} {Where(whole.Bsp!, from)} -> {to} {Where(whole.Bsp!, to)}; the link sees none of its pairs");
                pairs++;
            }
        }

        Assert.True(pairs > 0);
        int sw = RoomHarness.LeafAt(link.Bsp, new Vec3(256 + 100, 100, 60)).Cluster;
        int ne = RoomHarness.LeafAt(link.Bsp, new Vec3(512 + 150, 256 + 150, 60)).Cluster;
        Assert.True(sw >= 0 && ne >= 0 && sw != ne && link.Vis.CanSee(sw, ne));
    }

    /// <summary>
    /// A room of one cell beside a larger room, sharing a wall with no door
    /// in it, may still see into it round through a third room: the hub at
    /// (2, 1) sees the hall through the hub at (2, 0), whose doors meet both.
    /// The two-rooms-one-wall rule, sound for rooms of one cell, would drop
    /// that line; the flattened compile sees it, and so must the link.
    /// </summary>
    [Fact]
    public async Task ARoomSeesALargerNeighbourRoundThroughAThird()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall, Hub);
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("+, +, hub", "hall, +, hub");
        LinkedLevel link = await RoomPropHarness.LinkAsync(rooms, level);
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomLightHarness.ContextAsync("flat"));
        VisResult monoVis = await RoomHarness.VisAsync(whole.Bsp!, PortalSet.FromPortalFile(whole.Portals!));
        int[][] map = LevelLinkerTests.MapClusters(whole.Bsp!, link.Bsp);
        int north = RoomHarness.LeafAt(whole.Bsp!, new Vec3(512 + 128, 256 + 128, 60)).Cluster;
        int checkedPairs = 0;
        for (int to = 0; to < monoVis.ClusterCount; to++)
        {
            if (monoVis.CanSee(north, to) && map[to].Length > 0 && map[north].Length > 0)
            {
                Assert.True(map[north].Any(a => map[to].Any(b => link.Vis.CanSee(a, b))), $"{north} -> {to} {Where(whole.Bsp!, to)}");
                checkedPairs++;
            }
        }

        Assert.True(checkedPairs > 2);

        // The rule would clear every cluster of the hall from the hub's row;
        // the door flows through the hub at (2, 0) keep some of them.
        int b = RoomHarness.LeafAt(link.Bsp, new Vec3(512 + 128, 256 + 128, 60)).Cluster;
        int[] hall = [.. Enumerable.Range(0, link.Plan.Rooms[0].Room.ClusterCount)];
        Assert.Equal("hall", link.Plan.Rooms[0].Room.Definition.Name);
        Assert.Contains(hall, c => link.Vis.CanSee(b, c));
    }

    private static string Where(BspData bsp, int cluster)
    {
        DLeaf[] leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        DLeaf leaf = leaves.First(l => l.Cluster == cluster);
        return $"[{leaf.Mins[0]} {leaf.Mins[1]} {leaf.Mins[2]} .. {leaf.Maxs[0]} {leaf.Maxs[1]} {leaf.Maxs[2]}]";
    }

    // ---- the level file -----------------------------------------------------------------------

    /// <summary>
    /// A larger room is written in its anchor cell and <c>+</c> in every
    /// other cell it covers; the file reads and writes back to its own text,
    /// and the layout places the room once, covering its cells.
    /// </summary>
    [Fact]
    public void ALevelFileMarksTheCellsALargerRoomCovers()
    {
        string text = RoomHarness.LevelText("rooms.vmf", "+, +, hub", "hall@90, +, hub");
        LevelGrid level = LevelYaml.Parse(text, "level");
        string written = LevelYaml.Write(level);
        Assert.Contains("  - [hall@90, +, hub]\n", written, StringComparison.Ordinal);
        Assert.Equal(written, LevelYaml.Write(LevelYaml.Parse(written, "level")));
        Assert.Equal(["hall", "hub", "hub"], level.Placed.Select(p => p.Cell.Room));
        Assert.True(level[1, 1]!.IsCovered);

        LevelLayout layout = level.ToLayout(name => name == "hall" ? Hall : Hub, 256, RoomHarness.WalkableKit);
        RoomPlacement hall = layout.Rooms[0].Placement;
        Assert.Equal((2, 2, 1), (hall.CellsX, hall.CellsY, hall.Rotation));
        Assert.Equal([(0, 0), (0, 1), (1, 0), (1, 1)], hall.Covered().Select(c => (c.X, c.Y)).Order());
    }

    /// <summary>
    /// Every way a level file can mark a larger room's cells wrongly is
    /// refused with the rooms design's 17.3 texts, the cell's line and column
    /// in front: a <c>+</c> with a turn, a <c>+</c> no room covers, a covered
    /// cell holding a room or nothing, and a footprint running off the grid.
    /// </summary>
    [Theory]
    [InlineData(new[] { "+, +@90, hub", "hall, +, hub" }, "line 5, column 9: \"+@90\" is not a cell; + marks a cell a larger room covers, alone, with no rotation.")]
    [InlineData(new[] { "+, +, +", "hall, +, hub" }, "line 5, column 12: + is in no room's footprint; + marks the cells a larger room covers besides its south-west one.")]
    [InlineData(new[] { "+, hub, hub", "hall, +, hub" }, "line 5, column 9: room hall at cell (0, 0) covers cell (1, 1), which holds hub; write + there.")]
    [InlineData(new[] { "+, ~, hub", "hall, +, hub" }, "line 6, column 6: room hall at cell (0, 0) covers cell (1, 1), which holds ~; write + there.")]
    [InlineData(new[] { "hub, hub, hub", "hub, hub, hall" }, "line 6, column 16: room hall at cell (2, 0) turned 0 covers 2 x 2 cells and runs off the 2x3 grid.")]
    public void AMisMarkedFootprintIsRefused(string[] rows, string message)
    {
        Exception refused = Assert.ThrowsAny<Exception>(() =>
            LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "level").ToLayout(name => name == "hall" ? Hall : Hub, 256, RoomHarness.WalkableKit));
        Assert.Equal(message, refused.Message);
    }

    /// <summary>
    /// The generator lays out rooms of one cell only: a library holding a
    /// larger room generates the level it generates without it, and the
    /// header says how many it left out.
    /// </summary>
    [Fact]
    public void TheGeneratorLeavesLargerRoomsOut()
    {
        RoomDefinition[] small = [Hub, RoomHarness.WalkableRoom("end", RoomFacing.PositiveX)];
        LevelGeneratorOptions options = new(3, 3, 7);
        LevelGrid without = LevelGenerator.Generate(small, options, "level", "rooms.vmf", null);
        LevelGrid with = LevelGenerator.Generate([Hub, Hall, small[1]], options, "level", "rooms.vmf", null);
        Assert.Equal(LevelYaml.Write(without), LevelYaml.Write(with));
        Assert.Equal(LevelGenerator.Header(options, without), LevelGenerator.Header(options, with, 0));
        Assert.EndsWith(", 1 room(s) larger than one cell left out", LevelGenerator.Header(options, with, 1)[0], StringComparison.Ordinal);
    }

    /// <summary>Walks model 0's tree from its head: every node is reached exactly once (no node has two parents); returns the count.</summary>
    internal static int ReachedOnce(BspData bsp)
    {
        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        int[] seen = new int[nodes.Length];
        Stack<int> stack = new([BspStructView.As<DModel>(bsp[BspLump.Models])[0].HeadNode]);
        int reached = 0;
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            seen[n]++;
            Assert.True(seen[n] == 1, $"node {n} is reached twice");
            reached++;
            for (int side = 0; side < 2; side++)
            {
                if (nodes[n].Children[side] >= 0)
                {
                    stack.Push(nodes[n].Children[side]);
                }
            }
        }

        // The brush models' trees are reached from their own heads.
        foreach (DModel model in BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray().Skip(1))
        {
            Stack<int> more = new([model.HeadNode]);
            while (more.Count > 0)
            {
                int n = more.Pop();
                reached++;
                for (int side = 0; side < 2; side++)
                {
                    if (nodes[n].Children[side] >= 0)
                    {
                        more.Push(nodes[n].Children[side]);
                    }
                }
            }
        }

        return reached;
    }
}
