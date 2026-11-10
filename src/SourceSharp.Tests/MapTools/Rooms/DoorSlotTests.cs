//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.DoorSlotHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Door slots (the rooms design, 17.8 stage 9): the kit's <c>door_slots</c>
/// places, their spacing rule, the sockets a library finds at them and
/// their names, the kit check, and the joints the layout makes between
/// rooms facing at the same slot.
/// </summary>
public sealed class DoorSlotTests
{
    /// <summary>
    /// Each kit's slots: whole units, the middle slot the one-slot door,
    /// slot k and slot N - 1 - k each other's mirror on the edge, at least
    /// the wall depth from the corner, and at least the wall depth of wall
    /// between neighbours.
    /// </summary>
    [Theory]
    [InlineData(256f, 48f, 16f, 3, new float[] { 19, 104, 189 })]
    [InlineData(512f, 96f, 16f, 3, new float[] { 37, 208, 379 })]
    [InlineData(512f, 48f, 16f, 5, new float[] { 27, 130, 232, 334, 437 })]
    public void SlotsDivideTheEdgeEvenlyAndMirror(float cell, float width, float depth, int slots, float[] expected)
    {
        SocketKit kit = new(width, cell - (2 * depth), depth) { Slots = slots };
        float[] lows = [.. Enumerable.Range(0, slots).Select(k => kit.SlotLow(cell, k))];
        Assert.Equal(expected, lows);
        Assert.Equal(new SocketKit(width, cell - (2 * depth), depth).SlotLow(cell, 0), lows[slots / 2]);
        for (int k = 0; k < slots; k++)
        {
            Assert.Equal(MathF.Round(lows[k]), lows[k]);
            Assert.Equal(cell, lows[k] + lows[slots - 1 - k] + width);
        }

        Assert.True(lows[0] >= depth);
        for (int k = 0; k + 1 < slots; k++)
        {
            Assert.True(lows[k + 1] - lows[k] - width >= depth);
        }

        Assert.Null(kit.SlotProblem(cell));
    }

    /// <summary>A kit of one slot is the kit it always was: equal to one built without slots, printed alike, its door where it stood.</summary>
    [Fact]
    public void OneSlotIsTheKitItAlwaysWas()
    {
        SocketKit plain = new(96f, 224f, 16f);
        SocketKit one = plain with { Slots = 1 };
        Assert.Equal(plain, one);
        Assert.Equal(plain.ToString(), one.ToString());
        Assert.Equal(1, plain.Slots);
        Assert.Equal("SocketKit { Width = 48, Height = 224, Depth = 16, Slots = 3 }", Kit.ToString());
        Assert.NotEqual(Kit, Kit with { Slots = 1 });
    }

    /// <summary>Slots that leave less than a wall depth to the corner are refused with the numbers: the walkable kit's 96 doors three to a 256 edge.</summary>
    [Fact]
    public void SlotsThatDoNotFitAreRefusedWithTheirNumbers()
    {
        SocketKit kit = new(96f, 224f, 16f) { Slots = 3 };
        Assert.Equal(
            "door_slots 3 puts the first door -5 units from the corner of a 256-unit cell edge, less than the wall depth 16;"
            + " 3 doors 96 wide need an edge of at least 3 x (96 + 2 x 16) = 384, or fewer slots.",
            kit.SlotProblem(256f));

        VmfDocument library = RoomHarness.LibraryVmf(RoomHarness.Hub() with { Kit = kit });
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library));
        Assert.Contains("room \"hub\": the door kit SocketKit { Width = 96, Height = 224, Depth = 16, Slots = 3 }: door_slots 3 puts the first door -5 units", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A door_slots that is not 1, 3 or 5 is refused, naming the rule.</summary>
    [Theory]
    [InlineData("2")]
    [InlineData("7")]
    [InlineData("three")]
    public void ADoorSlotsNotOddAndSmallIsRefused(string value)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Twin);
        library.Chunks.First(c => c.GetValue("classname") == RoomLibraryVmf.RoomEntity).Keys.Single(k => k.Name == RoomLibraryVmf.DoorSlotsKey).Value = value;
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library));
        Assert.Equal(
            $"room \"twin\": \"door_slots\" is \"{value}\"; a cell edge has 1, 3 or 5 door slots, an odd number so the middle slot is the centred door and the slots mirror onto each other.",
            refused.Message);
    }

    /// <summary>
    /// A library finds the sockets at any subset of slots, in wall order,
    /// slot by slot from the room's south or west, named by wall and slot
    /// letter, a 2 x 2 room's with its cell edge too; a socket key renames
    /// one; and each socket's plug is at its slot.
    /// </summary>
    [Fact]
    public void ALibraryFindsTheSocketsAtTheirSlotsAndNamesThem()
    {
        RoomDefinition named = Twin with { Sockets = [.. Twin.Sockets.Select(s => s.Name == "east_c" ? s with { Name = "porch" } : s)] };
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomHarness.LibraryVmf(named, Big));
        Assert.Equal(
            [("east_a", RoomFacing.PositiveX, 0, 0), ("porch", RoomFacing.PositiveX, 0, 2), ("west_b", RoomFacing.NegativeX, 0, 1)],
            rooms[0].Definition.Sockets.Select(s => (s.Name, s.Facing, s.Offset, s.Slot)));
        Assert.Equal(
            [("east0_c", RoomFacing.PositiveX, 0, 2), ("west1_a", RoomFacing.NegativeX, 1, 0), ("north0_b", RoomFacing.PositiveY, 0, 1)],
            rooms[1].Definition.Sockets.Select(s => (s.Name, s.Facing, s.Offset, s.Slot)));
        Assert.Equal(Kit, rooms[0].Definition.Kit);

        RoomDefinition twin = rooms[0].Definition;
        Assert.Equal(new Box(new Vec3(240, 19, 16), new Vec3(256, 67, 240)), RoomLinter.SealBox(twin, twin.Sockets[0], 256));
        Assert.Equal(new Box(new Vec3(240, 189, 16), new Vec3(256, 237, 240)), RoomLinter.SealBox(twin, twin.Sockets[1], 256));
        Assert.Equal(new Box(new Vec3(0, 104, 16), new Vec3(16, 152, 240)), RoomLinter.SealBox(twin, twin.Sockets[2], 256));
    }

    /// <summary>A socket key that names no slot is refused, listing the scheme.</summary>
    [Fact]
    public void ASocketKeyWithoutASlotIsRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Twin);
        library.Chunks.First(c => c.GetValue("classname") == RoomLibraryVmf.RoomEntity).AddKey("socket_east", "door");
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library));
        Assert.Equal(
            "room \"twin\" has a key \"socket_east\"; a room under a kit of 3 door slots names a door slot by socket_<wall>_<slot>, like socket_east_a to socket_east_c, the slot a to c from the room's own south or west.",
            refused.Message);
    }

    /// <summary>Rooms of one slot and of three in one library are refused together: one library, one kit.</summary>
    [Fact]
    public void RoomsOfOneSlotAndOfThreeAreRefusedTogether()
    {
        VmfDocument library = RoomHarness.LibraryVmf(RoomHarness.Hub() with { Kit = Kit with { Slots = 1 } }, Twin);
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library));
        Assert.Equal(
            "room \"twin\" is built for cell 256 and kit SocketKit { Width = 48, Height = 224, Depth = 16, Slots = 3 },"
            + " but room \"hub\" for cell 256 and kit SocketKit { Width = 48, Height = 224, Depth = 16 }; every room of a library shares one grid and one door kit.",
            refused.Message);
    }

    /// <summary>
    /// The three-slot room's east wall against the corner room turned each
    /// way: the joint is the one pair whose doors stand at the same place,
    /// slot k meeting slot k or slot 2 - k as the turn has it, every other
    /// slot capped, and the two plugs coincide in the world.
    /// </summary>
    [Theory]
    [InlineData(0, "east_a", "west_a")]
    [InlineData(90, "east_a", "north_a")]
    [InlineData(180, "east_c", "east_a")]
    [InlineData(270, "east_c", "south_a")]
    public void AJointIsTheSameSlotSeenFromBothSides(int rotation, string ours, string theirs)
    {
        RoomDefinition three = Three, corner = Corner;
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", $"three, corner@{rotation}"), "slots");
        LevelLayout layout = level.ToLayout(name => name == "three" ? three : corner, RoomHarness.Cell, Kit);
        RoomInstance a = layout.Rooms[0], b = layout.Rooms[1];
        Assert.Equal([(ours, theirs)], a.Joints);
        Assert.Equal(["east_a", "east_b", "east_c"], a.Joints.Select(j => j.Socket).Concat(a.Capped).Order());
        Assert.Contains((theirs, ours), b.Joints);
        Box mine = Transformed(a.Placement, three, ours), other = Transformed(b.Placement, corner, theirs);
        Assert.Equal(mine.Mins.Y, other.Mins.Y);
        Assert.Equal(mine.Maxs.Y, other.Maxs.Y);
        Assert.Equal(mine.Maxs.X, other.Mins.X);
    }

    private static Box Transformed(RoomPlacement placement, RoomDefinition definition, string socket)
    {
        RoomTransform transform = new(placement, RoomHarness.Cell);
        Box plug = RoomLinter.SealBox(definition, definition.Sockets.Single(s => s.Name == socket), RoomHarness.Cell);
        Vec3 a = transform.Apply(plug.Mins), b = transform.Apply(plug.Maxs);
        return new Box(new Vec3(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z)), new Vec3(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z)));
    }
}
