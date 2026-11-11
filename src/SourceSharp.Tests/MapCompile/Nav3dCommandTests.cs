//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap nav3d &lt;map.bsp&gt;</c>: the navigation of a compiled map,
/// written beside it (or where <c>-out</c> says), bound to its bytes, with
/// props' hulls from the game the map is in or <c>-game</c> names.
/// </summary>
public sealed class Nav3dCommandTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Nav"
        	FileSystem
        	{
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        		}
        	}
        }
        """;

    /// <summary>Where the static prop stands: solid with the game's models, open without them.</summary>
    private static readonly Vec3 InTheProp = new(64, 176, 24);

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static VPath At(string path) => VPath.Create(Rooted(path));

    private InMemoryFileSystem Game(string mapPath = "/game/maps/hall.bsp")
    {
        InMemoryFileSystem fs = new();
        fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
        foreach ((string path, byte[] bytes) in RoomPropHarness.Models())
        {
            fs.AddFile(Rooted("/game/" + path), bytes);
        }

        fs.AddFile(Rooted(mapPath), map.Bytes);
        return fs;
    }

    /// <summary>
    /// With no options the file goes beside the map as <c>hall.nav3d</c>,
    /// Brotli-stored, bound to the map, its props solid from the game the
    /// map's folder belongs to; one line reports the numbers.
    /// </summary>
    [Fact]
    public async Task TheNavigationGoesBesideTheMapBoundToIt()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();

        int exit = await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        byte[] file = fs.GetBytes(At("/game/maps/hall.nav3d"))!;
        Nav3dReader nav = Nav3dReader.Open(file);
        Assert.Equal(NavCodec.Brotli, nav.Codec);
        Assert.True(nav.MatchesBsp(map.Bytes));
        Assert.Equal(-1, nav.FindLeaf(InTheProp));
        Assert.Equal(2, nav.ObstacleCount);
        Assert.Contains(
            $"ssmap nav3d: wrote {HostPaths.Display(At("/game/maps/hall.nav3d"))}: {file.Length:N0} bytes, 4 of 6 cells (2 x 3, 16 voxels up),",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("warning", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>-out</c> and <c>-nav-codec</c> are honoured; <c>-game</c> names the game for a map outside it.</summary>
    [Fact]
    public async Task OutCodecAndGameAreHonoured()
    {
        InMemoryFileSystem fs = Game("/elsewhere/hall.bsp");
        using StringWriter output = new();

        int exit = await Nav3dCommand.RunAsync(
            fs, [], ["/elsewhere/hall.bsp", "-out", "/out/x.nav3d", "-nav-codec", "none", "-game", "/game", "-threads", "2"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.False(await fs.ExistsAsync(At("/elsewhere/hall.nav3d")));
        Nav3dReader nav = Nav3dReader.Open(fs.GetBytes(At("/out/x.nav3d"))!);
        Assert.Equal(NavCodec.None, nav.Codec);
        Assert.Equal(-1, nav.FindLeaf(InTheProp));
    }

    /// <summary>
    /// <c>-timings</c> reports each phase, the host's around the build's own,
    /// the cells' work and the total, and writes the same file as a run
    /// without it; without it there are no timing lines.
    /// </summary>
    [Fact]
    public async Task TimingsReportEachPhaseAndChangeNothing()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter plain = new();
        Assert.Equal(Program.ExitSuccess, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp", "-out", "/out/a.nav3d"], plain));
        Assert.DoesNotContain("time ", plain.ToString(), StringComparison.Ordinal);

        using StringWriter timed = new();
        int exit = await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp", "-out", "/out/b.nav3d", "-timings", "-threads", "2"], timed);

        Assert.True(exit == Program.ExitSuccess, timed.ToString());
        string[] lines = [.. timed.ToString().Split('\n').Where(l => l.StartsWith("ssmap nav3d: time ", StringComparison.Ordinal))];
        Assert.Equal(
            ["read", "models", "brushes", "props", "displacements", "geometry", "play space", "tree", "cells", "stitch", "jumps", "level", "write",
                "cells:", "total"],
            lines.Select(l => l["ssmap nav3d: time ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] switch
            {
                "play" => "play space",
                string word => word,
            }));
        Assert.All(lines.Where(l => !l.Contains("cells:", StringComparison.Ordinal)), l => Assert.Contains(" s cpu ", l, StringComparison.Ordinal));
        Assert.Contains("6 built", lines[13], StringComparison.Ordinal);
        Assert.Equal(fs.GetBytes(At("/out/a.nav3d")), fs.GetBytes(At("/out/b.nav3d")));
    }

    /// <summary>A map outside any game, with no <c>-game</c>, is built without props, and says so.</summary>
    [Fact]
    public async Task WithoutAGameThePropsAreLeftOutAndTheOutputSaysSo()
    {
        InMemoryFileSystem fs = Game("/elsewhere/hall.bsp");
        using StringWriter output = new();

        int exit = await Nav3dCommand.RunAsync(fs, [], ["/elsewhere/hall.bsp"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Nav3dReader nav = Nav3dReader.Open(fs.GetBytes(At("/elsewhere/hall.nav3d"))!);
        Assert.True(nav.FindLeaf(InTheProp) >= 0);
        Assert.Contains("ssmap nav3d: warning: no -game and no gameinfo.txt above", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ssmap nav3d: warning: static prop model \"models/props_test/box.mdl\"", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary><c>-voxel</c> chooses the voxel: 8 units gives 32 to a 256-unit cell; one that does not divide the cell fails naming it.</summary>
    [Fact]
    public async Task VoxelChoosesTheGridsVoxel()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();
        Assert.Equal(Program.ExitSuccess, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp", "-voxel", "8"], output));
        Nav3dReader nav = Nav3dReader.Open(fs.GetBytes(At("/game/maps/hall.nav3d"))!);
        Assert.Equal((8f, 32), (nav.VoxelSize, nav.CellVoxels));

        using StringWriter refused = new();
        Assert.Equal(RoomCommands.ExitFailed, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp", "-voxel", "10", "-out", "/game/maps/ten.nav3d"], refused));
        Assert.Contains("does not divide the 256-unit cell", refused.ToString(), StringComparison.Ordinal);
        Assert.False(await fs.ExistsAsync(At("/game/maps/ten.nav3d")));
    }

    [Theory]
    [InlineData("/game/maps/hall.bsp -voxel 0")]
    [InlineData("/game/maps/hall.bsp -voxel x")]
    [InlineData("")]
    [InlineData("/game/maps/hall.bsp -bogus")]
    [InlineData("/game/maps/hall.bsp -nav-codec zip")]
    [InlineData("/game/maps/hall.bsp -threads 0")]
    [InlineData("/game/maps/hall.bsp /game/maps/other.bsp")]
    [InlineData("/game/maps/hall.bsp -check -check")]
    [InlineData("/game/maps/hall.bsp -playspace maybe")]
    [InlineData("/game/maps/hall.bsp -playspace")]
    public async Task ACommandLineItCannotActOnPrintsTheUsage(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        InMemoryFileSystem fs = Game();
        using StringWriter output = new();

        Assert.Equal(Program.ExitUsage, await Nav3dCommand.RunAsync(fs, [], args, output));
        Assert.StartsWith("usage: ssmap nav3d <map.bsp>", output.ToString(), StringComparison.Ordinal);
        Assert.False(await fs.ExistsAsync(At("/game/maps/hall.nav3d")));
    }

    /// <summary>Bytes that are not a map, and a map that is not there, fail with a line naming the file; nothing is written.</summary>
    [Fact]
    public async Task AFileThatIsNotAMapFailsNamingIt()
    {
        InMemoryFileSystem fs = Game();
        fs.AddText(Rooted("/game/maps/junk.bsp"), "not a map");
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/junk.bsp"], output));
        Assert.Contains($"ssmap nav3d: {HostPaths.Display(At("/game/maps/junk.bsp"))}: not a BSP", output.ToString(), StringComparison.Ordinal);
        Assert.False(await fs.ExistsAsync(At("/game/maps/junk.nav3d")));

        using StringWriter missing = new();
        Assert.Equal(RoomCommands.ExitFailed, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/none.bsp"], missing));
        Assert.Contains($"ssmap nav3d: {HostPaths.Display(At("/game/maps/none.bsp"))}:", missing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>-check</c> builds nothing: it reads the navigation beside the map
    /// (or at <c>-out</c>) and reports every place a player stands as having
    /// ground under it; a navigation built from other bytes, and one that is
    /// not there, fail naming the file.
    /// </summary>
    [Fact]
    public async Task CheckReadsTheBuiltNavigationAndFindsGroundUnderEveryPlaceToStand()
    {
        InMemoryFileSystem fs = Game();
        using StringWriter built = new();
        Assert.Equal(Program.ExitSuccess, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp"], built));
        byte[] before = fs.GetBytes(At("/game/maps/hall.nav3d"))!;

        using StringWriter output = new();
        int exit = await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp", "-check", "-threads", "2"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Matches(
            $@"^ssmap nav3d: checked {System.Text.RegularExpressions.Regex.Escape(HostPaths.Display(At("/game/maps/hall.nav3d")))}: [\d,]+ places a player stands, 0 with no ground within 18 units under its box, ",
            output.ToString());
        Assert.Equal(before, fs.GetBytes(At("/game/maps/hall.nav3d")));

        fs.AddFile(Rooted("/game/maps/other.bsp"), [.. map.Bytes, 0]);
        using StringWriter other = new();
        Assert.Equal(RoomCommands.ExitFailed, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/other.bsp", "-out", "/game/maps/hall.nav3d", "-check"], other));
        Assert.Contains($"{HostPaths.Display(At("/game/maps/hall.nav3d"))} was not built from {HostPaths.Display(At("/game/maps/other.bsp"))}", other.ToString(), StringComparison.Ordinal);

        using StringWriter missing = new();
        Assert.Equal(RoomCommands.ExitFailed, await Nav3dCommand.RunAsync(fs, [], ["/game/maps/hall.bsp", "-out", "/game/maps/none.nav3d", "-check"], missing));
        Assert.Contains($"ssmap nav3d: {HostPaths.Display(At("/game/maps/none.nav3d"))}:", missing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The verb is dispatched and listed in the usage.</summary>
    [Fact]
    public async Task TheVerbIsDispatchedAndListed()
    {
        using StringWriter output = new();
        Assert.Equal(Program.ExitUsage, await Program.RunAsync(["nav3d"], output));
        Assert.StartsWith("usage: ssmap nav3d <map.bsp>", output.ToString(), StringComparison.Ordinal);

        using StringWriter help = new();
        Assert.Equal(Program.ExitSuccess, await Program.RunAsync(["help"], help));
        Assert.Contains("nav3d <map.bsp> [-out <file.nav3d>] [-game <dir>]", help.ToString(), StringComparison.Ordinal);
    }
}
