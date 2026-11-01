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

    [Theory]
    [InlineData("")]
    [InlineData("/game/maps/hall.bsp -bogus")]
    [InlineData("/game/maps/hall.bsp -nav-codec zip")]
    [InlineData("/game/maps/hall.bsp -threads 0")]
    [InlineData("/game/maps/hall.bsp /game/maps/other.bsp")]
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
