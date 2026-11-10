//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Rooms;


using Xunit;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>
/// The multi-cell sample's generator: the checked-in files, the library as
/// the pipeline's splitter reads it (footprints, sockets per cell edge,
/// heights), and the level and its turns re-anchored on their footprints.
/// </summary>
public sealed class RoomsMultiCellSampleTests
{
    /// <summary>The files under <c>samples/rooms-multicell/</c> are exactly what the generator writes, plus the hand-written README.</summary>
    [RepoSourceFact("samples/rooms-multicell/README.md")]
    public async Task TheCheckedInSampleIsWhatTheGeneratorWrites() =>
        await SampleFiles.AssertCheckedInAsync(RoomsMultiCellSample.Folder, RoomsMultiCellSample.Build(), "--multicell");

    /// <summary>Two builds are the same bytes, LF only.</summary>
    [Fact]
    public void TheBuildIsDeterministic()
    {
        IReadOnlyDictionary<string, byte[]> first = RoomsMultiCellSample.Build();
        IReadOnlyDictionary<string, byte[]> second = RoomsMultiCellSample.Build();
        Assert.Equal(first.Keys, second.Keys);
        foreach ((string path, byte[] bytes) in first)
        {
            Assert.Equal(bytes, second[path]);
            Assert.DoesNotContain((byte)'\r', bytes);
        }
    }

    /// <summary>
    /// The library splits into its five rooms with their footprints, heights
    /// and the sockets the generator cut, each at its cell edge.
    /// </summary>
    [Fact]
    public async Task TheLibrarySplitsIntoTheSamplesRooms()
    {
        VmfDocument library = await VmfDocument.ParseAsync(RoomsMultiCellSample.Build()["rooms.vmf"]);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal(RoomsMultiCellSample.Rooms.Select(r => r.Name), rooms.Select(r => r.Definition.Name));
        for (int i = 0; i < rooms.Count; i++)
        {
            RoomsMultiCellSample.MultiRoom expected = RoomsMultiCellSample.Rooms[i];
            RoomDefinition definition = rooms[i].Definition;
            Assert.Equal((expected.CellsX, expected.CellsY, expected.Height), (definition.CellsX, definition.CellsY, definition.Height));
            Assert.Equal(expected.Sockets.Order(), definition.Sockets.Select(s => (s.Facing, s.Offset)).Order());
        }

        Assert.Null(library.GetChunk("world")!.GetValue("nav"));
    }

    /// <summary>
    /// Every level file lays out, its rooms covering the cells its + marks
    /// say; each turn is the level before it turned a quarter, every room a
    /// quarter further on and anchored in its turned footprint's south-west
    /// cell; the fourth turn is the level again.
    /// </summary>
    [Fact]
    public void TheTurnedLevelsAreTheLevelTurned()
    {
        IReadOnlyDictionary<string, byte[]> files = RoomsMultiCellSample.Build();
        LevelGrid Read(int turns)
        {
            string name = RoomsMultiCellSample.TurnName(turns);
            return LevelYaml.Parse(Encoding.UTF8.GetString(files[$"levels/{name}.yaml"]), name);
        }

        RoomDefinition Of(string name)
        {
            RoomsMultiCellSample.MultiRoom room = RoomsMultiCellSample.Rooms.Single(r => r.Name == name);
            return new RoomDefinition(name, 256, new SocketKit(96, 224, 16), []) { CellsX = room.CellsX, CellsY = room.CellsY, Height = room.Height };
        }

        LevelGrid level = Read(0);
        for (int turns = 1; turns <= 4; turns++)
        {
            LevelLayout layout = level.ToLayout(Of, 256, new SocketKit(96, 224, 16));
            Assert.Equal(10, layout.Rooms.Count);
            LevelGrid turned = RoomsMultiCellSample.Turned(level);
            LevelGrid expected = Read(turns % 4);
            Assert.Equal(
                expected.Cells.Select(c => c is null ? "~" : $"{c.Room}@{c.Rotation}"),
                turned.Cells.Select(c => c is null ? "~" : $"{c.Room}@{c.Rotation}"));
            level = expected;
        }
    }
}
