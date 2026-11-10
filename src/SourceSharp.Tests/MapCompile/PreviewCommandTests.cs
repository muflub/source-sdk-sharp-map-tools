//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.Json;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapPreview;
using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>Facts for <c>ssmap preview</c>: arguments, the game it mounts, and the files it writes.</summary>
public sealed class PreviewCommandTests
{
    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Preview"
        	FileSystem
        	{
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        		}
        	}
        }
        """;

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    private static VPath At(string path) => VPath.Create(Rooted(path));

    private static async Task<InMemoryFileSystem> DiskAsync(string mapPath, bool game)
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, new PreviewTestMaps.TestEntity(
            "info_player_teamspawn", [("origin", "128 128 16"), ("TeamNum", "2")]));
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(bsp, bytes);
        InMemoryFileSystem fs = new();
        fs.AddFile(Rooted(mapPath), bytes.ToArray());
        if (game)
        {
            fs.AddText(Rooted("/game/gameinfo.txt"), GameInfoText);
            fs.AddText(Rooted("/game/materials/unit/plain.vmt"), "\"UnlitGeneric\" { \"$color\" \"[1 1 1]\" }");
        }

        return fs;
    }

    [Fact]
    public async Task APreviewGoesBesideTheMapWithASheetAndAReport()
    {
        InMemoryFileSystem fs = await DiskAsync("/game/maps/room.bsp", game: true);
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(fs, [], ["/game/maps/room.bsp", "-size", "128x96", "-camera", "256 256 128 0 0 90 a look"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.NotNull(fs.GetBytes(At("/game/maps/room.preview/sheet.png")));
        byte[] json = fs.GetBytes(At("/game/maps/room.preview/index.json"))!;
        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement cameras = report.RootElement.GetProperty("cameras");
        Assert.Equal("a look", cameras[cameras.GetArrayLength() - 1].GetProperty("description").GetString());
        Assert.Empty(report.RootElement.GetProperty("checks").GetProperty("missing_materials").EnumerateArray());
        Assert.Contains("views", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndividualViewsGoToTheOutFolder()
    {
        InMemoryFileSystem fs = await DiskAsync("/game/maps/room.bsp", game: true);
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(
            fs, [], ["/game/maps/room.bsp", "-out", "/elsewhere", "-no-sheet", "-size", "96x72", "-hdr", "-tools", "-exposure", "1.5", "-threads", "2"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Null(fs.GetBytes(At("/elsewhere/sheet.png")));
        Assert.NotNull(fs.GetBytes(At("/elsewhere/views/01-overview.png")));
        Assert.NotNull(fs.GetBytes(At("/elsewhere/views/03-spawn1.png")));
    }

    [Fact]
    public async Task WithoutAGameEveryMaterialIsMissingButThePreviewStillRenders()
    {
        InMemoryFileSystem fs = await DiskAsync("/loose/room.bsp", game: false);
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(fs, [], ["/loose/room.bsp", "-size", "64x64", "-individual"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains("no -game", output.ToString(), StringComparison.Ordinal);
        Assert.NotNull(fs.GetBytes(At("/loose/room.preview/sheet.png")));
        Assert.NotNull(fs.GetBytes(At("/loose/room.preview/views/01-overview.png")));
    }

    /// <summary>
    /// A camera file gives one camera per line, numbered after the ones on
    /// the command line, skipping blank and <c>#</c> lines; a bad line or a
    /// missing file stops the run, saying which.
    /// </summary>
    [Fact]
    public async Task ACameraFileAddsItsCamerasAfterTheCommandLines()
    {
        InMemoryFileSystem fs = await DiskAsync("/game/maps/room.bsp", game: true);
        fs.AddText(Rooted("/cams.txt"), "# the room\n\n100 100 64 10 45 the corner\r\n200 200 64 0 180 70\n");
        fs.AddText(Rooted("/bad.txt"), "100 100 64 10 45\n1 2 3\n");
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(fs, [], ["/game/maps/room.bsp", "-camera", "50 50 64 0 0 first", "-cameras", "/cams.txt", "-size", "96x72"], output);
        int bad = await PreviewCommand.RunAsync(fs, [], ["/game/maps/room.bsp", "-cameras", "/bad.txt"], output);
        int missing = await PreviewCommand.RunAsync(fs, [], ["/game/maps/room.bsp", "-cameras", "/gone.txt"], output);

        Assert.True(exit == Program.ExitSuccess, output.ToString());
        using JsonDocument json = JsonDocument.Parse(fs.GetBytes(At("/game/maps/room.preview/index.json"))!);
        string[] custom = [.. json.RootElement.GetProperty("cameras").EnumerateArray()
            .Where(c => c.GetProperty("kind").GetString() == "custom")
            .Select(c => c.GetProperty("id").GetString() + ": " + c.GetProperty("description").GetString())];
        Assert.Equal(["cam1: first", "cam2: the corner", "cam3: custom at (200 200 64)"], custom);
        Assert.Equal(Program.ExitUsage, bad);
        Assert.Contains("line 2 is not", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Program.ExitUsage, missing);
        Assert.Contains("ssmap preview: " + Path.GetFullPath("/gone.txt") + ":", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACameraFileLineThatIsNotACameraIsReportedByNumber()
    {
        List<PreviewCamera> cameras = [];

        Assert.Null(PreviewCommand.AddCameras("# only a comment\n\n", cameras));
        Assert.Empty(cameras);
        Assert.Equal("line 3 is not \"x y z pitch yaw [fov] [label]\": 1 2", PreviewCommand.AddCameras("0 0 0 0 0\n#\n1 2\n", cameras));
        Assert.Single(cameras);
    }

    /// <summary>
    /// The scale figure is drawn on perspective views unless <c>-no-figure</c>
    /// asks otherwise; the overview, which has a scale bar, is the same
    /// either way.
    /// </summary>
    [Fact]
    public async Task NoFigureLeavesPerspectiveViewsClear()
    {
        InMemoryFileSystem fs = await DiskAsync("/game/maps/room.bsp", game: true);
        using StringWriter output = new();

        int with = await PreviewCommand.RunAsync(fs, [], ["/game/maps/room.bsp", "-out", "/with", "-no-sheet", "-size", "96x72"], output);
        int without = await PreviewCommand.RunAsync(fs, [], ["/game/maps/room.bsp", "-out", "/without", "-no-sheet", "-size", "96x72", "-no-figure"], output);

        Assert.Equal((Program.ExitSuccess, Program.ExitSuccess), (with, without));
        Assert.Equal(fs.GetBytes(At("/with/views/01-overview.png")), fs.GetBytes(At("/without/views/01-overview.png")));
        Assert.NotEqual(fs.GetBytes(At("/with/views/03-spawn1.png")), fs.GetBytes(At("/without/views/03-spawn1.png")));
    }

    [Theory]
    [InlineData]
    [InlineData("-size", "10x10", "m.bsp")]
    [InlineData("-camera", "1 2 3", "m.bsp")]
    [InlineData("-exposure", "-1", "m.bsp")]
    [InlineData("-threads", "0", "m.bsp")]
    [InlineData("m.bsp", "other.bsp")]
    [InlineData("-bogus", "m.bsp")]
    [InlineData("m.bsp", "-cameras")]
    public async Task BadArgumentsPrintTheUsage(params string[] args)
    {
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(new InMemoryFileSystem(), [], args, output);

        Assert.Equal(Program.ExitUsage, exit);
        Assert.StartsWith("usage: ssmap preview", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingOrBrokenMapFails()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Rooted("/maps/junk.bsp"), [1, 2, 3, 4]);
        using StringWriter output = new();

        Assert.Equal(RoomCommands.ExitFailed, await PreviewCommand.RunAsync(fs, [], ["/maps/absent.bsp"], output));
        Assert.Equal(RoomCommands.ExitFailed, await PreviewCommand.RunAsync(fs, [], ["/maps/junk.bsp"], output));
    }

    [Fact]
    public async Task AGameThatCannotMountFails()
    {
        InMemoryFileSystem fs = await DiskAsync("/loose/room.bsp", game: false);
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(fs, [], ["/loose/room.bsp", "-game", "/nowhere"], output);

        Assert.Equal(RoomCommands.ExitFailed, exit);
        Assert.Contains("cannot mount", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1 2 3 4 5", 90f, "custom at (1 2 3)")]
    [InlineData("1 2 3 4 5 60", 60f, "custom at (1 2 3)")]
    [InlineData("1 2 3 4 5 60 the label", 60f, "the label")]
    [InlineData("1 2 3 4 5 the label", 90f, "the label")]
    public void CamerasParsePositionAnglesFovAndLabel(string text, float fov, string description)
    {
        Assert.True(PreviewCommand.TryParseCamera(text, 3, out PreviewCamera? camera));
        Assert.Equal("cam3", camera!.Id);
        Assert.Equal(fov, camera.FovDegrees);
        Assert.Equal(description, camera.Description);
        Assert.Equal(1f, camera.Position.X);
        (float pitch, float yaw) = camera.Angles();
        Assert.Equal(4f, pitch, 3);
        Assert.Equal(5f, yaw, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 2 3 4")]
    [InlineData("1 2 x 4 5")]
    [InlineData("1 2 3 4 5 5")]
    [InlineData("1 2 3 4 5 175")]
    public void BadCamerasAreRefused(string text) =>
        Assert.False(PreviewCommand.TryParseCamera(text, 1, out _));

    [Theory]
    [InlineData("1024x768", true, 1024, 768)]
    [InlineData("64X64", true, 64, 64)]
    [InlineData("63x64", false, 0, 0)]
    [InlineData("4096x64", false, 0, 0)]
    [InlineData("100", false, 0, 0)]
    [InlineData("axb", false, 0, 0)]
    public void SizesAreBounded(string text, bool ok, int width, int height)
    {
        Assert.Equal(ok, PreviewCommand.TryParseSize(text, out (int Width, int Height) size));
        Assert.Equal((width, height), size);
    }

    [Theory]
    [InlineData("/steam/Team Fortress 2/tf", true)]
    [InlineData("/sdk/game/mod_tf/", true)]
    [InlineData("/sdk/game/mod_hl2mp", false)]
    [InlineData(null, false)]
    public void TfGameDirectoriesAreRecognised(string? directory, bool tf2) =>
        Assert.Equal(tf2, PreviewCommand.IsTf2(directory));
}
