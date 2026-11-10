//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using SourceSharp.Tests.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The door slots sample through the commands (the rooms design, 17.8 stage
/// 9): <c>ssmap room</c> packs it, <c>ssmap rooms</c> lists each socket's
/// slot, <c>ssmap link</c> links its level with no warning and the map
/// passes the loader checks, the linked map holds every sample as the
/// flattened compile does, the level's navigation is one walk through every
/// room and its columns are the flattened compile's, and <c>ssmap layout</c>
/// says plainly that it lays out libraries of one slot only.
/// </summary>
public sealed class RoomsSlotsSampleCommandsTests
{
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in RoomsSlotsSample.Build())
        {
            fs.AddFile(Rooted("/slots/" + path), bytes);
        }

        return fs;
    }

    private static byte[] Bytes(InMemoryFileSystem fs, string path) => fs.GetBytes(VPath.Create(Rooted(path)))!;

    /// <summary>The whole chain: see the class.</summary>
    [Fact]
    public async Task TheLevelPacksListsLinksAndNavigates()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter pack = new();
        Assert.True(await RoomCommands.RunRoomAsync(fs, [], ["/slots/rooms.vmf", "-game", "/slots"], pack) == Program.ExitSuccess, pack.ToString());

        using StringWriter listing = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/slots/rooms.vmf"], listing));
        string[] lines = listing.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("  east_a: (248, 11, 8) to (256, 75, 248), 64 wide x 240 high x 8 deep, slot 1 of 3", lines);
        Assert.Contains("  east_c: (248, 181, 8) to (256, 245, 248), 64 wide x 240 high x 8 deep, slot 3 of 3", lines);

        using StringWriter link = new();
        Assert.True(await RoomCommands.RunLinkAsync(fs, ["/slots/levels/slots.yaml", "-out", "/slots/out/slots.bsp"], link) == Program.ExitSuccess, link.ToString());
        Assert.DoesNotContain("warning", link.ToString(), StringComparison.Ordinal);
        await RoomsFeaturesSampleCommandsTests.AssertCheckedAsync(fs, "/slots/out/slots.bsp");

        VmfDocument library = await VmfDocument.ParseAsync(RoomsSlotsSample.Build()["rooms.vmf"]);
        LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(Bytes(fs, "/slots/levels/slots.yaml")), "slots");
        BspData linked;
        using (MemoryStream stream = new(Bytes(fs, "/slots/out/slots.bsp")))
        {
            linked = await BspFile.LoadAsync(stream);
        }

        BspData flat = await RoomHeightHarness.CompileFlatAsync(library, level);
        Assert.True(RoomHeightHarness.SameSpace(linked, flat, level) > 0);

        Nav3dReader nav = Nav3dReader.Open(Bytes(fs, "/slots/out/slots.nav3d"));
        (int[] component, int count) = Nav3dWalk.Components(nav, Nav3dWalker.Player);
        Assert.Single(component.Where((c, l) => Nav3dWalk.IsStand(nav, Nav3dWalker.Player, l)).Distinct());
        Assert.True(count >= 1);
        RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav, [.. level.Placed.SelectMany(p => Covered(level, p))]);

        using StringWriter layout = new();
        Assert.NotEqual(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, ["/slots/rooms.vmf", "-rows", "3", "-columns", "3", "-seed", "1"], layout));
        Assert.Equal(
            "ssmap layout: the door kit has door_slots 3; the generator lines rooms up at one door per cell edge, so it lays out libraries of one door slot only. Write a level of slot rooms by hand.",
            layout.ToString().Trim());
    }

    private static IEnumerable<(int X, int Y)> Covered(LevelGrid level, (int X, int Y, LevelCell Cell) placed)
    {
        RoomsSlotsSample.SlotRoom room = RoomsSlotsSample.Rooms.Single(r => r.Name == placed.Cell.Room);
        return new RoomPlacement(placed.Cell.Room, placed.X, placed.Y, placed.Cell.Rotation) { CellsX = room.CellsX, CellsY = room.CellsY }
            .Covered().Select(c => (c.X, c.Y));
    }
}
