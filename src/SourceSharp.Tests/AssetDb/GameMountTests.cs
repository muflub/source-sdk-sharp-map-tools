//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The export mount: a gameinfo.txt's search paths, with appid paths resolved
/// through an in-memory stand-in for the Steam library list. Game directories
/// are absolute, as the host always names them.
/// </summary>
public class GameMountTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"/steam"
        		"apps"
        		{
        			"243750"		"1"
        		}
        	}
        }
        """;

    private const string SdkManifest = """
        "AppState"
        {
        	"appid"		"243750"
        	"installdir"		"Source SDK Base 2013 Multiplayer"
        }
        """;

    private const string GameInfo = """
        "GameInfo"
        {
            game	"asset db fixture"
            FileSystem
            {
                SteamAppId	243750
                SearchPaths
                {
                    game+mod			modcontent
                    game				|appid_243750|hl2
                }
            }
        }
        """;

    private const string GameInfoMissingPath = """
        "GameInfo"
        {
            game	"asset db fixture"
            FileSystem
            {
                SteamAppId	243750
                SearchPaths
                {
                    game+mod			nosuchdir
                    game				|appid_243750|hl2
                }
            }
        }
        """;

    private static InMemoryFileSystem Disk() => new InMemoryFileSystem()
        .AddText("steam/steamapps/libraryfolders.vdf", LibraryFolders)
        .AddText("steam/steamapps/appmanifest_243750.acf", SdkManifest)
        .AddText("steam/steamapps/common/Source SDK Base 2013 Multiplayer/hl2/materials/hl2/walls/wall_a.vtf", "vtf")
        // A plain search path is relative to the install directory, the
        // gameinfo directory's parent, as the engine reads it.
        .AddText("games/proj/modcontent/materials/mod/detail.vmt", "vmt")
        .AddText("games/proj/game/gameinfo.txt", GameInfo)
        .AddText("games/proj/game/broken/gameinfo.txt", GameInfoMissingPath);

    [Fact]
    public async Task ModAndAppIdContentAreReachableThroughTheMount()
    {
        InMemoryFileSystem disk = Disk();

        await using GameMount mount = await GameMount.OpenAsync(
            disk, "/games/proj/game", ["/steam/steamapps/common"], CancellationToken.None);

        Assert.NotNull(await mount.Content.ReadAsync(VPath.Create("materials/mod/detail.vmt"), CancellationToken.None));
        Assert.NotNull(await mount.Content.ReadAsync(
            VPath.Create("materials/hl2/walls/wall_a.vtf"), CancellationToken.None));
        Assert.Empty(mount.Skipped);
    }

    [Fact]
    public async Task UnmatchedSearchPathsAreReportedNotThrown()
    {
        InMemoryFileSystem disk = Disk();

        await using GameMount mount = await GameMount.OpenAsync(
            disk, "/games/proj/game/broken", ["/steam/steamapps/common"], CancellationToken.None);

        // The missing mod dir is reported; the appid path still mounted.
        Assert.Equal(["nosuchdir"], mount.Skipped);
        Assert.NotNull(await mount.Content.ReadAsync(
            VPath.Create("materials/hl2/walls/wall_a.vtf"), CancellationToken.None));
    }

    [Fact]
    public async Task AnAppIdPathWithoutAnySteamRootThrowsRatherThanSilentlySkipping()
    {
        InMemoryFileSystem disk = Disk();

        // The compilers treat an unresolvable appid path as a configuration
        // error, not a warning; the export mount keeps that contract.
        await Assert.ThrowsAsync<InvalidOperationException>(() => GameMount.OpenAsync(
            disk, "/games/proj/game", null, CancellationToken.None));
    }

    [Fact]
    public async Task TheGameDirectoryIsCarriedForTheHost()
    {
        InMemoryFileSystem disk = Disk();

        await using GameMount mount = await GameMount.OpenAsync(
            disk, "/games/proj/game", ["/steam/steamapps/common"], CancellationToken.None);

        Assert.Equal("/games/proj/game", mount.GameDirectory);
        Assert.NotNull(mount.Info);
    }

    [Fact]
    public async Task ADirectoryWithoutAGameinfoIsNamedInTheError()
    {
        InMemoryFileSystem disk = Disk();

        FileNotFoundException error = await Assert.ThrowsAsync<FileNotFoundException>(() => GameMount.OpenAsync(
            disk, "/games/proj/nothing", ["/steam/steamapps/common"], CancellationToken.None));

        Assert.Contains("no gameinfo.txt in /games/proj/nothing", error.Message, StringComparison.Ordinal);
    }
}
