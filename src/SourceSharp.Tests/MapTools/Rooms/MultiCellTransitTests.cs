//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.TransitHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A transition room larger than one cell (the rooms design, 17.8 and O30:
/// role rooms may be larger at the link): a 2 x 2 up room whose arrival,
/// spawn, button and volume stand in its north-east cell, not its anchor,
/// links as its level flattens in both modes at two turns, its transition
/// named by its anchor.
/// </summary>
public sealed class MultiCellTransitTests
{
    private const string Keys = "up_map: above\ndown_map: below\n";

    /// <summary>The turns and both modes.</summary>
    public static TheoryData<int, bool> TurnsAndModes => new() { { 0, false }, { 0, true }, { 90, false }, { 90, true } };

    private static RoomDefinition BigUp => MultiCellHarness.Large(
        "up", 2, 2, (RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 1), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 1));

    /// <summary>The library: the 2 x 2 up room (its entities a cell north-east of the anchor), the down room and the plain room.</summary>
    private static VmfDocument Library()
    {
        VmfDocument library = RoomHarness.LibraryVmf(BigUp, Down, Plain);
        float down = 512 + RoomHarness.LibraryGap, plain = down + RoomHarness.Cell + RoomHarness.LibraryGap;
        foreach ((VmfChunk[] entities, Vec3 at) in new[] { (UpEntities, new Vec3(256, 256, 0)), (DownEntities, new Vec3(down, 0, 0)), (PlainEntities, new Vec3(plain, 0, 0)) })
        {
            foreach (VmfChunk entity in entities)
            {
                library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(at)));
            }
        }

        Marker(library, "up").AddKey(RoomPois.RoleKey, "up");
        Marker(library, "down").AddKey(RoomPois.RoleKey, "down");
        return library;
    }

    /// <summary>The link and the flattened level's whole-map compile carry the same entities and models, in both modes at two turns of the larger up room.</summary>
    [Theory]
    [MemberData(nameof(TurnsAndModes))]
    public async Task ALargerUpRoomLinksAsItFlattens(int rotation, bool mod)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = Level("mid", Keys, "+, +, plain", $"up@{rotation}, +, down");
        LinkedLevel linked = await LinkAsync(rooms, level, mod);
        BspData whole = await CompileFlatAsync(library, level, mod);
        Assert.Equal(Comparable(whole), Comparable(linked.Bsp));
        Assert.Equal(Models(whole), Models(linked.Bsp));
        Assert.True(linked.HasTransitions);
        if (!mod)
        {
            Assert.Contains(OfClass(linked.Bsp, "func_button"), b => b.Get("OnPressed")!.StartsWith("c0r0_", StringComparison.Ordinal));
        }
    }
}
