//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.MapTools.Vis;
using SourceSharp.MapTools.Bsp.Driver;

using SourceSharp.Tests.MapTools.Nav;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.DoorSlotHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Levels of rooms under a kit of three door slots, linked and held to the
/// flattened level's whole compile (the rooms design, 17.8 stage 9): a room
/// with two doors on one wall, a corridor entering its neighbours at a
/// corner slot, and a 2 x 2 room with slots, at every turn of the larger
/// room; the level's navigation and map alike.
/// </summary>
public sealed class DoorSlotLinkTests(DoorSlotLinkTests.Fixture fixture)
    : IClassFixture<DoorSlotLinkTests.Fixture>
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The level, north row first: the 2 x 2 room with every slot in the north-west with the
    /// corner room turned a half beside its east wall; the three-slot room
    /// and the two-door room turned a half in the middle row; the corridor
    /// between two corner rooms in the south.
    /// </summary>
    internal static LevelGrid Level(int rotation) => RoomHeightHarness.Level(
        "+, +, ~", $"ring@{rotation}, +, corner@180", "three, twin@180, ~", "corner, corridor, corner");

    /// <summary>
    /// The joints the layout makes with the larger room unturned: the
    /// corridor at its south slots to the corner rooms' south slots; the
    /// three-slot room's south and north slots to the two-door room's two
    /// doors (its east wall, turned to face west, slot for slot reversed),
    /// its middle slot capped; the larger room's north slot of its south-east
    /// cell edge to the turned corner room's east_a, which the half turn put
    /// at the north of its west wall, and its south-west cell's west slot to
    /// the three-slot room's north wall; that room's south wall to the
    /// corner room below.
    /// </summary>
    [Fact]
    public void TheLayoutJoinsTheSameSlots()
    {
        LevelLayout layout = RoomPropHarness.Layout(fixture.Rooms, Level(0));
        Dictionary<string, List<(string, string)>> joints = [];
        foreach (RoomInstance room in layout.Rooms)
        {
            string key = $"{room.Placement.Room}@{room.Placement.CellX},{room.Placement.CellY}";
            joints[key] = [.. room.Joints];
        }

        Assert.Equal([("east_a", "west_a"), ("north_a", "south_a")], joints["corner@0,0"]);
        Assert.Equal([("east_a", "west_a"), ("west_a", "east_a")], joints["corridor@1,0"]);
        Assert.Equal([("west_a", "east_a")], joints["corner@2,0"]);
        Assert.Equal([("east_a", "east_c"), ("east_c", "east_a"), ("north_a", "south0_a"), ("south_a", "north_a")], joints["three@0,1"]);
        Assert.Equal(["east_b"], layout.Rooms.Single(r => r.Placement.Room == "three").Capped);
        Assert.Equal([("east_a", "east_c"), ("east_c", "east_a")], joints["twin@1,1"]);
        Assert.Equal([("east0_c", "east_a"), ("south0_a", "north_a")], joints["ring@0,2"]);
        Assert.Equal([("east_a", "east0_c")], joints["corner@2,2"]);
    }

    /// <summary>
    /// At every turn of the larger room the link and the flattened compile
    /// hold every sample alike, the top tree reaches every node once, every
    /// open leaf is in its nodes' bounds and the map passes the loader
    /// checks; through the two-door room's doors a line runs clear from the
    /// three-slot room at its south and north slots, and is shut at its
    /// capped middle slot, in both maps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task TheLevelLinksAsItFlattens(int rotation)
    {
        LevelGrid level = Level(rotation);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(fixture.Rooms, level);
        BspData flat = await RoomHeightHarness.CompileFlatAsync(fixture.Library, level);
        Assert.True(RoomHeightHarness.SameSpace(linked.Bsp, flat, level) > 0);
        Assert.Null(RoomHeightHarness.BoundsProblem(linked.Bsp));
        Assert.Equal(BspStructView.Count<DNode>(linked.Bsp[BspLump.Nodes]), MultiCellLinkTests.ReachedOnce(linked.Bsp));
        foreach (BspData map in new[] { linked.Bsp, flat })
        {
            LevelProbe probe = new(map);
            Assert.Equal(1f, probe.Trace(new Vec3(128, 256 + 43, 60), new Vec3(384, 256 + 43, 60), (int)BrushContents.Solid));
            Assert.Equal(1f, probe.Trace(new Vec3(128, 256 + 213, 60), new Vec3(384, 256 + 213, 60), (int)BrushContents.Solid));
            Assert.True(probe.Trace(new Vec3(128, 256 + 128, 60), new Vec3(384, 256 + 128, 60), (int)BrushContents.Solid) < 1f);
            Assert.Equal(1f, probe.Trace(new Vec3(128, 43, 60), new Vec3(640, 43, 60), (int)BrushContents.Solid));
        }

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// The level's navigation at every turn of the larger room: each room's
    /// columns are the flattened compile's grid; each joined door names the
    /// door the layout joins it to, which stands across the same edge facing
    /// back, and its door point is on its cell face at its own plug's middle, on the floor; and a
    /// room's nav section keeps its kit's slots and each socket's slot.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task TheLevelsNavigationJoinsTheSameSlots(int rotation)
    {
        LevelGrid level = Level(rotation);
        LevelLayout layout = RoomPropHarness.Layout(fixture.Rooms, level);
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(LevelNavLinker.Link(
            layout, level.Columns, level.Rows, (room, turn) => fixture.Navs[room].Turned(turn), null, Guid.Empty)));
        BspData flat = await RoomHeightHarness.CompileFlatAsync(fixture.Library, level);
        List<(int X, int Y)> cells = [.. layout.Rooms.SelectMany(r => r.Placement.Covered().Select(c => (c.X, c.Y)))];
        RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav, cells);

        Nav3dLevel read = nav.ToLevel();
        int joined = 0;
        foreach (RoomInstance room in layout.Rooms)
        {
            RoomDefinition definition = fixture.Rooms.Get(room.Placement.Room).Definition;
            RoomTransform transform = new(room.Placement, RoomHarness.Cell);
            foreach (RoomSocket socket in definition.Sockets)
            {
                (int X, int Y) cell = room.Placement.SocketCells(definition, socket, RoomHarness.Cell).Cell;
                Nav3dDoor door = read.Doors.Single(d => d.Name == socket.Name && d.Cell == (uint)((cell.Y * level.Columns) + cell.X));
                Box plug = RoomLinter.SealBox(definition, socket, RoomHarness.Cell);
                float midX = (plug.Mins.X + plug.Maxs.X) / 2, midY = (plug.Mins.Y + plug.Maxs.Y) / 2;
                Vec3 middle = transform.Apply(socket.Facing switch
                {
                    RoomFacing.PositiveX => new Vec3(definition.SpanX, midY, 16),
                    RoomFacing.NegativeX => new Vec3(0, midY, 16),
                    RoomFacing.PositiveY => new Vec3(midX, definition.SpanY, 16),
                    _ => new Vec3(midX, 0, 16),
                });
                Nav3dPoi point = read.Pois.Single(p => p.Door >= 0 && read.Doors[p.Door] == door);
                Assert.Equal(middle, point.Position);
                if (room.Joints.FirstOrDefault(j => j.Socket == socket.Name) is { NeighborSocket: { } theirs })
                {
                    Assert.True(door.Joined);
                    Assert.Equal(theirs, read.Doors[door.Other].Name);
                    joined++;
                }
                else
                {
                    Assert.False(door.Joined);
                }
            }
        }

        Assert.True(joined >= 8);
        foreach (RoomNav room in fixture.Navs.Values)
        {
            RoomNav back = RoomNavSection.Read(RoomNavSection.Write(room.Turned(1), NavCompression.None))!;
            Assert.Equal(3, back.Slots);
            Assert.Equal(room.Sockets, back.Sockets);
        }
    }

    /// <summary>
    /// The superset gate over the slot level: every cluster pair the
    /// flattened level's whole compile and vis sees, the link sees through
    /// some pair of its own clusters under them (sampled across each leaf,
    /// <see cref="Spanned"/>); and the corridor's south
    /// slot doors let its west corner room see its east one.
    /// </summary>
    [Fact]
    public async Task TheLinkedPvsIsASupersetOfTheFlattenedCompiles()
    {
        LevelGrid level = Level(0);
        LinkedLevel link = await RoomPropHarness.LinkAsync(fixture.Rooms, level);
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, fixture.Library), await RoomLightHarness.ContextAsync("flat"));
        VisResult monoVis = await RoomHarness.VisAsync(whole.Bsp!, PortalSet.FromPortalFile(whole.Portals!));
        int[][] map = Spanned(whole.Bsp!, link.Bsp, LevelLinkerTests.MapClusters(whole.Bsp!, link.Bsp));
        int pairs = 0;
        for (int from = 0; from < monoVis.ClusterCount; from++)
        {
            for (int to = 0; to < monoVis.ClusterCount; to++)
            {
                if (!monoVis.CanSee(from, to) || map[from].Length == 0 || map[to].Length == 0)
                {
                    continue;
                }

                Assert.True(map[from].Any(a => map[to].Any(b => link.Vis.CanSee(a, b))), $"the flattened compile sees {from} {MultiCellLinkTests.Where(whole.Bsp!, from)} -> {to} {MultiCellLinkTests.Where(whole.Bsp!, to)}; the link sees none of its pairs");
                pairs++;
            }
        }

        Assert.True(pairs > 0);
        int west = RoomHarness.LeafAt(link.Bsp, new Vec3(200, 43, 60)).Cluster;
        int east = RoomHarness.LeafAt(link.Bsp, new Vec3(512 + 56, 43, 60)).Cluster;
        Assert.True(west >= 0 && east >= 0 && west != east && link.Vis.CanSee(west, east));
    }

    /// <summary>
    /// The link's clusters under each of the flattened compile's, sampled
    /// across each leaf every 32 units as well as about its middle: doors at
    /// the same slot in a row of rooms line up, and the flattened compile
    /// makes one leaf of the strip through them, from the first room to the
    /// last, which the gate's samples about the middle see only the middle
    /// room of.
    /// </summary>
    private static int[][] Spanned(BspData monolithic, BspData linked, int[][] map)
    {
        List<HashSet<int>> sets = [.. map.Select(m => new HashSet<int>(m))];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(monolithic[BspLump.Leafs]))
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0)
            {
                continue;
            }

            for (float x = leaf.Mins[0] + 2; x <= leaf.Maxs[0] - 2; x += 32)
            {
                for (float y = leaf.Mins[1] + 2; y <= leaf.Maxs[1] - 2; y += 32)
                {
                    int cluster = LevelLinker.PointInLeafCluster(linked, new Vec3(x, y, (leaf.Mins[2] + leaf.Maxs[2]) / 2));
                    if (cluster >= 0)
                    {
                        _ = sets[leaf.Cluster].Add(cluster);
                    }
                }
            }
        }

        return [.. sets.Select(set => set.Order().ToArray())];
    }

    /// <summary>The level's map at every turn of the larger room is the flattened compile's, its doors at their slots.</summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task TheLevelsMapIsTheFlattenedCompiles(int rotation)
    {
        LevelGrid level = Level(rotation);
        LevelLayout layout = RoomPropHarness.Layout(fixture.Rooms, level);
        LevelMapPlan plan = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, fixture.Rooms.Get);
        Assert.Null(plan.Warning);
        Map2dLevel linked = plan.Build(42);
        BspData flat = await TransitHarness.CompileFlatAsync(fixture.Library, level, mod: false);
        Map2dLevel compiled = LevelMapBuilder.FromCompile(flat, 42, level, [fixture.Library]);
        Assert.Equal(Map2dWriter.Write(linked), Map2dWriter.Write(compiled));
        int twin = layout.Rooms.ToList().FindIndex(r => r.Placement.Room == "twin");
        Assert.Equal(
            [(256 + 19f, 256 + 67f), (256 + 189f, 256 + 237f)],
            linked.Doors.Where(d => d.Placement == twin && d.Open).Select(d => (MathF.Min(d.Y0, d.Y1), MathF.Max(d.Y0, d.Y1))).Order());
    }

    /// <summary>The slot rooms' library, compiled once, and each room's navigation.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        /// <summary>The library VMF.</summary>
        public VmfDocument Library { get; } = RoomHarness.LibraryVmf(Twin, Corridor, Corner, Three, Ring);

        /// <summary>The compiled rooms.</summary>
        public RoomLibrary Rooms { get; private set; } = null!;

        /// <summary>Each room's navigation at turn 0.</summary>
        public Dictionary<string, RoomNav> Navs { get; } = new(StringComparer.Ordinal);

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(Library);
            Rooms = new RoomLibrary(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize);
            foreach (LibraryRoom room in split.Rooms)
            {
                (VmfDocument stripped, IReadOnlyList<AuthoredPoi> pois) = RoomPois.Extract(room.Document);
                RoomObject compiled = await RoomCompiler.CompileAsync(stripped, room.Definition, await RoomPropHarness.ContextAsync(room.Definition.Name));
                Rooms.Add(compiled);
                Navs[room.Definition.Name] = RoomNavBuilder.Build(room.Definition, compiled.Bsp, pois, RoomRole.None, NavSettings.Default);
            }
        }

        /// <inheritdoc/>
        public Task DisposeAsync() => Task.CompletedTask;
    }
}
