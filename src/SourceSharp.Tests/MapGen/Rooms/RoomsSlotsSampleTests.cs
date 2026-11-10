//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>The door slots sample (<see cref="RoomsSlotsSample"/>): its files and its rooms.</summary>
public sealed class RoomsSlotsSampleTests
{
    /// <summary>The files under <c>samples/rooms-slots/</c> are exactly what the generator writes, plus the hand-written README.</summary>
    [RepoSourceFact("samples/rooms-slots/README.md")]
    public async Task TheCheckedInSampleIsWhatTheGeneratorWrites() =>
        await SampleFiles.AssertCheckedInAsync(RoomsSlotsSample.Folder, RoomsSlotsSample.Build(), "--slots");

    /// <summary>The library splits into its five rooms under the three-slot kit, each socket at its slot and named for it.</summary>
    [Fact]
    public async Task TheLibrarysRoomsStandAtTheirSlots()
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(await SourceSharp.MapFormats.Text.VmfDocument.ParseAsync(RoomsSlotsSample.LibraryVmf()));
        Assert.Equal(["twin", "corridor", "corner", "three", "ring"], rooms.Select(r => r.Definition.Name));
        Assert.All(rooms, r => Assert.Equal(RoomsSlotsSample.Kit, r.Definition.Kit));
        Assert.Equal(["east_a", "east_c", "west_b"], rooms[0].Definition.Sockets.Select(s => s.Name));
        Assert.Equal(24, rooms[4].Definition.Sockets.Count);
        Assert.Equal([11f, 96f, 181f], Enumerable.Range(0, 3).Select(k => RoomsSlotsSample.Kit.SlotLow(256, k)));
    }
}
