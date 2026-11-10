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
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.DoorSlotHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The per-socket features on door slots (the rooms design, 17.8 stage 9):
/// a water socket at a slot, socket furniture and <c>room_needs
/// joined_&lt;socket&gt;</c> on slot sockets, a transition level of slot
/// rooms, and a kit of five slots through room, link and flatten. Each
/// held to the flattened level's compile.
/// </summary>
public sealed class DoorSlotFeatureTests
{
    /// <summary>Two doors on the east wall, at the south and middle slots.</summary>
    private static RoomDefinition Pa => Room("pa", (RoomFacing.PositiveX, 0), (RoomFacing.PositiveX, 1));

    /// <summary>Two doors on the east wall, at the middle and north slots: turned a half beside <see cref="Pa"/>, they meet its doors.</summary>
    private static RoomDefinition Pb => Room("pb", (RoomFacing.PositiveX, 1), (RoomFacing.PositiveX, 2));

    private static Vec3 Corner(int room) => new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);

    private static Box Plug(RoomDefinition room, string socket) =>
        RoomLinter.SealBox(room, room.Sockets.Single(s => s.Name == socket), RoomHarness.Cell);

    /// <summary>
    /// Water through the south slot (<c>water_east_a</c> on one side,
    /// <c>water_east_c</c> on the other, the neighbour turned a half): the
    /// link and the flattened compile hold the same space, water records
    /// and fluid, and the doorway's water is one record with the rooms'.
    /// </summary>
    [Fact]
    public async Task WaterThroughASlotDoorLinksAsItFlattens()
    {
        RoomDefinition a = Room("pa", (RoomFacing.PositiveX, 0)), b = Room("pb", (RoomFacing.PositiveX, 2));
        VmfDocument library = RoomHarness.LibraryVmf(a, b);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        world.Children.Add(VmfPlacement.MoveSolid(RoomWaterHarness.Water(new Box(new Vec3(144, 16, 16), new Vec3(240, 96, 64))), QuarterTurn.Translation(Corner(0))));
        world.Children.Add(VmfPlacement.MoveSolid(RoomWaterHarness.Water(new Box(new Vec3(144, 160, 16), new Vec3(240, 240, 64)), RoomWaterHarness.WaterBrush + 1), QuarterTurn.Translation(Corner(1))));
        RoomWaterHarness.Declare(library, "pa", "east_a", $"64 {RoomWaterHarness.CheapWater}");
        RoomWaterHarness.Declare(library, "pb", "east_c", $"64 {RoomWaterHarness.CheapWater}");
        RoomLibrary rooms = await RoomWaterHarness.CompileSocketsAsync(library);
        Assert.NotNull(rooms.Get("pa").WaterOfCompile);

        LevelGrid level = RoomHeightHarness.Level("pa, pb@180");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await RoomWaterHarness.CompileFlatAsync(library, level);
        Assert.Equal(LevelLinkerWaterTests.Points(flat, 2, 1), LevelLinkerWaterTests.Points(linked.Bsp, 2, 1));
        Assert.Equal(["64 16 unit/water_cheap"], LevelLinkerWaterTests.Records(linked.Bsp));
        Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked.Bsp));
        Assert.Equal(LevelLinkerWaterSocketTests.FluidSummary(flat), LevelLinkerWaterSocketTests.FluidSummary(linked.Bsp));
        Assert.Equal((int)BrushContents.Water, RoomHarness.LeafAt(linked.Bsp, new Vec3(256, 43, 40)).Contents & (int)BrushContents.Water);
    }

    /// <summary>
    /// Socket furniture at slot doors and <c>room_needs joined_&lt;socket&gt;</c>
    /// on a slot socket: of the two doors in the south slot's doorway the one
    /// of higher priority is kept; a brush that needs <c>joined_east_b</c>
    /// is kept, one that needs <c>!joined_east_a</c> is dropped, as the
    /// flattened compile has them; alone, every slot capped, the furniture
    /// and the joined brush go and the unjoined one stays.
    /// </summary>
    [Fact]
    public async Task FurnitureAndJoinedConditionsFollowTheSlotSockets()
    {
        RoomDefinition a = Pa, b = Pb;
        Box mine = Plug(a, "east_a"), theirs = Plug(b, "east_c");
        VmfDocument library = RoomHarness.LibraryVmf(a, b);
        foreach ((int room, VmfChunk entity) in new (int, VmfChunk)[]
        {
            (0, RoomBrushHarness.Door(700, mine.Mins, mine.Maxs, ("targetname", "pa_door"), (RoomStaticProps.SocketKey, "east_a"))),
            (1, RoomBrushHarness.Door(710, theirs.Mins, theirs.Maxs, ("targetname", "pb_door"), (RoomStaticProps.SocketKey, "east_c"), (RoomStaticProps.PriorityKey, "5"))),
            (0, RoomBrushHarness.Door(720, new Vec3(60, 140, 16), new Vec3(92, 172, 64), ("targetname", "when_b"), (RoomNeeds.Key, "joined_east_b"))),
            (0, RoomBrushHarness.Door(730, new Vec3(60, 60, 16), new Vec3(92, 92, 64), ("targetname", "unless_a"), (RoomNeeds.Key, "!joined_east_a"))),
        })
        {
            library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(Corner(room))));
        }

        RoomLibrary rooms = await RoomBrushHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("pa, pb@180");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        Assert.Equal(["pb_door", "when_b"], RoomBrushHarness.BrushEntities(linked.Bsp).Select(p => p.Entity.Get("targetname")!).Order());
        Assert.Equal(RoomBrushHarness.Observed(await RoomBrushHarness.CompileFlatAsync(library, level)), RoomBrushHarness.Observed(linked.Bsp));

        LinkedLevel alone = await RoomPropHarness.LinkAsync(rooms, RoomHeightHarness.Level("pa"));
        Assert.Equal(["unless_a"], RoomBrushHarness.BrushEntities(alone.Bsp).Select(p => p.Entity.Get("targetname")!));
    }

    /// <summary>
    /// A transition level of slot rooms (up, plain, down, joined at their
    /// south slots): the link and the flattened compile carry the same
    /// entities and models in both modes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATransitionLevelOfSlotRoomsLinksAsItFlattens(bool mod)
    {
        RoomDefinition up = Room("up", (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0));
        RoomDefinition down = Room("down", (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0));
        RoomDefinition plain = Room("plain", (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0));
        VmfDocument library = RoomHarness.LibraryVmf(up, down, plain);
        VmfChunk[][] entities = [TransitHarness.UpEntities, TransitHarness.DownEntities, TransitHarness.PlainEntities];
        for (int room = 0; room < entities.Length; room++)
        {
            foreach (VmfChunk entity in entities[room])
            {
                library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(Corner(room))));
            }
        }

        TransitHarness.Marker(library, "up").AddKey(RoomPois.RoleKey, "up");
        TransitHarness.Marker(library, "down").AddKey(RoomPois.RoleKey, "down");
        RoomLibrary rooms = await TransitHarness.CompileAsync(library);
        LevelGrid level = TransitHarness.Level("mid", "up_map: above\ndown_map: below\n", "up, plain, down");
        LinkedLevel linked = await TransitHarness.LinkAsync(rooms, level, mod);
        BspData whole = await TransitHarness.CompileFlatAsync(library, level, mod);
        Assert.True(linked.HasTransitions);
        Assert.Equal(TransitHarness.Comparable(whole), TransitHarness.Comparable(linked.Bsp));
        Assert.Equal(TransitHarness.Models(whole), TransitHarness.Models(linked.Bsp));
    }

    /// <summary>
    /// A kit of five slots (doors 34 wide, walls 8 deep, at 9, 60, 111, 162
    /// and 213 along a 256 edge): a room with doors at the first, middle and
    /// last slots beside itself turned a half joins slot a to e, c to c and
    /// e to a; the link holds every sample as the flattened compile does,
    /// and a line runs through each of the three doors in both maps.
    /// </summary>
    [Fact]
    public async Task AFiveSlotKitLinksAsItFlattens()
    {
        SocketKit kit = new(34f, 240f, 8f) { Slots = 5 };
        Assert.Equal([9f, 60f, 111f, 162f, 213f], Enumerable.Range(0, 5).Select(k => kit.SlotLow(256, k)));
        RoomDefinition five = new("five", RoomHarness.Cell, kit,
            [.. ((int[])[0, 2, 4]).Select(k => new RoomSocket(RoomFacing.PositiveX, RoomLibraryVmf.DefaultSocketName(false, RoomFacing.PositiveX, 0, 5, k)) { Slot = k })]);
        VmfDocument library = RoomHarness.LibraryVmf(five);
        Assert.Equal(["east_a", "east_c", "east_e"], RoomLibraryVmf.Split(library)[0].Definition.Sockets.Select(s => s.Name));
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level("five, five@180");
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Assert.Equal([("east_a", "east_e"), ("east_c", "east_c"), ("east_e", "east_a")], layout.Rooms[0].Joints);

        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await RoomHeightHarness.CompileFlatAsync(library, level);
        Assert.True(RoomHeightHarness.SameSpace(linked.Bsp, flat, level) > 0);
        Assert.Equal(BspStructView.Count<DNode>(linked.Bsp[BspLump.Nodes]), MultiCellLinkTests.ReachedOnce(linked.Bsp));
        foreach (BspData map in new[] { linked.Bsp, flat })
        {
            LevelProbe probe = new(map);
            foreach (float y in (float[])[26, 128, 230])
            {
                Assert.Equal(1f, probe.Trace(new Vec3(128, y, 60), new Vec3(384, y, 60), (int)BrushContents.Solid));
            }

            Assert.True(probe.Trace(new Vec3(128, 77, 60), new Vec3(384, 77, 60), (int)BrushContents.Solid) < 1f);
        }
    }
}
