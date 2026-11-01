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
/// <c>ssmap all</c> writes the finished map's <c>.nav3d</c> beside it, by
/// the path <c>ssmap nav3d</c> takes, bound to the map's final bytes;
/// <c>-no-nav</c> writes none, and <c>-require-nav</c> turns a navigation
/// that cannot be written from a warning into a failure.
/// </summary>
public sealed class AllNavCommandTests
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

    /// <summary>A game directory holding the harness materials and models, and <see cref="BspNavMap"/>'s VMF in its <c>maps/</c>.</summary>
    private sealed class Game : IDisposable
    {
        public Game()
        {
            Root = Path.Combine(Path.GetTempPath(), "allnav-" + Guid.NewGuid().ToString("N"));
            Mod = Path.Combine(Root, "mod");
            Directory.CreateDirectory(Path.Combine(Mod, "maps"));
            File.WriteAllText(Path.Combine(Mod, "gameinfo.txt"), GameInfoText);
            Write($"materials/{RoomHarness.Plain}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n"u8.ToArray());
            Write($"materials/{RoomHarness.Trigger}.vmt",
                "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n"u8.ToArray());
            Write($"materials/{RoomHarness.PlayerClip}.vmt",
                "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%playerClip\" \"1\"\n}\n"u8.ToArray());
            foreach ((string path, byte[] bytes) in RoomPropHarness.Models())
            {
                Write(path, bytes);
            }

            Vmf = Path.Combine(Mod, "maps", "hall.vmf");
            File.WriteAllBytes(Vmf, BspNavMap.Vmf().ToBytes());
        }

        public string Root { get; }

        public string Mod { get; }

        public string Vmf { get; }

        public string Bsp => Path.ChangeExtension(Vmf, ".bsp");

        public string Nav => Path.ChangeExtension(Vmf, ".nav3d");

        public async Task<(int Exit, string Output)> AllAsync(params string[] more)
        {
            using StringWriter output = new();
            int exit = await AllCommand.RunAsync(new PhysicalFileSystem("/"), [], ["-game", Mod, "-cooker", "none", .. more, Vmf], output);
            return (exit, output.ToString());
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string path, byte[] bytes)
        {
            string full = Path.Combine(Mod, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    /// <summary>
    /// The chain writes <c>hall.nav3d</c> after vrad: it names the final
    /// map's bytes, and the props are solid from the game the chain mounted.
    /// </summary>
    [Fact]
    public async Task AllWritesTheFinishedMapsNavigationBesideIt()
    {
        using Game game = new();

        (int exit, string output) = await game.AllAsync();

        Assert.True(exit == Program.ExitSuccess, output);
        Nav3dReader nav = Nav3dReader.Open(await File.ReadAllBytesAsync(game.Nav));
        Assert.True(nav.MatchesBsp(await File.ReadAllBytesAsync(game.Bsp)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(64, 176, 24)));
        Assert.Equal(NavCodec.Brotli, nav.Codec);
        Assert.Contains($"ssmap all: wrote {game.Nav}: ", output, StringComparison.Ordinal);
    }

    /// <summary><c>-no-nav</c> writes the map and no navigation; <c>-nav-codec</c> chooses the file's codec.</summary>
    [Fact]
    public async Task NoNavWritesNoneAndNavCodecChoosesTheCodec()
    {
        using Game game = new();

        (int exit, string output) = await game.AllAsync("-no-nav");
        Assert.True(exit == Program.ExitSuccess, output);
        Assert.True(File.Exists(game.Bsp));
        Assert.False(File.Exists(game.Nav));
        Assert.DoesNotContain(".nav3d", output, StringComparison.Ordinal);

        (exit, output) = await game.AllAsync("-nav-codec", "none");
        Assert.True(exit == Program.ExitSuccess, output);
        Assert.Equal(NavCodec.None, Nav3dReader.Open(await File.ReadAllBytesAsync(game.Nav)).Codec);
    }

    /// <summary>
    /// A navigation that cannot be written (here: a directory stands where
    /// the file goes) leaves the written map and warns; with
    /// <c>-require-nav</c> the run fails.
    /// </summary>
    [Fact]
    public async Task ANavigationThatCannotBeWrittenWarnsOrWithRequireNavFails()
    {
        using Game game = new();
        Directory.CreateDirectory(game.Nav);

        (int exit, string output) = await game.AllAsync();
        Assert.True(exit == Program.ExitSuccess, output);
        Assert.True(File.Exists(game.Bsp));
        Assert.Contains($"ssmap all: cannot write {game.Nav}:", output, StringComparison.Ordinal);
        Assert.Contains("ssmap all: warning: the map was written without a .nav3d (-require-nav makes this an error, -no-nav skips it)", output, StringComparison.Ordinal);

        (exit, output) = await game.AllAsync("-require-nav");
        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("ssmap all: the map was written, but its navigation failed", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("m.vmf", true, false, "brotli:5")]
    [InlineData("-no-nav m.vmf", false, false, "brotli:5")]
    [InlineData("-require-nav -nav-codec deflate:6 m.vmf", true, true, "deflate:6")]
    public void TheNavSwitchesAreChainOptions(string line, bool navigation, bool require, string codec)
    {
        AllArgs parsed = AllCommand.Parse(line.Split(' '));
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        Assert.True(NavCompression.TryParse(codec, out NavCompression expected));
        Assert.Equal((navigation, require, expected), (parsed.Navigation, parsed.RequireNavigation, parsed.NavCompression));
    }

    [Theory]
    [InlineData("-no-nav -require-nav m.vmf", "-no-nav and -require-nav: give one")]
    [InlineData("-nav-codec zip m.vmf", "-nav-codec \"zip\" is not none, deflate[:0-9] or brotli[:0-11]")]
    public void ConflictingOrBadNavSwitchesAreProblems(string line, string message)
    {
        AllArgs parsed = AllCommand.Parse(line.Split(' '));
        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Diagnostics, d => d.Message == message);
    }
}
