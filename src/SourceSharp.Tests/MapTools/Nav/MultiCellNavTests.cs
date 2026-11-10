//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.MultiCellHarness;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// Navigation of rooms larger than one cell (the rooms design, 17.8 and
/// 17.11): one clearance grid over the footprint, door points at the middle
/// of each socket's cell edge, what capping each door changes, points of
/// interest anywhere in the footprint; the four stored turns of a footprint
/// that is not square swap its sides; and in the level's file the room's
/// columns are written per covered cell, the same columns the flattened
/// level's compile gives.
/// </summary>
public sealed class MultiCellNavTests
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static RoomDefinition Court => Large(
        "court", 3, 3, (RoomFacing.PositiveX, 1), (RoomFacing.NegativeX, 1), (RoomFacing.PositiveY, 1), (RoomFacing.NegativeY, 1));

    /// <summary>The library of the hall, the gallery, the court and the hub, with a marker in the gallery's north cell.</summary>
    private static VmfDocument Library()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall, Gallery, Court, Hub);
        float gallery = 512 + RoomHarness.LibraryGap;
        VmfChunk poi = RoomPropHarness.Entity(RoomPois.Entity, 777, new Vec3(gallery + 128, 256 + 128, 16), ("poi_type", "landmark"), ("targetname", "cxry_lookout"));
        library.Chunks.Add(poi);
        return library;
    }

    private static async Task<(RoomLibrary Rooms, Dictionary<string, RoomNav> Navs)> CompileAsync(VmfDocument library)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary rooms = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize);
        Dictionary<string, RoomNav> navs = new(StringComparer.Ordinal);
        foreach (LibraryRoom room in split.Rooms)
        {
            (VmfDocument stripped, IReadOnlyList<AuthoredPoi> pois) = RoomPois.Extract(room.Document);
            RoomObject compiled = await RoomCompiler.CompileAsync(stripped, room.Definition, await RoomPropHarness.ContextAsync(room.Definition.Name));
            rooms.Add(compiled);
            navs[room.Definition.Name] = RoomNavBuilder.Build(room.Definition, compiled.Bsp, pois, RoomRole.None, NavSettings.Default);
        }

        return (rooms, navs);
    }

    private static Nav3dReader Link(RoomLibrary rooms, Dictionary<string, RoomNav> navs, LevelGrid level) =>
        Nav3dReader.Open(Nav3dWriter.Write(LevelNavLinker.Link(
            RoomPropHarness.Layout(rooms, level), level.Columns, level.Rows, (room, turn) => navs[room].Turned(turn), null, Guid.Empty)));

    /// <summary>
    /// A larger room's grid covers its footprint, n voxels to a cell; its
    /// door points stand at the middle of each socket's cell edge on the
    /// doorway's floor; its four turns swap a footprint's sides, keep the
    /// records, put each turned door point where the placement's transform
    /// puts the room's, and come back to the room after four; and the
    /// section stores and reads it, sockets' cell edges included.
    /// </summary>
    [Fact]
    public async Task ALargerRoomsGridCoversItsFootprintAtEveryTurn()
    {
        (RoomLibrary rooms, Dictionary<string, RoomNav> navs) = await CompileAsync(Library());
        RoomNav gallery = navs["gallery"];
        int n = gallery.CellVoxels;
        Assert.Equal((1, 2, n, 2 * n), (gallery.CellsX, gallery.CellsY, gallery.Columns.SizeX, gallery.Columns.SizeY));
        RoomDefinition definition = rooms.Get("gallery").Definition;
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            Box plug = RoomLinter.SealBox(definition, definition.Sockets[s], 256);
            Vec3 door = gallery.SocketData[s].DoorPoint;
            Assert.Equal(16f, door.Z);
            Assert.True(door.X == (plug.Mins.X + plug.Maxs.X) / 2 || door.Y == (plug.Mins.Y + plug.Maxs.Y) / 2);
        }

        Assert.Equal(new Vec3(256, 256 + 128, 16), gallery.SocketData[0].DoorPoint); // east1
        for (int turn = 0; turn < 4; turn++)
        {
            RoomNav turned = gallery.Turned(turn);
            Assert.Equal((turn & 1) == 0 ? (1, 2) : (2, 1), (turned.CellsX, turned.CellsY));
            Assert.Equal(gallery.Records, turned.Records);
            RoomTransform transform = new(new RoomPlacement("gallery", 0, 0, turn) { CellsX = 1, CellsY = 2 }, 256);
            for (int s = 0; s < definition.Sockets.Count; s++)
            {
                Assert.Equal(transform.Apply(gallery.SocketData[s].DoorPoint), turned.SocketData[s].DoorPoint);
            }

            RoomNav read = RoomNavSection.Read(RoomNavSection.Write(turned, NavCompression.None))!;
            Assert.Equal((turned.CellsX, turned.CellsY), (read.CellsX, read.CellsY));
            Assert.Equal(definition.Sockets, read.Sockets);
            Assert.Equal(turned.Columns.Runs, read.Columns.Runs);
        }

        RoomNav back = gallery.Turned(1).Turned(1).Turned(1).Turned(1);
        Assert.Equal(gallery.Columns.Runs, back.Columns.Runs);
        Assert.Equal(gallery.Columns.ColumnStarts, back.Columns.ColumnStarts);
    }

    /// <summary>
    /// A path through the 3 x 3 court crosses its internal cell faces with
    /// no seam: in the level's file, every place a player stands in the
    /// court's nine cells on its floor is one walkable component, around the
    /// low wall that crosses a face, and the wall's top is one place across
    /// the two cells it spans; and the point of interest in the gallery's
    /// north cell stands in that cell.
    /// </summary>
    [Fact]
    public async Task WalkingCrossesALargerRoomsInternalFaces()
    {
        VmfDocument library = Library();
        library.GetChunk("world")!.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(896 + 224, 448, 16), new Vec3(896 + 320, 464, 80), 6000));
        (RoomLibrary rooms, Dictionary<string, RoomNav> navs) = await CompileAsync(library);
        Nav3dReader nav = Link(rooms, navs, RoomHeightHarness.Level("+, +, +, ~", "+, +, +, +", "court, +, +, gallery"));
        (int[] component, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player);
        HashSet<int> floors = [], tops = [];
        HashSet<int> topCells = [];
        for (int l = 0; l < nav.LeafCount; l++)
        {
            (int cell, _, _) = nav.LeafColumn(l);
            if (cell % 4 > 2 || cell >= 12 || !Nav3dWalk.IsStand(nav, Nav3dWalker.Player, l))
            {
                continue;
            }

            if (nav.LeafBounds(l).Mins.Z == 16)
            {
                floors.Add(component[l]);
            }
            else if (nav.LeafBounds(l).Mins.Z == 80)
            {
                tops.Add(component[l]);
                topCells.Add(cell);
            }
        }

        // The floor of all nine cells is one walk, round the wall; the wall's
        // top, too high to climb, is one place of its own across two cells.
        Assert.Single(floors);
        Assert.Single(tops);
        Assert.Equal(2, topCells.Count);
        Nav3dPoi lookout = nav.ToLevel().Pois.Single(p => p.Name == "c3r0_lookout");
        Assert.Equal((uint)((1 * 4) + 3), lookout.Cell);
    }

    /// <summary>
    /// Door records and points per socket at every turn of the hall beside
    /// hubs: each door stands in the cell whose edge it is on, faces out of
    /// it, and a joined one names the hub's door across that edge; each
    /// cell's door bits are its own doors'.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task DoorsStandInTheirOwnCellsAtEveryTurn(int rotation)
    {
        (RoomLibrary rooms, Dictionary<string, RoomNav> navs) = await CompileAsync(Library());
        LevelGrid level = RoomHeightHarness.Level("hub, hub, hub, hub", "hub, +, +, hub", $"hub, hall@{rotation}, +, hub", "hub, hub, hub, hub");
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Nav3dReader nav = Link(rooms, navs, level);
        Nav3dLevel read = nav.ToLevel();
        RoomPlacement hall = layout.Rooms.Single(r => r.Placement.Room == "hall").Placement;
        RoomDefinition definition = rooms.Get("hall").Definition;
        foreach (RoomSocket socket in definition.Sockets)
        {
            ((int X, int Y) cell, (int X, int Y) across) = hall.SocketCells(definition, socket, 256);
            Nav3dDoor door = read.Doors.Single(d => d.Name == socket.Name && d.Cell == (uint)((cell.Y * 4) + cell.X));
            Assert.True(door.Joined);
            Nav3dDoor other = read.Doors[door.Other];
            Assert.Equal((uint)((across.Y * 4) + across.X), other.Cell);
            Assert.Equal((door.Direction + 2) % 4, other.Direction);
            Assert.NotEqual(0, read.Cells[(int)door.Cell].Joined & (1 << door.Direction));
            Nav3dPoi point = read.Pois.Single(p => p.Door >= 0 && read.Doors[p.Door] == door);
            Assert.Equal(door.Cell, point.Cell);
        }

        Assert.Equal(4, hall.Covered().Count(c => read.Cells[(c.Y * 4) + c.X].Room == "hall"));
    }

    /// <summary>
    /// The hall's columns in the level's file, every cell it covers and
    /// every turn, and the hubs' beside it, are the grid built straight from
    /// the flattened level's whole compile, run for run and record for
    /// record; with a block to climb in the hall the level has jump links.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task TheCoveredCellsAreTheFlattenedCompilesGrid(int rotation)
    {
        VmfDocument library = Library();
        library.GetChunk("world")!.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(100, 300, 16), new Vec3(180, 380, 64), 6001));
        (RoomLibrary rooms, Dictionary<string, RoomNav> navs) = await CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("+, +, hub", $"hall@{rotation}, +, hub");
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Nav3dReader nav = Link(rooms, navs, level);
        BspData flat = await RoomHeightHarness.CompileFlatAsync(library, level);
        List<(int X, int Y)> cells = [.. layout.Rooms.SelectMany(r => r.Placement.Covered().Select(c => (c.X, c.Y)))];
        RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav, cells);
        Assert.NotEmpty(nav.ToLevel().Jumps);
    }
}
