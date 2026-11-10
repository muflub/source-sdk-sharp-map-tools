//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A door that stands on the floor at any height: the kit's
/// <c>door_sill floor</c> (<see cref="DoorSill.Floor"/>). Its opening runs
/// from the wall depth up by the door's height, so a kit may have a door
/// lower than the room's interior and rooms lower than their cell; a kit
/// without the key is the centred kit it always was, to the bit; the sill
/// is part of the kit, compared between rooms and libraries, and recorded
/// in the room's container.
/// </summary>
public sealed class DoorSillTests
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>The floor kit the link facts use: a 96 x 128 door on the harness's 256 cell and 16 wall.</summary>
    private static SocketKit FloorKit { get; } = new(96f, 128f, 16f) { Sill = DoorSill.Floor };

    /// <summary>The lowest room the floor kit allows: the door and the two slabs.</summary>
    private const float Low = 160;

    private static RoomDefinition FloorRoom(string name, float? height = null) =>
        new RoomDefinition(name, RoomHarness.Cell, FloorKit,
            [.. new[] { RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY }
                .Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))])
        { Height = height ?? RoomHarness.Cell };

    // ---- the kit ------------------------------------------------------------------------------

    /// <summary>
    /// A centred kit's opening in units is its unit rectangle times the cell,
    /// the product every plug box was always built from; a floor kit's runs
    /// from the wall depth up by the door, exactly.
    /// </summary>
    [Theory]
    [InlineData(96f, 224f, 16f, 256f)]
    [InlineData(96f, 96f, 8f, 256f)]
    [InlineData(256f, 960f, 32f, 1024f)]
    [InlineData(64f, 100.3f, 12.7f, 333f)]
    public void TheOpeningStandsAsTheSillSays(float width, float height, float depth, float cell)
    {
        SocketKit centred = new(width, height, depth);
        (float u0, float v0, float u1, float v1) = centred.OpeningUnit(cell);
        Assert.Equal((v0 * cell, v1 * cell), centred.OpeningZ(cell));

        SocketKit floor = centred with { Sill = DoorSill.Floor };
        Assert.Equal((depth, depth + height), floor.OpeningZ(cell));
        (float fu0, float fv0, float fu1, float fv1) = floor.OpeningUnit(cell);
        Assert.Equal((u0, u1), (fu0, fu1));
        Assert.Equal(depth / cell, fv0);
        Assert.Equal((depth + height) / cell, fv1);
    }

    /// <summary>
    /// The sill is part of the kit: a floor kit is not the centred kit of the
    /// same numbers; a centred kit prints as it always did, and a floor kit
    /// says so; a sill that is neither rule is refused.
    /// </summary>
    [Fact]
    public void TheSillIsPartOfTheKit()
    {
        SocketKit centred = new(96, 224, 16);
        SocketKit floor = centred with { Sill = DoorSill.Floor };
        Assert.Equal(DoorSill.Centred, centred.Sill);
        Assert.NotEqual(centred, floor);
        Assert.Equal(new SocketKit(96, 224, 16), centred);
        Assert.Equal("SocketKit { Width = 96, Height = 224, Depth = 16 }", centred.ToString());
        Assert.Equal("SocketKit { Width = 96, Height = 224, Depth = 16, Sill = Floor }", floor.ToString());
        floor.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (centred with { Sill = (DoorSill)2 }).Validate());
    }

    /// <summary>
    /// The case the rule is for: a 192-high door in a 1024 cell with a 32
    /// wall hangs 384 units above the floor when centred, and a player walks
    /// through it on the floor; a floor kit's door must still be a player's
    /// size.
    /// </summary>
    [Fact]
    public void AFloorDoorAPlayerFitsThroughPassesAtAnyHeight()
    {
        SocketKit centred = new(256, 192, 32);
        string? problem = PlayerHull.DoorProblem(centred, 1024);
        Assert.Contains("puts the door's sill at z = 416 but the floor's top at z = 32 (the wall depth)", problem, StringComparison.Ordinal);
        Assert.EndsWith("cell size - 2 x wall depth = 960, or at any height with door_sill floor", problem, StringComparison.Ordinal);

        Assert.Null(PlayerHull.DoorProblem(centred with { Sill = DoorSill.Floor }, 1024));
        Assert.Equal(
            "is 64 tall; a standing player needs a door at least 72 tall",
            PlayerHull.DoorProblem(new SocketKit(96, 64, 32) { Sill = DoorSill.Floor }, 1024));
        Assert.Equal(
            "is 24 wide; a standing player needs a door at least 32 wide",
            PlayerHull.DoorProblem(new SocketKit(24, 192, 32) { Sill = DoorSill.Floor }, 1024));
    }

    // ---- the library --------------------------------------------------------------------------

    /// <summary>A one-room library of a room on its own wall depth, its model the room model's, its marker naming the sill when it is the floor's.</summary>
    private static VmfDocument OneRoomLibrary(RoomDefinition room)
    {
        VmfDocument library = RoomModel.Build(room, room.Kit.Depth);
        library.Chunks.Add(RoomHarness.InfoRoom(room, Vec3.Zero));
        return library;
    }

    /// <summary>
    /// <c>door_sill floor</c> on the marker splits the library the centred
    /// rule refuses: its plugs stand on the floor slab, the door's height
    /// above it, on every wall.
    /// </summary>
    [Fact]
    public void ALibraryWithAFloorSillSplits()
    {
        RoomDefinition room = new("mine", 1024, new SocketKit(256, 192, 32) { Sill = DoorSill.Floor }, [new RoomSocket(RoomFacing.PositiveX, "east"), new RoomSocket(RoomFacing.NegativeY, "south")]);
        LibraryRoom split = Assert.Single(RoomLibraryVmf.Split(OneRoomLibrary(room)));
        Assert.Equal(DoorSill.Floor, split.Definition.Kit.Sill);
        Assert.Equal(room.Sockets, split.Definition.Sockets);
        Assert.Equal(new Box(new Vec3(992, 384, 32), new Vec3(1024, 640, 224)), RoomLinter.SealBox(split.Definition, split.Definition.Sockets[0], 1024));

        VmfDocument centred = OneRoomLibrary(room);
        RemoveKey(centred.GetChunks("entity").Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity), RoomLibraryVmf.DoorSillKey);
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(centred));
        Assert.Equal(
            "the library's door kit (door_width 256, door_height 192, wall_depth 32, cell_size 1024) puts the door's sill at z = 416"
            + " but the floor's top at z = 32 (the wall depth), so a player cannot step through; a door stands on the floor when its"
            + " height is cell size - 2 x wall depth = 960, or at any height with door_sill floor.",
            refused.Message);
    }

    /// <summary>
    /// <c>centre</c>, in any case, is the default spelt out: the same kit as
    /// no key; any other value is refused naming the two rules.
    /// </summary>
    [Theory]
    [InlineData("centre", null)]
    [InlineData("CENTRE", null)]
    [InlineData("Floor", null)]
    [InlineData("middle", "room \"hub\": \"door_sill\" is \"middle\"; a door stands on the floor (floor) or is centred up its wall (centre, the default).")]
    [InlineData("", "room \"hub\": \"door_sill\" is \"\"; a door stands on the floor (floor) or is centred up its wall (centre, the default).")]
    public void TheSillKeyIsReadOrRefused(string value, string? message)
    {
        bool floor = value.Equals("floor", StringComparison.OrdinalIgnoreCase);
        RoomDefinition hub = floor ? FloorRoom("hub") : RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX);
        VmfDocument library = RoomHarness.LibraryVmf(hub);
        VmfChunk marker = library.GetChunks("entity").Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity);
        RemoveKey(marker, RoomLibraryVmf.DoorSillKey);
        marker.AddKey(RoomLibraryVmf.DoorSillKey, value);
        if (message is not null)
        {
            Assert.Equal(message, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library)).Message);
            return;
        }

        LibraryRoom room = Assert.Single(RoomLibraryVmf.Split(library));
        Assert.Equal(hub.Kit, room.Definition.Kit);
        Assert.Equal(floor ? DoorSill.Floor : DoorSill.Centred, room.Definition.Kit.Sill);
    }

    /// <summary>
    /// Every room of a library shares one kit, its sill included; and a
    /// floor door must fit under the ceiling of a room as tall as its cell.
    /// </summary>
    [Fact]
    public void ALibraryHoldsItsRoomsToOneSillAndTheDoorToTheCell()
    {
        VmfDocument mixed = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX), RoomHarness.WalkableRoom("end", RoomFacing.PositiveX));
        mixed.GetChunks("entity").Where(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity).Last().AddKey(RoomLibraryVmf.DoorSillKey, "floor");
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(mixed));
        Assert.Equal(
            "room \"end\" is built for cell 256 and kit SocketKit { Width = 96, Height = 224, Depth = 16, Sill = Floor },"
            + " but room \"hub\" for cell 256 and kit SocketKit { Width = 96, Height = 224, Depth = 16 }; every room of a library shares one grid and one door kit.",
            refused.Message);

        // 16 + 225 reaches into the 16-unit ceiling slab of a 256 cell.
        VmfDocument tall = RoomHarness.LibraryVmf(FloorRoom("hub"));
        VmfChunk marker = tall.GetChunks("entity").Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity);
        marker.Keys.Single(k => k.Name == RoomLibraryVmf.DoorHeightKey).Value = "225";
        Assert.Equal(
            "room \"hub\": the door kit SocketKit { Width = 96, Height = 225, Depth = 16, Sill = Floor } does not fit in a 256-unit cell.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(tall)).Message);
        marker.Keys.Single(k => k.Name == RoomLibraryVmf.DoorHeightKey).Value = "224";
        Assert.Equal(DoorSill.Floor, RoomLibraryVmf.Split(tall)[0].Definition.Kit.Sill);
    }

    /// <summary>
    /// A room lower than its cell keeps its floor doors: its height may be
    /// as low as the door and the two slabs, and the plug boxes are the
    /// cube room's, bit for bit.
    /// </summary>
    [Fact]
    public void ALowRoomKeepsItsFloorDoors()
    {
        Assert.Null(RoomDefinition.HeightProblem(Low, FloorKit));
        Assert.Equal(
            "159 leaves no room for the door; a room is at least 160 tall (door_height + 2 x wall_depth).",
            RoomDefinition.HeightProblem(Low - 1, FloorKit));

        VmfDocument library = RoomHarness.LibraryVmf(FloorRoom("hub"), FloorRoom("low", Low));
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal([RoomHarness.Cell, Low], rooms.Select(r => r.Definition.Height));
        Assert.Equal(RoomCompiler.SealBoxes(rooms[0].Definition), RoomCompiler.SealBoxes(rooms[1].Definition));
        Assert.All(RoomCompiler.SealBoxes(rooms[1].Definition), b => Assert.Equal((16f, 144f), (b.Mins.Z, b.Maxs.Z)));
    }

    // ---- between libraries --------------------------------------------------------------------

    /// <summary>17.3, kit: two libraries whose doors stand differently are refused, naming the key as authors write it.</summary>
    [Fact]
    public void LibrariesWithDifferentSillsAreRefused()
    {
        static LevelLibraries.LibraryFacts Facts(string key, SocketKit kit) =>
            new(key, $"../{key}.vmf", new RoomDefinition("room", 256, kit, []), [], RoomLibraryOptions.None, null, [new("classname", "worldspawn")]);

        SocketKit centred = new(96, 128, 16);
        LinkException refused = Assert.Throws<LinkException>(() => LevelLibraries.Check([Facts("base", centred), Facts("mine", centred with { Sill = DoorSill.Floor })]));
        Assert.Equal(
            "libraries base (../base.vmf) and mine (../mine.vmf) have different door kits: door_sill centre against floor; the rooms of a level join through one kit.",
            refused.Message);
        Assert.Empty(LevelLibraries.Check([Facts("base", centred with { Sill = DoorSill.Floor }), Facts("mine", centred with { Sill = DoorSill.Floor })]));
    }

    // ---- the compile, the container and the link ----------------------------------------------

    /// <summary>
    /// A floor kit's room records the sill: its container's kit says
    /// <c>"sill":"floor"</c> and loads back as a floor kit, its compile's
    /// claimed key ends in <c>,floor</c>; a centred room's container and key
    /// have no sill at all; and a container whose sill is anything else is
    /// refused.
    /// </summary>
    [Fact]
    public async Task TheContainerRecordsAFloorSill()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(FloorRoom("hub"), FloorRoom("low", Low)));
        RoomObject hub = rooms.Get("hub");
        Assert.EndsWith("|256|96,128,16,floor", hub.InputKeys[^1], StringComparison.Ordinal);
        Assert.EndsWith("|256|96,128,16,floor|h160", rooms.Get("low").InputKeys[^1], StringComparison.Ordinal);

        using MemoryStream file = new();
        await RoomObjectStore.SaveAsync(hub, file);
        byte[] bytes = file.ToArray();
        Assert.Contains("\"kit\":{\"width\":96,\"height\":128,\"depth\":16,\"sill\":\"floor\"}", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using (MemoryStream read = new(bytes))
        {
            RoomObject loaded = await RoomObjectStore.LoadAsync(read);
            Assert.Equal(hub.Definition.Kit, loaded.Definition.Kit);
            Assert.Equal(hub.Definition.Sockets, loaded.Definition.Sockets);
            Assert.Equal(hub.InputKeys[^1], loaded.InputKeys[^1]);
        }

        RoomLibrary centred = await RoomPropHarness.CompileAsync(RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX)));
        using MemoryStream plain = new();
        await RoomObjectStore.SaveAsync(centred.Get("hub"), plain);
        Assert.DoesNotContain("sill", Encoding.UTF8.GetString(plain.ToArray()), StringComparison.Ordinal);
        Assert.EndsWith("|256|96,224,16", centred.Get("hub").InputKeys[^1], StringComparison.Ordinal);

        byte[] edited = Replace(bytes, "\"sill\":\"floor\"", "\"sill\":\"flor\"");
        using MemoryStream bad = new(edited);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomObjectStore.LoadAsync(bad));
        Assert.Equal("room manifest kit sill is not \"floor\".", refused.Message);
    }

    /// <summary>
    /// A floor-kit level, the cube hub beside the low room at every turn: the
    /// link and the flattened level's compile hold every sample of both cells
    /// to the same space; a line through the door near the floor is clear in
    /// both and one above the door's top is not; the map passes the loader
    /// checks; and the link is the same bytes at one thread and four.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task AFloorKitLevelLinksAsItFlattensAtEveryTurn(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(FloorRoom("hub"), FloorRoom("low", Low));
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        LevelGrid level = RoomHeightHarness.Level($"hub, low@{rotation}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await RoomHeightHarness.CompileFlatAsync(library, level);

        Assert.True(RoomHeightHarness.SameSpace(linked.Bsp, flat, level) > 0);
        foreach (BspData map in new[] { linked.Bsp, flat })
        {
            LevelProbe probe = new(map);
            Assert.Equal(1f, probe.Trace(new Vec3(128, 128, 40), new Vec3(256 + 128, 128, 40), (int)BrushContents.Solid));
            Assert.True(probe.Trace(new Vec3(128, 128, 150), new Vec3(256 + 128, 128, 150), (int)BrushContents.Solid) < 1f);
            Assert.NotEqual(0, probe.Contents(new Vec3(256 + 128, 128, 150)) & (int)BrushContents.Solid);
        }

        Assert.Null(RoomHeightHarness.BoundsProblem(linked.Bsp));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
        LinkedLevel four = await RoomPropHarness.LinkAsync(rooms, level, degree: 4);
        Assert.Equal(await MultiLibraryHarness.BytesAsync(linked.Bsp), await MultiLibraryHarness.BytesAsync(four.Bsp));
    }

    // ---- the door light's grid ----------------------------------------------------------------

    /// <summary>
    /// The door light's response grid stands over the room across the joint
    /// wherever the door does: a centred kit lifts it by exactly nothing (its
    /// nodes are the bits they were), a floor kit by the cell's half height
    /// less the opening's middle; and a source on a lifted node goes wholly
    /// to that node.
    /// </summary>
    [Fact]
    public void TheDoorLightGridIsLiftedOverTheRoomAcrossAFloorDoor()
    {
        Assert.Equal(0f, DoorLightMath.Lift(new SocketKit(96, 224, 16), 256));
        float lift = DoorLightMath.Lift(FloorKit, 256);
        Assert.Equal(128f - (16f + 64f), lift);
        for (int n = 0; n < DoorLightMath.NodesPerClass; n++)
        {
            Vec3 plain = DoorLightMath.Node(n, 256, 16);
            Assert.Equal(plain, DoorLightMath.Node(n, 256, 16, 0));
            Assert.Equal(plain + new Vec3(0, 0, lift), DoorLightMath.Node(n, 256, 16, lift));
        }

        Assert.Equal(DoorLightMath.NodeFlux(96, 224, 256, 16), DoorLightMath.NodeFlux(96, 224, 256, 16, 0));
        double[] nodeFlux = DoorLightMath.NodeFlux(96, 128, 256, 16, lift);
        Assert.NotEqual(DoorLightMath.NodeFlux(96, 128, 256, 16), nodeFlux);
        DoorSource lamp = new(EmitType.Point, DoorLightMath.Node(5, 256, 16, lift), default, new Vec3(1, 1, 1), 0, 0, 0, 1, 0, 0, 0, DoorLightMath.AllCells);
        Vec3[] shares = new Vec3[DoorLightMath.EmitterCount];
        DoorLightMath.Project(lamp, new Vec3(2, 2, 2), nodeFlux, 256, 16, shares, lift);
        for (int e = 0; e < shares.Length; e++)
        {
            Assert.Equal(e == 5 ? (float)(2 / nodeFlux[5]) : 0f, shares[e].X, 5);
        }
    }

    private static void RemoveKey(VmfChunk chunk, string name)
    {
        foreach (VmfKey key in chunk.Keys.Where(k => k.Name == name).ToList())
        {
            chunk.Children.Remove(key);
        }
    }

    private static byte[] Replace(byte[] bytes, string from, string to)
    {
        byte[] needle = Encoding.UTF8.GetBytes(from);
        int at = bytes.AsSpan().IndexOf(needle);
        Assert.True(at >= 0);
        byte[] replacement = Encoding.UTF8.GetBytes(to);

        // The manifest is a length-prefixed blob: one byte shorter moves
        // everything after it, so the length before the manifest is fixed up.
        List<byte> result = [.. bytes[..at], .. replacement, .. bytes[(at + needle.Length)..]];
        int lengthAt = 12;
        int length = (bytes[lengthAt] << 24) | (bytes[lengthAt + 1] << 16) | (bytes[lengthAt + 2] << 8) | bytes[lengthAt + 3];
        length += replacement.Length - needle.Length;
        result[lengthAt] = (byte)(length >> 24);
        result[lengthAt + 1] = (byte)(length >> 16);
        result[lengthAt + 2] = (byte)(length >> 8);
        result[lengthAt + 3] = (byte)length;
        return [.. result];
    }
}
