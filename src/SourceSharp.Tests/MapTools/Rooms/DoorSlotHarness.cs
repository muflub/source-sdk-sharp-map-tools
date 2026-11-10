//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms under a kit of three door slots per cell edge (the rooms design,
/// 17.8 stage 9): the walkable kit's doors narrowed to 48 so three fit a
/// 256-unit edge with a wall depth of wall at each corner (3 x (48 + 2 x
/// 16) = 240), at 19, 104 and 189 along it.
/// </summary>
internal static class DoorSlotHarness
{
    /// <summary>The three-slot kit.</summary>
    public static SocketKit Kit { get; } = new SocketKit(48f, RoomHarness.Cell - 32f, 16f) { Slots = 3 };

    /// <summary>A room of the footprint under the three-slot kit, with sockets at the given (face, cell offset, slot) places, named by default.</summary>
    public static RoomDefinition Room(string name, int cellsX, int cellsY, params (RoomFacing Facing, int Offset, int Slot)[] sockets) =>
        new RoomDefinition(name, RoomHarness.Cell, Kit,
            [.. sockets.Select(s => new RoomSocket(s.Facing, RoomLibraryVmf.DefaultSocketName(cellsX != 1 || cellsY != 1, s.Facing, s.Offset, Kit.Slots, s.Slot)) { Offset = s.Offset, Slot = s.Slot })])
        { CellsX = cellsX, CellsY = cellsY };

    /// <summary>A one-cell room with sockets at the given (face, slot) places.</summary>
    public static RoomDefinition Room(string name, params (RoomFacing Facing, int Slot)[] sockets) =>
        Room(name, 1, 1, [.. sockets.Select(s => (s.Facing, 0, s.Slot))]);

    /// <summary>Two doors on one wall: east at its south and north slots, and a door in the middle of the west wall.</summary>
    public static RoomDefinition Twin => Room("twin", (RoomFacing.PositiveX, 0), (RoomFacing.PositiveX, 2), (RoomFacing.NegativeX, 1));

    /// <summary>A corridor along its south side: doors at the south slot of the east and west walls.</summary>
    public static RoomDefinition Corridor => Room("corridor", (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0));

    /// <summary>Every wall's first slot: south on east and west, west on north and south.</summary>
    public static RoomDefinition Corner => Room("corner", (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 0));

    /// <summary>Every slot of the east wall, and the west slot of the north and south walls.</summary>
    public static RoomDefinition Three => Room(
        "three", (RoomFacing.PositiveX, 0), (RoomFacing.PositiveX, 1), (RoomFacing.PositiveX, 2), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 0));

    /// <summary>A 2 x 2 room with a socket at every slot of every cell edge: whichever way it turns, each edge has every slot.</summary>
    public static RoomDefinition Ring => Room(
        "ring", 2, 2, [.. Enum.GetValues<RoomFacing>().SelectMany(f => Enumerable.Range(0, 6).Select(k => (f, k / 3, k % 3)))]);

    /// <summary>A 2 x 2 room: the east wall's south cell at its north slot, the west wall's north cell at its south slot, the north wall's west cell in the middle.</summary>
    public static RoomDefinition Big => Room(
        "big", 2, 2, (RoomFacing.PositiveX, 0, 2), (RoomFacing.NegativeX, 1, 0), (RoomFacing.PositiveY, 0, 1));
}
