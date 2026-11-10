//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.Json;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapPreview;
using SourceSharp.MapPreview.Overlays;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Report;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Movement;
using Xunit;

namespace SourceSharp.Tests.MapPreview;

/// <summary>
/// Facts for the movement analysis inside the preview: the reach overlay,
/// the report's movement section, the session's markers, and
/// <c>-movement</c> on the command line.
/// </summary>
public class MovementPreviewTests
{
    private static ReachabilityResult Result(Vec3[] nodes, Dictionary<int, bool[]> reached, NodeGroup[]? traps = null) =>
        new(nodes, [.. nodes.Select(_ => Array.Empty<int>())], reached, [], [], traps ?? []);

    private static (RgbaImage Image, CameraView View, GBuffer Buffer) TopDown()
    {
        PreviewCamera top = new("t", CameraKind.Overview, new Vec3(0, 0, 500), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 0, 100, 100, 100, "t");
        GBuffer buffer = new(100, 100);
        Array.Fill(buffer.Depth, 500f);
        return (RgbaImage.Filled(100, 100, new Rgba(0, 0, 0)), top.View(), buffer);
    }

    [Fact]
    public void DotsAreColouredByWhoReachesThem()
    {
        (RgbaImage image, CameraView view, GBuffer buffer) = TopDown();
        Vec3[] nodes = [new(-50, 0, 0), new(0, 0, 0), new(50, 0, 0), new(0, 50, 0)];
        Dictionary<int, bool[]> reached = new() { [2] = [true, true, false, true], [3] = [true, false, false, true] };
        NodeGroup trap = new([3], nodes[3], nodes[3], nodes[3]);

        int drawn = ReachOverlay.Draw(image, view, buffer, Result(nodes, reached, [trap]), 2);

        Assert.Equal(4, drawn);
        Rgba Expected(Rgba c) => new((byte)((c.R * ReachOverlay.Opacity + 50) / 100), (byte)((c.G * ReachOverlay.Opacity + 50) / 100), (byte)((c.B * ReachOverlay.Opacity + 50) / 100));
        Assert.Equal(Expected(ReachOverlay.AllTeams), image.Get(25, 50));
        Assert.Equal(Expected(ReachOverlay.SomeTeams), image.Get(50, 50));
        Assert.Equal(Expected(ReachOverlay.Unreached), image.Get(75, 50));
        Assert.Equal(Expected(ReachOverlay.Trap), image.Get(50, 25));
    }

    [Fact]
    public void HiddenOffscreenAndCutFloorIsNotDrawn()
    {
        (RgbaImage image, CameraView view, GBuffer buffer) = TopDown();
        buffer.Depth[(50 * 100) + 50] = 100f; // a roof over the middle
        Vec3[] nodes = [new(0, 0, 0), new(500, 0, 0), new(-40, 0, 300)];

        int drawn = ReachOverlay.Draw(image, view, buffer, Result(nodes, new() { [0] = [true, true, true] }), 1, cutAboveZ: 200);

        Assert.Equal(0, drawn);
    }

    [Fact]
    public async Task ASessionWithMovementAddsAReachTileMarkersAndAReport()
    {
        // A sealed room: one spawn stands on the floor, one is inside a pillar.
        BspData bsp = await PreviewTestMaps.RoomAsync(
            false,
            new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "128 128 16"), ("TeamNum", "2")]),
            new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "400 400 16"), ("TeamNum", "3")]),
            new PreviewTestMaps.TestEntity("func_detail", [], (new Vec3(360, 360, 0), new Vec3(440, 440, 200))));
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();

        PreviewResult result = await new PreviewSession(game).RenderAsync(bsp, "room", PreviewOptions.Default with
        {
            TileSize = (64, 48),
            Individual = true,
            ViewSize = (96, 72),
            Movement = MovementProfile.Tf2("soldier"),
            Reachability = ReachabilityOptions.Default with { Spacing = 64 },
        });

        Assert.Contains("views/01-reach.png", result.Images.Select(i => i.FileName));
        MovementReport movement = result.Report.Checks.Movement!;
        Assert.Equal("tf2_soldier", movement.Profile);
        Assert.True(movement.ReachedByTeam[2] > 0);
        Assert.Equal(0, movement.ReachedByTeam[3]);
        RegionReport stuck = Assert.Single(movement.StuckSpawns);
        Assert.Contains(result.Report.Markers, m => m.Number == stuck.Marker && m.Kind == "stuck_spawn");

        using JsonDocument json = JsonDocument.Parse(result.ReportJson());
        JsonElement section = json.RootElement.GetProperty("checks").GetProperty("movement");
        Assert.Equal("tf2_soldier", section.GetProperty("profile").GetString());
        Assert.Equal(stuck.Marker, section.GetProperty("stuck_spawns")[0].GetProperty("marker").GetInt32());
        Assert.True(section.GetProperty("reached_by_team").GetProperty("2").GetInt32() > 0);
    }

    [Fact]
    public async Task AnAirborneFuzzAddsItsSectionAndCanBeTurnedOff()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "128 128 16"), ("TeamNum", "2")]));
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();
        PreviewOptions options = PreviewOptions.Default with
        {
            TileSize = (64, 48),
            Movement = MovementProfile.Tf2("soldier"),
            Reachability = ReachabilityOptions.Default with { Spacing = 64 },
            Airborne = AirborneOptions.Default with { Trajectories = 200 },
        };

        PreviewResult with = await new PreviewSession(game).RenderAsync(bsp, "room", options);
        PreviewResult without = await new PreviewSession(game).RenderAsync(bsp, "room", options with { Airborne = null });

        AirborneReport air = with.Report.Checks.Movement!.Airborne!;
        Assert.Equal(200, air.Trajectories);
        Assert.All(air.Problems, p => Assert.NotEqual("ceiling_bonk", p.Kind));
        Assert.All(air.CeilingBonks, p => Assert.Equal("ceiling_bonk", p.Kind));
        using JsonDocument json = JsonDocument.Parse(with.ReportJson());
        JsonElement section = json.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("airborne");
        Assert.Equal(200, section.GetProperty("trajectories").GetInt32());
        Assert.Null(without.Report.Checks.Movement!.Airborne);
    }

    [Fact]
    public void SpotReportsWriteTheirSolidAndMarker()
    {
        PreviewReport report = new("m", 20, [], new SceneCounts(0, 0, 0, 0, 0, 0), [], [], [],
            new PreviewChecks([], [], new LightingReport(0, 0, 0, 0, 0, false), new SkyboxReport(null, 0), [], new SortedDictionary<string, int>(),
                new MovementReport("p", 1, new Dictionary<int, int> { [2] = 1 }, [],
                    [new RegionReport(5, [0, 0, 0], [0, 0, 0], [1, 1, 1], 1, ["rocket_jump"]), new RegionReport(3, [0, 0, 0], [0, 0, 0], [1, 1, 1], 2)], [], 0,
                    new AirborneReport(10, 3, [new SpotReport("edge_catch", 4, [1, 2, 3], ["rocket_jump"], [[0, 0, 0], [16, 16, 100]], 7)],
                        [new SpotReport("ceiling_bonk", 2, [0, 0, 0], ["sticky_jump"], null, 0)],
                        [new RegionReport(40, [5, 5, 300], [0, 0, 300], [64, 64, 300], 8)]))));

        using JsonDocument json = JsonDocument.Parse(PreviewReportWriter.Write(report));
        JsonElement air = json.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("airborne");

        Assert.Equal(3, air.GetProperty("explosive_only_nodes").GetInt32());
        JsonElement problem = air.GetProperty("problems")[0];
        Assert.Equal(7, problem.GetProperty("marker").GetInt32());
        Assert.Equal(100, problem.GetProperty("solid")[1][2].GetDouble());
        JsonElement bonk = air.GetProperty("ceiling_bonks")[0];
        Assert.False(bonk.TryGetProperty("solid", out _));
        Assert.False(bonk.TryGetProperty("marker", out _));
        JsonElement area = air.GetProperty("blast_only_areas")[0];
        Assert.Equal(40, area.GetProperty("nodes").GetInt32());
        Assert.Equal(8, area.GetProperty("marker").GetInt32());
        JsonElement traps = json.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("traps");
        Assert.Equal("rocket_jump", traps[0].GetProperty("escapes")[0].GetString());
        Assert.False(traps[1].TryGetProperty("escapes", out _));
    }

    [Fact]
    public void CollisionWarningsAreWrittenOnlyWhenThereAreAny()
    {
        static PreviewReport With(IReadOnlyList<string>? warnings) => new("m", 20, [], new SceneCounts(0, 0, 0, 0, 0, 0), [], [], [],
            new PreviewChecks([], [], new LightingReport(0, 0, 0, 0, 0, false), new SkyboxReport(null, 0), [], new SortedDictionary<string, int>(),
                new MovementReport("p", 1, new Dictionary<int, int>(), [], [], [], 0, null, warnings)));

        using JsonDocument json = JsonDocument.Parse(PreviewReportWriter.Write(With(["static prop model \"m.mdl\" ... non-solid"])));
        using JsonDocument none = JsonDocument.Parse(PreviewReportWriter.Write(With([])));
        using JsonDocument absent = JsonDocument.Parse(PreviewReportWriter.Write(With(null)));

        Assert.Equal("static prop model \"m.mdl\" ... non-solid",
            json.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("collision_warnings")[0].GetString());
        Assert.False(none.RootElement.GetProperty("checks").GetProperty("movement").TryGetProperty("collision_warnings", out _));
        Assert.False(absent.RootElement.GetProperty("checks").GetProperty("movement").TryGetProperty("collision_warnings", out _));
    }

    [Fact]
    public async Task TheCommandLinePrintsEachCollisionWarning()
    {
        static PreviewReport With(MovementReport? movement) => new("m", 20, [], new SceneCounts(0, 0, 0, 0, 0, 0), [], [], [],
            new PreviewChecks([], [], new LightingReport(0, 0, 0, 0, 0, false), new SkyboxReport(null, 0), [], new SortedDictionary<string, int>(), movement));
        using StringWriter two = new();
        using StringWriter none = new();

        await PreviewCommand.WriteWarningsAsync(two, With(new MovementReport("p", 1, new Dictionary<int, int>(), [], [], [], 0, null, ["a", "b"])));
        await PreviewCommand.WriteWarningsAsync(none, With(null));

        Assert.Equal($"ssmap preview: warning: a{Environment.NewLine}ssmap preview: warning: b{Environment.NewLine}", two.ToString());
        Assert.Empty(none.ToString());
    }

    /// <summary>
    /// A stuck spawn's marker says why: no floor under a clear box, or the
    /// box starts inside something, named when a prop stands there. The
    /// text was once "stands on no floor" for a spawn inside an ore cart.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 200, false, "spawn team 2 has no floor under it")]
    [InlineData(0, 0, 4, false, "spawn team 2 starts inside a brush")]
    [InlineData(0, 0, 4, true, "spawn team 2 starts inside the prop models/cart.mdl")]
    [InlineData(400, 0, 4, true, "spawn team 2 starts inside a brush")]
    public void AStuckSpawnSaysWhatItIsStuckIn(float x, float y, float z, bool cart, string text)
    {
        // A crate-sized solid at the origin and a slab far off; the cart
        // prop, when there, stands on the crate.
        HullTraceWorld world = new([
            NavBrush.Box(new Vec3(-40, -40, 0), new Vec3(40, 40, 64), 1),
            NavBrush.Box(new Vec3(360, -40, 0), new Vec3(440, 40, 64), 1),
        ]);
        PreviewProp[] props = cart ? [new PreviewProp("models/cart.mdl", new Vec3(8, 0, 0), Vec3.Zero, 0, -1, true)] : [];

        Assert.Equal(text, PreviewSession.StuckText(2, world, MovementProfile.Tf2("soldier"), new Vec3(x, y, z), props));
    }

    [Theory]
    [InlineData(null, "a player who gets here cannot get out (5 positions)")]
    [InlineData("", "a player who gets here cannot get out, not even by blast jumping (5 positions)")]
    [InlineData("rocket_jump,sticky_jump", "a player who gets here cannot walk or jump out (5 positions); rocket_jump, sticky_jump gets out")]
    public void ATrapMarkerSaysWhetherBlastJumpsGetOut(string? escapes, string text) =>
        Assert.Equal(text, PreviewSession.TrapText(5, escapes?.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void AnExitTriggerMakesATrapAPitAndIsWritten()
    {
        Assert.Equal("a pit no one walks or jumps out of (5 positions), left by its trigger: trigger_hurt hurts the player; trigger_teleport \"f\" teleports the player",
            PreviewSession.TrapText(5, ["rocket_jump"], ["trigger_hurt hurts the player", "trigger_teleport \"f\" teleports the player"]));
        Assert.Equal("a player who gets here cannot get out (5 positions)", PreviewSession.TrapText(5, null, []));

        PreviewReport report = new("m", 20, [], new SceneCounts(0, 0, 0, 0, 0, 0), [], [], [],
            new PreviewChecks([], [], new LightingReport(0, 0, 0, 0, 0, false), new SkyboxReport(null, 0), [], new SortedDictionary<string, int>(),
                new MovementReport("p", 1, new Dictionary<int, int>(), [],
                    [new RegionReport(5, [0, 0, 0], [0, 0, 0], [1, 1, 1], 1, null, ["trigger_hurt hurts the player"]), new RegionReport(3, [0, 0, 0], [0, 0, 0], [1, 1, 1], 2, null, [])], [], 0)));
        using JsonDocument json = JsonDocument.Parse(PreviewReportWriter.Write(report));
        JsonElement traps = json.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("traps");

        Assert.Equal("trigger_hurt hurts the player", traps[0].GetProperty("exits")[0].GetString());
        Assert.False(traps[1].TryGetProperty("exits", out _));
    }

    [Fact]
    public void BlastOnlyFloorIsDrawnBlue()
    {
        (RgbaImage image, CameraView view, GBuffer buffer) = TopDown();
        Vec3[] nodes = [new(0, 0, 0)];

        ReachOverlay.Draw(image, view, buffer, Result(nodes, new() { [2] = [false] }), 2, null, [0]);

        Rgba c = ReachOverlay.ExplosiveOnly;
        Assert.Equal(new Rgba((byte)((c.R * ReachOverlay.Opacity + 50) / 100), (byte)((c.G * ReachOverlay.Opacity + 50) / 100), (byte)((c.B * ReachOverlay.Opacity + 50) / 100)), image.Get(50, 50));
    }

    [Fact]
    public async Task WithoutMovementThereIsNoMovementSection()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "128 128 16")]));
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();

        PreviewResult result = await new PreviewSession(game).RenderAsync(bsp, "room", PreviewOptions.Default with { TileSize = (64, 48) });

        Assert.Null(result.Report.Checks.Movement);
        using JsonDocument json = JsonDocument.Parse(result.ReportJson());
        Assert.False(json.RootElement.GetProperty("checks").TryGetProperty("movement", out _));
    }

    [Theory]
    [InlineData(null, true, "tf2_soldier")]
    [InlineData(null, false, "hl2mp")]
    [InlineData("scout", false, "tf2_scout")]
    [InlineData("HL2MP", true, "hl2mp")]
    public void MovementProfilesFollowTheGameAndTheClass(string? cls, bool tf2, string name) =>
        Assert.Equal(name, PreviewCommand.MovementFor(cls, tf2).Name);

    [Fact]
    public async Task MovementOnTheCommandLineTakesAnOptionalClass()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "128 128 16"), ("TeamNum", "2")]));
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(bsp, bytes);
        InMemoryFileSystem fs = new();
        string map = VPath.Create(Path.GetFullPath("/maps/room.bsp")).Value;
        fs.AddFile(map, bytes.ToArray());
        using StringWriter output = new();

        int exit = await PreviewCommand.RunAsync(fs, [], ["/maps/room.bsp", "-movement", "heavy", "-size", "64x64", "-individual"], output);
        int plain = await PreviewCommand.RunAsync(fs, [], ["/maps/room.bsp", "-movement", "-out", "/second", "-size", "64x64", "-airborne", "0"], output);
        int flights = await PreviewCommand.RunAsync(fs, [], ["/maps/room.bsp", "-airborne", "50", "-out", "/third", "-size", "64x64"], output);
        int bad = await PreviewCommand.RunAsync(fs, [], ["/maps/room.bsp", "-airborne", "-5"], output);

        Assert.Equal(Program.ExitSuccess, exit);
        Assert.Equal(Program.ExitSuccess, plain);
        byte[] json = fs.GetBytes(VPath.Create(Path.GetFullPath("/maps/room.preview/index.json")))!;
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("tf2_heavy", doc.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("profile").GetString());
        Assert.NotNull(fs.GetBytes(VPath.Create(Path.GetFullPath("/maps/room.preview/views/01-reach.png"))));
        byte[] second = fs.GetBytes(VPath.Create(Path.GetFullPath("/second/index.json")))!;
        using JsonDocument doc2 = JsonDocument.Parse(second);
        Assert.Equal("hl2mp", doc2.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("profile").GetString());
        Assert.False(doc2.RootElement.GetProperty("checks").GetProperty("movement").TryGetProperty("airborne", out _));
        Assert.Equal(Program.ExitSuccess, flights);
        using JsonDocument doc3 = JsonDocument.Parse(fs.GetBytes(VPath.Create(Path.GetFullPath("/third/index.json")))!);
        Assert.Equal(50, doc3.RootElement.GetProperty("checks").GetProperty("movement").GetProperty("airborne").GetProperty("trajectories").GetInt32());
        Assert.Equal(Program.ExitUsage, bad);
    }
}
