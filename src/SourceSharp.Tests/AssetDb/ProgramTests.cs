//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapGen.Content;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The <c>ssassets</c> command line: parsing, exit codes, and the verbs end
/// to end against a game directory on disk.
/// </summary>
public sealed class ProgramTests
{
    private const string GameInfo = """
        "GameInfo"
        {
            game	"Test Game"
            FileSystem
            {
                SearchPaths
                {
                    game+mod	|gameinfo_path|.
                }
            }
        }
        """;

    private static async Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exit = await Program.RunAsync(args, output, error, "/home/nobody", CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A game directory on disk: one brush material, its texture and one prop.</summary>
    private static string WriteGame(TempTree temp)
    {
        temp.Write("install/testgame/gameinfo.txt", System.Text.Encoding.UTF8.GetBytes(GameInfo));
        temp.Write(
            "install/testgame/materials/brick/wall.vtf",
            VtfWriter.Write(8, 8, ImageFormat.Bgra8888, 0, (_, _) => (150, 60, 40, 255)));
        temp.Write(
            "install/testgame/materials/brick/wall.vmt",
            System.Text.Encoding.UTF8.GetBytes("LightmappedGeneric { $basetexture brick/wall }"));
        (byte[] mdl, byte[] vvd, byte[] vtx) = AssetTestGame.CubeModel();
        temp.Write("install/testgame/models/props/cube.mdl", mdl);
        temp.Write("install/testgame/models/props/cube.vvd", vvd);
        temp.Write("install/testgame/models/props/cube.dx90.vtx", vtx);
        return Path.Combine(temp.Root, "install", "testgame");
    }

    [Fact]
    public async Task NoArgumentsPrintsUsageAndFails()
    {
        (int exit, string output, _) = await RunAsync();

        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("ssassets import", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("help")]
    public async Task HelpPrintsUsageAndSucceeds(string flag)
    {
        (int exit, string output, _) = await RunAsync(flag);

        Assert.Equal(Program.ExitSuccess, exit);
        Assert.Equal(Program.Usage, output);
    }

    [Fact]
    public async Task ABadCommandLineSaysWhyAndExitsWithUsage()
    {
        (int exit, _, string error) = await RunAsync("import", "--out", "/tmp/x");

        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("import needs at least one --game", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingVerbReportsAndExitsWithFailure()
    {
        using TempTree temp = new();

        (int exit, _, string error) = await RunAsync("list", "--out", Path.Combine(temp.Root, "none"));

        Assert.Equal(Program.ExitFailure, exit);
        Assert.Contains("no asset database", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportListQueryAndRemoveWorkEndToEnd()
    {
        using TempTree temp = new();
        string game = WriteGame(temp);
        string db = Path.Combine(temp.Root, "db");

        (int importExit, string imported, string progress) = await RunAsync(
            "import", "--game", game, "--out", db, "--view-size", "64", "--threads", "2");

        Assert.Equal(Program.ExitSuccess, importExit);
        Assert.Contains("testgame: 1 textures, 1 materials, 1 models, 0 issues", imported, StringComparison.Ordinal);
        Assert.Contains("mounting testgame", progress, StringComparison.Ordinal);
        Assert.Contains("textures 1/1", progress, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(db, "assets.db")));
        Assert.True(File.Exists(Path.Combine(db, "README.md")));
        Assert.True(File.Exists(Path.Combine(db, "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(db, "textures", "brick", "wall.png")));
        Assert.True(File.Exists(Path.Combine(db, "models", "props", "cube", "sheet.png")));

        (int listExit, string listed, _) = await RunAsync("list", "--out", db);
        Assert.Equal(Program.ExitSuccess, listExit);
        Assert.StartsWith("testgame\tTest Game\t1 textures", listed, StringComparison.Ordinal);

        (int queryExit, string rows, _) = await RunAsync(
            "query", "--out", db, "SELECT material, image, keywords FROM material_view");
        Assert.Equal(Program.ExitSuccess, queryExit);
        Assert.Equal(
            "material\timage\tkeywords" + Environment.NewLine + "brick/wall\ttextures/brick/wall.png\tNULL" + Environment.NewLine,
            rows);

        (int writeExit, _, string refused) = await RunAsync("query", "--out", db, "DELETE FROM materials");
        Assert.Equal(Program.ExitFailure, writeExit);
        Assert.Contains("readonly", refused, StringComparison.OrdinalIgnoreCase);

        (int removeExit, string removed, _) = await RunAsync("remove", "--out", db, "--name", "testgame");
        Assert.Equal(Program.ExitSuccess, removeExit);
        Assert.Equal("removed testgame; 8 images deleted" + Environment.NewLine, removed);
        Assert.False(File.Exists(Path.Combine(db, "textures", "brick", "wall.png")));

        (int again, _, string missing) = await RunAsync("remove", "--out", db, "--name", "testgame");
        Assert.Equal(Program.ExitFailure, again);
        Assert.Contains("no game named testgame", missing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NameOverridesTheDirectoryName()
    {
        using TempTree temp = new();
        string game = WriteGame(temp);
        string db = Path.Combine(temp.Root, "db");

        (int exit, string output, _) = await RunAsync(
            "import", "--game", game, "--out", db, "--name", "mine", "--models", "none", "--no-textures");

        Assert.Equal(Program.ExitSuccess, exit);
        Assert.Contains("mine: 0 textures, 1 materials, 0 models", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsingImportCollectsEveryFlag()
    {
        CommandLine command = CommandLine.Parse(
        [
            "import", "--game", "a", "--game", "b", "--out", "o", "--steam", "s1", "--steam", "s2",
            "--models", "all", "--no-textures", "--texture-size", "0", "--view-size", "256", "--threads", "3", "--force",
        ]);

        Assert.Equal("import", command.Verb);
        Assert.Equal(["a", "b"], command.Games);
        Assert.Equal("o", command.Out);
        Assert.Equal(["s1", "s2"], command.Steam);
        Assert.Equal(ModelSelection.All, command.Options.Models);
        Assert.False(command.Options.Textures);
        Assert.Equal(0, command.Options.MaxTextureSize);
        Assert.Equal(256, command.Options.ViewSize);
        Assert.Equal(3, command.Options.MaxParallelism);
        Assert.True(command.Options.Force);
    }

    [Theory]
    [InlineData("props", ModelSelection.Props)]
    [InlineData("none", ModelSelection.None)]
    public void ModelSelectionsParse(string value, ModelSelection expected) =>
        Assert.Equal(expected, CommandLine.Parse(["import", "--game", "g", "--out", "o", "--models", value]).Options.Models);

    [Fact]
    public void ParsingTheOtherVerbs()
    {
        Assert.Equal("o", CommandLine.Parse(["list", "--out", "o"]).Out);
        Assert.Equal("tf", CommandLine.Parse(["remove", "--out", "o", "--name", "tf"]).Name);
        Assert.Equal("SELECT 1", CommandLine.Parse(["query", "--out", "o", "SELECT 1"]).Sql);
    }

    [Theory]
    [InlineData(new string[0], "no command given")]
    [InlineData(new[] { "build" }, "unknown command 'build'")]
    [InlineData(new[] { "list" }, "list needs --out")]
    [InlineData(new[] { "list", "--out" }, "--out needs a value")]
    [InlineData(new[] { "list", "--out", "o", "--bogus" }, "unknown option '--bogus' for list")]
    [InlineData(new[] { "import", "--out", "o" }, "import needs at least one --game")]
    [InlineData(new[] { "import", "--out", "o", "--game", "a", "--game", "b", "--name", "n" }, "--name names one game")]
    [InlineData(new[] { "import", "--out", "o", "--game", "a", "--models", "some" }, "--models takes props, all or none")]
    [InlineData(new[] { "import", "--out", "o", "--game", "a", "--threads", "0" }, "--threads takes a whole number of at least 1")]
    [InlineData(new[] { "import", "--out", "o", "--game", "a", "--view-size", "x" }, "--view-size takes a whole number")]
    [InlineData(new[] { "import", "--out", "o", "--game", "a", "--texture-size", "-1" }, "--texture-size takes a whole number")]
    [InlineData(new[] { "remove", "--out", "o" }, "remove needs --name")]
    [InlineData(new[] { "query", "--out", "o" }, "query needs the SQL")]
    [InlineData(new[] { "query", "--out", "o", "SELECT 1", "SELECT 2" }, "unknown option 'SELECT 2'")]
    public void BadCommandLinesSayWhy(string[] args, string message)
    {
        FormatException error = Assert.Throws<FormatException>(() => CommandLine.Parse(args));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/games/Team Fortress 2/tf", "tf")]
    [InlineData("/games/Source SDK Base 2013 Multiplayer/HL2/", "hl2")]
    public void TheDefaultGameNameIsTheDirectorysOwnName(string directory, string expected) =>
        Assert.Equal(expected, Program.GameName(directory));

    [Fact]
    public void TheDefaultSteamRootsAreUnderHome()
    {
        IReadOnlyList<string> roots = Program.DefaultSteamRoots("/home/someone");

        Assert.Equal(4, roots.Count);
        // Joined with the platform's separator: a backslash after the home on Windows.
        Assert.All(roots, r => Assert.StartsWith("/home/someone" + Path.DirectorySeparatorChar, r, StringComparison.Ordinal));
        Assert.All(roots, r => Assert.EndsWith(Path.Combine("steamapps", "common"), r, StringComparison.Ordinal));
    }
}
