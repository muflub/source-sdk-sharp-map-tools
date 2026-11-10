//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;
using SourceSharp.Tests.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The multi-cell sample through the commands (the rooms design, 17.8 and
/// 17.12): <c>ssmap room</c> packs its larger rooms at version 6, <c>ssmap
/// rooms</c> lists their footprints, and each level, at its four turns,
/// links with no warning but the missing navigation's, passes <c>ssmap check</c>, and is held to its
/// flattened level's whole compile in what a player meets: the same solid,
/// open and water space at every point of a lattice, the same lines clear
/// between the rooms' cells, a level map of the same bytes, the same
/// entities, and every sight line the flattened compile has kept by the
/// link's visibility.
/// </summary>
public sealed class RoomsMultiCellSampleCommandsTests
{
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in RoomsMultiCellSample.Build())
        {
            fs.AddFile(Rooted("/multicell/" + path), bytes);
        }

        return fs;
    }

    private static byte[] Bytes(InMemoryFileSystem fs, string path) => fs.GetBytes(VPath.Create(Rooted(path)))!;

    private static async Task<BspData> MapAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(Bytes(fs, path));
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>The whole chain, every turn: see the class.</summary>
    [Fact]
    public async Task EveryLevelLinksAndMatchesItsFlattenedCompile()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter pack = new();
        Assert.True(
            await RoomCommands.RunRoomAsync(fs, [], ["/multicell/rooms.vmf", "-game", "/multicell"], pack) == Program.ExitSuccess,
            pack.ToString());
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(Bytes(fs, "/multicell/rooms.roompack").AsSpan(8)));

        using StringWriter listing = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/multicell/rooms.vmf"], listing));
        string[] lines = listing.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.StartsWith("court: cell at (", StringComparison.Ordinal) && l.EndsWith("768 x 768 x 256, 3 x 3 cells, 4 door(s)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("atrium: cell at (", StringComparison.Ordinal) && l.EndsWith("512 x 512 x 512, 2 x 2 cells, 4 door(s)", StringComparison.Ordinal));

        VmfDocument library = await VmfDocument.ParseAsync(RoomsMultiCellSample.Build()["rooms.vmf"]);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        for (int turns = 0; turns < 4; turns++)
        {
            string name = RoomsMultiCellSample.TurnName(turns);
            LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(Bytes(fs, $"/multicell/levels/{name}.yaml")), name);

            using StringWriter link = new();
            Assert.True(await RoomCommands.RunLinkAsync(fs, [$"/multicell/levels/{name}.yaml", "-out", $"/multicell/out/{name}.bsp"], link) == Program.ExitSuccess, link.ToString());
            Assert.DoesNotContain("warning", link.ToString(), StringComparison.Ordinal);
            await RoomsFeaturesSampleCommandsTests.AssertCheckedAsync(fs, $"/multicell/out/{name}.bsp");

            using StringWriter flatten = new();
            Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLinkAsync(fs, [$"/multicell/levels/{name}.yaml", "--flatten", "-out", $"/multicell/maps/{name}.vmf"], flatten));
            using StringWriter vbsp = new();
            Assert.True(await VbspCommand.RunAsync(fs, [$"/multicell/maps/{name}.vmf"], cooker, vbsp) == Program.ExitSuccess, vbsp.ToString());
            await RoomsFeaturesSampleCommandsTests.AssertCheckedAsync(fs, $"/multicell/maps/{name}.bsp");

            BspData linked = await MapAsync(fs, $"/multicell/out/{name}.bsp");
            BspData flat = await MapAsync(fs, $"/multicell/maps/{name}.bsp");

            // Space: solid, open or water alike at every point, the atrium's upper half included.
            Assert.Equal(LevelLinkerWaterTests.Points(flat, level.Columns, level.Rows), LevelLinkerWaterTests.Points(linked, level.Columns, level.Rows));
            Assert.Equal(Lattice(flat, level, [300, 450]), Lattice(linked, level, [300, 450]));
            Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked));
            Assert.Equal(RoomDisplacementHarness.Infos(flat).Length, RoomDisplacementHarness.Infos(linked).Length);

            // Lines: clear between two cells' middles in one map exactly when in the other.
            LevelProbe linkedProbe = new(linked), flatProbe = new(flat);
            List<Vec3> middles = [.. Middles(level)];
            int clear = 0;
            for (int a = 0; a < middles.Count; a++)
            {
                for (int b = a + 1; b < middles.Count; b++)
                {
                    bool open = flatProbe.Trace(middles[a], middles[b], (int)BrushContents.Solid) == 1f;
                    Assert.True(open == (linkedProbe.Trace(middles[a], middles[b], (int)BrushContents.Solid) == 1f), $"{name}: {middles[a]} to {middles[b]}");
                    clear += open ? 1 : 0;

                    // Every sight line the flattened compile has, the link's PVS keeps.
                    if (open)
                    {
                        int ca = linkedProbe.Leafs[linkedProbe.Leaf(middles[a])].Cluster, cb = linkedProbe.Leafs[linkedProbe.Leaf(middles[b])].Cluster;
                        Assert.True(Sees(linked, ca, cb), $"{name}: the link's PVS drops {middles[a]} to {middles[b]}");
                    }
                }
            }

            Assert.True(clear > middles.Count, $"{name}: {clear} clear lines");

            // The navigation: every cell a room covers holds the flattened
            // compile's grid run for run, every place a player stands has
            // ground under it in the linked map's own collision, and ssmap
            // nav reads it.
            Nav3dReader nav = Nav3dReader.Open(Bytes(fs, $"/multicell/out/{name}.nav3d"));
            LevelLayout covered = level.ToLayout(r => RoomLibraryVmf.Split(library).Single(x => x.Definition.Name == r).Definition, 256, new SocketKit(96, 224, 16));
            List<(int X, int Y)> cells = [.. covered.Rooms.SelectMany(r => r.Placement.Covered().Select(c => (c.X, c.Y)))];
            Assert.Equal(25, cells.Count);
            RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav, cells);
            NavFloorReport floor = NavFloorCheck.Check(nav, linked, BspNavMap.Hull, null);
            Assert.Empty(floor.Missing);
            Assert.True(floor.Standable > 1000, $"{name}: {floor.Standable} places to stand");
            using StringWriter navText = new();
            Assert.Equal(Program.ExitSuccess, await NavCommand.RunAsync(fs, [$"/multicell/out/{name}.nav3d"], navText));
            Assert.Contains("25 placed", navText.ToString(), StringComparison.Ordinal);

            // The level map, and the entities.
            uint checksum = BspMapChecksum.Compute(Bytes(fs, $"/multicell/out/{name}.bsp"));
            Assert.True(
                Map2dWriter.Write(LevelMapBuilder.FromCompile(flat, checksum, level, [library])).AsSpan().SequenceEqual(Bytes(fs, $"/multicell/out/{name}.map2d")),
                $"{name}: the linked level map is not the flattened compile's");
            Assert.Equal(Classes(flat), Classes(linked));
        }
    }

    /// <summary>Whether a cell's middle at standing height and four points around it, in every grid cell.</summary>
    private static IEnumerable<Vec3> Middles(LevelGrid level)
    {
        for (int x = 0; x < level.Columns; x++)
        {
            for (int y = 0; y < level.Rows; y++)
            {
                foreach ((float dx, float dy) in new (float, float)[] { (128, 128), (60, 60), (196, 196), (60, 196), (196, 60) })
                {
                    yield return new Vec3((x * 256) + dx, (y * 256) + dy, 100);
                }
            }
        }
    }

    /// <summary>Solid or open at every point of a 32-unit lattice at the heights given.</summary>
    private static List<bool> Lattice(BspData bsp, LevelGrid level, float[] heights)
    {
        List<bool> solid = [];
        for (float x = 8; x < level.Columns * 256; x += 32)
        {
            for (float y = 8; y < level.Rows * 256; y += 32)
            {
                foreach (float z in heights)
                {
                    solid.Add((RoomHarness.LeafAt(bsp, new Vec3(x, y, z)).Contents & (int)BrushContents.Solid) != 0);
                }
            }
        }

        return solid;
    }

    /// <summary>Whether cluster <paramref name="a"/>'s PVS row in a map's visibility lump has cluster <paramref name="b"/>.</summary>
    private static bool Sees(BspData bsp, int a, int b)
    {
        if (a < 0 || b < 0)
        {
            return true;
        }

        ReadOnlySpan<byte> lump = bsp[BspLump.Visibility].Data.Span;
        int clusters = BinaryPrimitives.ReadInt32LittleEndian(lump);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(lump[(4 + (a * 8))..]);
        byte[] row = new byte[(clusters + 7) >> 3];
        VisRunLength.Decompress(lump[offset..], row);
        return (row[b >> 3] & (1 << (b & 7))) != 0;
    }

    /// <summary>A map's entity classes, sorted, the world left out.</summary>
    private static List<string> Classes(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Skip(1).Select(e => e.Get("classname") ?? string.Empty).Order(StringComparer.Ordinal)];
}
