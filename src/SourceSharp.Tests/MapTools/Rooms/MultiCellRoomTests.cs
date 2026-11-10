//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.MultiCellHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms larger than one cell (the rooms design, 17.8): the
/// <c>room_footprint</c> key, the footprint box, sockets per cell edge of
/// the perimeter with their default names and plug boxes, and the compile
/// that splits at every cell face with one subtree per cell.
/// </summary>
public sealed class MultiCellRoomTests
{
    // ---- the model ----------------------------------------------------------------------------

    /// <summary>
    /// A larger room's box is its footprint; each face has one cell edge per
    /// cell along it; a socket stands in the cell whose edge it is on, its
    /// plug a one-cell room's moved along the face by whole cells.
    /// </summary>
    [Fact]
    public void ASocketIsAtOneCellEdgeOfThePerimeter()
    {
        RoomDefinition hall = Hall;
        hall.Validate();
        Assert.True(hall.IsLarge);
        Assert.True(hall.IsShaped);
        Assert.False(hall.HasOwnHeight);
        Assert.Equal(new Box(Vec3.Zero, new Vec3(512, 512, 256)), hall.Bounds);
        Assert.Equal(2, hall.EdgesOn(RoomFacing.PositiveX));
        Assert.Equal(["east0", "west1", "north0", "south1"], hall.Sockets.Select(s => s.Name));
        Assert.Equal([(1, 0), (0, 1), (0, 1), (1, 0)], hall.Sockets.Select(hall.CellOf));

        RoomDefinition cube = RoomHarness.WalkableRoom("cube", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        Box[] one = [.. cube.Sockets.Select(s => RoomLinter.SealBox(cube, s, 256))];
        Assert.Equal(one[0].Mins + new Vec3(256, 0, 0), RoomLinter.SealBox(hall, hall.Sockets[0], 256).Mins);
        Assert.Equal(one[1].Mins + new Vec3(0, 256, 0), RoomLinter.SealBox(hall, hall.Sockets[1], 256).Mins);
        Assert.Equal(one[2].Maxs + new Vec3(0, 256, 0), RoomLinter.SealBox(hall, hall.Sockets[2], 256).Maxs);
        Assert.Equal(one[3].Maxs + new Vec3(256, 0, 0), RoomLinter.SealBox(hall, hall.Sockets[3], 256).Maxs);
    }

    /// <summary>A footprint or socket a room cannot have is refused by the definition.</summary>
    [Fact]
    public void ADefinitionOutOfShapeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Hall with { CellsX = 17 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Hall with { CellsY = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Large("x", 2, 1, (RoomFacing.PositiveX, 1)).Validate());
        ArgumentException twice = Assert.Throws<ArgumentException>(() => (Hall with { Sockets = [.. Hall.Sockets, new RoomSocket(RoomFacing.PositiveX, "again")] }).Validate());
        Assert.StartsWith("Two sockets at one cell edge: PositiveX at cell 0.", twice.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomSocket(RoomFacing.PositiveX, "x") { Offset = -1 }.Validate());
        Assert.Equal("RoomSocket { Facing = PositiveX, Name = east }", new RoomSocket(RoomFacing.PositiveX, "east").ToString());
        Assert.Equal("RoomSocket { Facing = PositiveX, Name = east1, Offset = 1 }", new RoomSocket(RoomFacing.PositiveX, "east1") { Offset = 1 }.ToString());
    }

    // ---- the library --------------------------------------------------------------------------

    /// <summary>
    /// <c>room_footprint</c> splits the room over its footprint: its box, its
    /// sockets found per cell edge in face then offset order with their
    /// default names, a renamed edge, and the room beside it placed after
    /// the footprint.
    /// </summary>
    [Fact]
    public void ALibrarySplitsALargerRoom()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall, Hub, Gallery);
        VmfChunk marker = library.GetChunks("entity").Where(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity).First();
        marker.AddKey("socket_west1", "back");

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal(["hall", "hub", "gallery"], rooms.Select(r => r.Definition.Name));
        RoomDefinition hall = rooms[0].Definition;
        Assert.Equal((2, 2), (hall.CellsX, hall.CellsY));
        Assert.Equal(["east0", "back", "north0", "south1"], hall.Sockets.Select(s => s.Name));
        Assert.Equal([0, 1, 0, 1], hall.Sockets.Select(s => s.Offset));
        Assert.Equal(new Vec3(512 + RoomHarness.LibraryGap, 0, 0), rooms[1].Corner);
        Assert.False(rooms[1].Definition.IsLarge);
        Assert.Equal((1, 2), (rooms[2].Definition.CellsX, rooms[2].Definition.CellsY));
        Assert.Equal(["east1", "north0", "south0"], rooms[2].Definition.Sockets.Select(s => s.Name));
        foreach (VmfChunk solid in rooms[0].Document.GetChunk("world")!.GetChunks("solid"))
        {
            Assert.True(VmfPlacement.Bounds(solid).ContainsWithin(hall.Bounds, 0));
        }
    }

    /// <summary>A footprint that is not one, and socket keys a larger room does not have, are refused with their texts.</summary>
    [Theory]
    [InlineData("room_footprint", "2", "room hall: room_footprint \"2\" is not two whole numbers of cells from 1 to 16, like \"2 3\".")]
    [InlineData("room_footprint", "2 17", "room hall: room_footprint \"2 17\" is not two whole numbers of cells from 1 to 16, like \"2 3\".")]
    [InlineData("room_footprint", "0 2", "room hall: room_footprint \"0 2\" is not two whole numbers of cells from 1 to 16, like \"2 3\".")]
    [InlineData("room_footprint", "2.5 2", "room hall: room_footprint \"2.5 2\" is not two whole numbers of cells from 1 to 16, like \"2 3\".")]
    [InlineData("socket_east", "door", "room \"hall\" has a key \"socket_east\"; a room of 2 x 2 cells names a cell edge of its perimeter by socket_east0 to socket_east1, socket_west0 to socket_west1, socket_north0 to socket_north1 or socket_south0 to socket_south1.")]
    [InlineData("socket_east2", "door", "room \"hall\" has a key \"socket_east2\"; a room of 2 x 2 cells names a cell edge of its perimeter by socket_east0 to socket_east1, socket_west0 to socket_west1, socket_north0 to socket_north1 or socket_south0 to socket_south1.")]
    [InlineData("socket_east1", "door", "room \"hall\" names its east1 socket \"door\" (socket_east1), but its east1 cell edge has no door plug: no world brush fills (496 336 16)-(512 432 240).")]
    [InlineData("water_west", "48 nature/water", "room \"hall\" has a key \"water_west\"; a room of 2 x 2 cells names a cell edge of its perimeter by water_east0 to water_east1, water_west0 to water_west1, water_north0 to water_north1 or water_south0 to water_south1.")]
    public void ALargerRoomsKeysAreHeldToItsFootprint(string key, string value, string message)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall);
        VmfChunk marker = library.GetChunks("entity").Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity);
        foreach (VmfKey existing in marker.Keys.Where(k => k.Name == key).ToList())
        {
            marker.Children.Remove(existing);
        }

        marker.AddKey(key, value);
        Assert.Equal(message, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library)).Message);
    }

    // ---- the compile --------------------------------------------------------------------------

    /// <summary>
    /// A larger room compiles on cell-sized blocks: its tree splits at every
    /// internal cell face before anything else, so each covered cell has its
    /// own subtree, every point of a cell descends from the head through the
    /// block nodes into that cell's root, and no block node owns a face.
    /// </summary>
    [Fact]
    public async Task ALargerRoomsTreeHasASubtreePerCell()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(Hall));
        RoomObject hall = rooms.Get("hall");
        RoomCellTree tree = Assert.IsType<RoomCellTree>(hall.CellTree);
        Assert.Equal(4, tree.Roots.Count);
        Assert.Equal(4, tree.Roots.Distinct().Count());
        Assert.All(tree.Roots, r => Assert.True(r >= 0));
        Assert.NotEmpty(tree.BlockNodes);

        DNode[] nodes = BspStructView.As<DNode>(hall.Bsp[BspLump.Nodes]).ToArray();
        DPlane[] planes = BspStructView.As<DPlane>(hall.Bsp[BspLump.Planes]).ToArray();
        Assert.All(tree.BlockNodes, n => Assert.Equal(0, nodes[n].NumFaces));
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                // A point well inside the cell walks through block nodes only until it reaches the cell's root.
                Vec3 point = new((x * 256) + 100, (y * 256) + 120, 60);
                int child = 0;
                while (child >= 0 && tree.IsOmitted(child))
                {
                    DPlane plane = planes[nodes[child].PlaneNum];
                    child = nodes[child].Children[Vec3.Dot(plane.Normal, point) - plane.Dist >= 0 ? 0 : 1];
                }

                Assert.Equal(tree.RootOf(x, y), child);
            }
        }
    }

    /// <summary>
    /// A room of one cell compiles on stock's blocks and has no cell tree;
    /// its tree is the one it always had.
    /// </summary>
    [Fact]
    public async Task ARoomOfOneCellHasNoCellTree()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(Hub));
        Assert.Null(rooms.Get("hub").CellTree);
        Assert.Null(RoomCellTree.Derive(rooms.Get("hub").Definition, rooms.Get("hub").Bsp));
    }

    /// <summary>
    /// A brush side lying exactly on an internal cell face, with open space
    /// in front of it, would be a face of a node the link leaves out, so the
    /// compile refuses it naming the plane; the same block moved off the
    /// face compiles.
    /// </summary>
    [Fact]
    public async Task ASurfaceOnAnInternalCellFaceIsRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hall);
        library.GetChunk("world")!.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(256, 100, 16), new Vec3(288, 140, 64), 5000));
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => RoomPropHarness.CompileAsync(library));
        Assert.Equal(
            "room hall: a surface lies on the cell face x = 256 inside its 2 x 2 footprint; the link routes each cell to its own part of the room"
            + " and would drop it. Move the surface off the cell face, or let its brush cross it.",
            refused.Message);

        VmfDocument crossing = RoomHarness.LibraryVmf(Hall);
        crossing.GetChunk("world")!.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(240, 100, 16), new Vec3(288, 140, 64), 5000));
        Assert.NotNull((await RoomPropHarness.CompileAsync(crossing)).Get("hall").CellTree);
    }

    // ---- the pack -----------------------------------------------------------------------------

    /// <summary>
    /// A pack holding a larger room is version 6, its room carrying a
    /// revision 2 <c>SHAP</c> section; read back, the room has its
    /// footprint, its sockets' cell edges and the cell tree its compile has.
    /// A pack of one-cell rooms keeps version 4.
    /// </summary>
    [Fact]
    public async Task APackWithALargerRoomIsVersionSix()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(Hall, Hub));
        byte[] large = await PackAsync(rooms.Get("hall"), rooms.Get("hub"));
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(large.AsSpan(8)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian((await PackAsync(rooms.Get("hub"))).AsSpan(8)));

        using MemoryStream stream = new(large);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(stream, index, [new RoomPackRequest("hall", [0])]);
        RoomDefinition hall = loaded[0].Definition;
        Assert.Equal((2, 2), (hall.CellsX, hall.CellsY));
        Assert.Equal(rooms.Get("hall").Definition.Sockets, hall.Sockets);
        Assert.Equal(rooms.Get("hall").CellTree!.Roots, loaded[0].CellTree!.Roots);
        Assert.Equal(rooms.Get("hall").CellTree!.BlockNodes, loaded[0].CellTree!.BlockNodes);
    }

    /// <summary>
    /// A larger room's shape in a pack whose version says it holds none is
    /// damage, refused naming the room; a build that reads only version 5
    /// refuses a version 6 pack by its version check.
    /// </summary>
    [Fact]
    public async Task ALargerRoomInAnOlderPackIsRefused()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(Hall));
        byte[] pack = await PackAsync(rooms.Get("hall"));
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), 5);
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.LoadRoomsAsync(stream, index, [new RoomPackRequest("hall", [0])]));
        Assert.Equal(
            "room pack entry \"hall\" covers several cells in a version 5 pack; only a version 6 pack holds rooms larger than one cell,"
            + " so the pack is damaged; recompile the library with ssmap room.",
            refused.Message);
        Assert.Equal("room pack version 6; this build reads version 5.", Assert.Throws<LinkException>(() => RoomPack.CheckVersion(6, 5)).Message);
    }

    private static async Task<byte[]> PackAsync(params RoomObject[] rooms)
    {
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        using MemoryStream stream = new();
        await RoomPack.SaveAsync(items, stream);
        return stream.ToArray();
    }
}
