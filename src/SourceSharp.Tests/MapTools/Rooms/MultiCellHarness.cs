//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms larger than one cell for the multi-cell facts (the rooms design,
/// 17.8), on the walkable kit and the harness's 256 grid: a 2 x 2 hall with
/// a door on every face at chosen cell edges, a 1 x 2 gallery, and the prop
/// harness's four-socket hub to join them to.
/// </summary>
internal static class MultiCellHarness
{
    /// <summary>A room of the footprint with sockets at the given (face, offset) edges, named by default.</summary>
    public static RoomDefinition Large(string name, int cellsX, int cellsY, params (RoomFacing Facing, int Offset)[] sockets) =>
        new RoomDefinition(name, RoomHarness.Cell, RoomHarness.WalkableKit,
            [.. sockets.Select(s => new RoomSocket(s.Facing, RoomLibraryVmf.DefaultSocketName(cellsX != 1 || cellsY != 1, s.Facing, s.Offset)) { Offset = s.Offset })])
        { CellsX = cellsX, CellsY = cellsY };

    /// <summary>The 2 x 2 hall: east at its south cell, west at its north, north at its west cell, south at its east.</summary>
    public static RoomDefinition Hall => Large(
        "hall", 2, 2,
        (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 1), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 1));

    /// <summary>The 1 x 2 gallery: a door at each end and one on its east side's north cell.</summary>
    public static RoomDefinition Gallery => Large(
        "gallery", 1, 2,
        (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 0), (RoomFacing.PositiveX, 1));

    /// <summary>The four-socket cube room.</summary>
    public static RoomDefinition Hub => RoomPropHarness.Hub;
}
