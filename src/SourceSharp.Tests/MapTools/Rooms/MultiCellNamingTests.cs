//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.MultiCellHarness;
using static SourceSharp.Tests.MapTools.Rooms.RoomNamingFacts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Names in rooms larger than one cell (the rooms design, 17.8 and O28): a
/// larger room's <c>cxry_</c> is its anchor cell; it names no neighbour
/// cell and has no neighbour flags, since a side has several neighbours;
/// its <c>room_needs</c> names a socket, <c>joined_&lt;socket&gt;</c>; and a
/// neighbour naming any cell it covers names it by its anchor.
/// </summary>
public sealed class MultiCellNamingTests
{
    private static List<LevelEntity> Entities(params string[][] keys) => Room(keys)(0);

    private static RoomLintException Refused(params string[] keys) =>
        Assert.Throws<RoomLintException>(() => RoomNameAnalysis.Analyse("hall", Entities(["classname", "info_target", "id", "5", .. keys]), null, Hall));

    /// <summary>A neighbour offset, a neighbour flag, a direction and a side are refused in a larger room with 17.3's texts.</summary>
    [Fact]
    public void ALargerRoomNamesOnlyItsOwnCell()
    {
        Assert.Equal(
            "room hall: entity 5 (info_target) key \"target\": \"cx+1ry_door\" names a neighbour cell; a room of 2 x 2 cells names only its own cell (cxry_).",
            Refused("target", "cx+1ry_door").Message);
        Assert.Equal(
            "room hall: entity 5 (info_target) key \"target\": \"cxry_has_east\" names a neighbour cell; a room of 2 x 2 cells names only its own cell (cxry_).",
            Refused("target", "cxry_has_east").Message);
        Assert.Equal(
            "room hall: entity 5 (info_target) key \"OnUser1\": \"cxry-1_door\" names a neighbour cell; a room of 2 x 2 cells names only its own cell (cxry_).",
            Refused("OnUser1", Out("cxry-1_door", "Open")).Message);
        foreach (string needs in (string[])["east", "joined_east", "!north", "joined_east0,south"])
        {
            Assert.Equal(
                $"room hall: entity 5 (info_target) room_needs \"{needs}\": a room of 2 x 2 cells has several neighbours on a side; name a socket, joined_<socket>.",
                Refused("room_needs", needs).Message);
        }

        // The same names are a room of one cell's to use.
        _ = RoomNameAnalysis.Analyse("hub", Entities(["classname", "info_target", "target", "cx+1ry_door", "room_needs", "joined_east"]), null, Hub);
        _ = RoomNameAnalysis.Analyse("hall", Entities(["classname", "info_target", "targetname", "cxry_door"]), null, Hall);
    }

    /// <summary>
    /// <c>joined_&lt;socket&gt;</c> holds when that socket of the placement
    /// is joined: the entity is kept with the socket joined and dropped with
    /// it capped, negated the other way round; the condition survives the
    /// pack's names section.
    /// </summary>
    [Theory]
    [InlineData("joined_east0", 1UL, true)]
    [InlineData("joined_east0", 2UL, false)]
    [InlineData("!joined_west1", 1UL, true)]
    [InlineData("!joined_west1", 2UL, false)]
    public void JoinedSocketHoldsWhenThatSocketIsJoined(string needs, ulong joined, bool kept)
    {
        List<LevelEntity> entities = Entities(["classname", "info_target", "targetname", "cxry_lamp", "room_needs", needs]);
        RoomNameTurn names = RoomNameAnalysis.Analyse("hall", entities, null, Hall)[1];
        NeedsRef stored = Assert.Single(names.Needs);
        Assert.Equal(needs.Contains("east0", StringComparison.Ordinal) ? 0 : 1, Assert.Single(stored.Conditions).Need.Socket);
        RoomNameTurn read = RoomNameTurn.Read(names.ToSection().Bytes.ToArray(), "hall", 1)!;
        Assert.Equal(stored.Conditions[0].Need, Assert.Single(read.Needs).Conditions[0].Need);

        LevelResolution resolution = LevelEntityResolver.Resolve(
            [
                new ResolverRoom
                {
                    Room = "hall", Column = 1, Row = 2, Turns = 1, Names = names, Entities = entities, Joined = JoinedMask.None,
                    JoinedSockets = joined, SpanX = 2, SpanY = 2, CellCentre = "0 0 0",
                },
            ],
            new LevelNamingOptions(false, true, 4, 4));
        Assert.Equal(kept, Named(resolution, "c1r2_lamp") is not null);
    }

    /// <summary>
    /// A room of one cell naming a cell a larger room covers names that
    /// room by its anchor, where the larger room's own names are; a cell
    /// beyond it still holds no room.
    /// </summary>
    [Fact]
    public void ANeighboursNameOfACoveredCellNamesTheAnchor()
    {
        LevelResolution resolution = Resolve(false,
        [
            new Placed("hall", 0, 0, 0, Room(["classname", "info_target", "targetname", "cxry_door"]), SpanX: 2, SpanY: 2),
            new Placed("hub", 2, 1, 0, Room(["classname", "info_target", "targetname", "cxry_switch", "target", "cx-1ry_door"])),
        ]);
        LevelEntity hub = Named(resolution, "c2r1_switch")!;
        Assert.Equal("c0r0_door", hub.Get("target"));
        Assert.NotNull(Named(resolution, "c0r0_door"));
    }
}
