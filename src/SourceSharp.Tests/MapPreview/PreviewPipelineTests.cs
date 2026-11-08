//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;
using System.Text.Json;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview;
using SourceSharp.MapPreview.Cameras;
using SourceSharp.MapPreview.Overlays;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Report;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using Xunit;

namespace SourceSharp.Tests.MapPreview;

/// <summary>
/// Facts for the camera planner, the overlays and contact sheet, the report,
/// and <see cref="PreviewSession"/> end to end over a compiled room.
/// </summary>
public class PreviewPipelineTests
{
    private static readonly PreviewTestMaps.TestEntity Red = new("info_player_teamspawn", [("origin", "128 128 16"), ("angles", "0 45 0"), ("TeamNum", "2")]);
    private static readonly PreviewTestMaps.TestEntity Red2 = new("info_player_teamspawn", [("origin", "160 128 16"), ("TeamNum", "2")]);
    private static readonly PreviewTestMaps.TestEntity Blue = new("info_player_teamspawn", [("origin", "384 384 16"), ("angles", "0 225 0"), ("TeamNum", "3")]);
    private static readonly PreviewTestMaps.TestEntity Barrel = new("prop_dynamic", [("origin", "256 256 0"), ("model", "models/lost.mdl")]);

    private static PreviewSpawn Spawn(int team, float x) => new("info_player_teamspawn", team, new Vec3(x, 0, 0), 0);

    [Fact]
    public void SpawnsArePickedOnePerTeamFirst()
    {
        PreviewSpawn[] spawns = [Spawn(2, 0), Spawn(2, 1), Spawn(2, 2), Spawn(3, 3)];

        IReadOnlyList<PreviewSpawn> two = CameraPlanner.PickSpawns(spawns, 2);
        IReadOnlyList<PreviewSpawn> three = CameraPlanner.PickSpawns(spawns, 3);

        Assert.Equal([0f, 3f], two.Select(s => s.Origin.X));
        Assert.Equal([0f, 3f, 1f], three.Select(s => s.Origin.X));
        Assert.Empty(CameraPlanner.PickSpawns([], 4));
    }

    [Fact]
    public async Task ThePlanIsOverviewIsometricThenSpawnsWithACutawayAboveTheHighestSpawn()
    {
        PreviewScene scene = BspSceneBuilder.Build(await PreviewTestMaps.RoomAsync(true, Red, Blue));

        IReadOnlyList<PlannedView> plan = CameraPlanner.Plan(scene, CameraPlanOptions.Default with { EyeHeight = 72 });

        Assert.Equal([CameraKind.Overview, CameraKind.Isometric, CameraKind.Spawn, CameraKind.Spawn], plan.Select(p => p.Camera.Kind));
        Assert.Equal(216f, plan[0].Filter.CutAboveZ);
        Assert.Null(plan[1].Filter.CutAboveZ);
        Assert.Equal(new Vec3(128, 128, 88), plan[2].Camera.Position);
        Assert.Contains("red", plan[2].Camera.Description, StringComparison.Ordinal);
        Assert.Contains("blue", plan[3].Camera.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentBoundsLeaveOutTheSkyShell()
    {
        PreviewScene scene = BspSceneBuilder.Build(await PreviewTestMaps.RoomAsync(true, Red));

        (Vec3 min, Vec3 max) = CameraPlanner.ContentBounds(scene);

        Assert.True(max.Z <= 256f, $"max z {max.Z}");
        Assert.True(min.X >= 0f && max.X <= 512f);
        PreviewScene empty = PreviewTestMaps.Scene([], 0);
        Assert.Equal((empty.Min, empty.Max), CameraPlanner.ContentBounds(empty));
    }

    [Fact]
    public void TheOverviewFramesTheBoundsNorthUp()
    {
        PreviewCamera camera = CameraPlanner.Overview(new Vec3(0, 0, 0), new Vec3(400, 200, 100), 400, 300, null);

        Assert.Equal(new Vec3(200, 100, 164), camera.Position);
        Assert.Equal(150f * CameraPlanner.FrameMargin, camera.OrthoHalfHeight, 3);
        Assert.Equal("overview top-down", camera.Description);
    }

    [Fact]
    public void TheIsometricViewFitsEveryCorner()
    {
        Vec3 min = new(0, 0, 0), max = new(1000, 500, 300);
        PreviewCamera camera = CameraPlanner.Isometric(min, max, 320, 240);
        CameraView view = camera.View();

        for (int corner = 0; corner < 8; corner++)
        {
            Vec3 p = new((corner & 1) != 0 ? max.X : min.X, (corner & 2) != 0 ? max.Y : min.Y, (corner & 4) != 0 ? max.Z : min.Z);
            (float x, float y, float _) = view.Project(p)!.Value;
            Assert.InRange(x, 0, 320);
            Assert.InRange(y, 0, 240);
        }
    }

    [Theory]
    [InlineData(1f, 1024, 256)]
    [InlineData(8f, 1024, 2048)]
    [InlineData(0.01f, 256, 16)]
    public void TheScaleBarIsTheLongestRoundLengthInAQuarterOfTheWidth(float unitsPerPixel, int width, int units)
    {
        (int found, int pixels) = Annotator.BarLength(unitsPerPixel, width);
        Assert.Equal(units, found);
        Assert.Equal(Math.Max(1, (int)((units / unitsPerPixel) + 0.5f)), pixels);
    }

    [Fact]
    public void MarkersAreDrawnWhereTheyProjectAndGreyedWhenHidden()
    {
        PreviewCamera camera = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, 0, 0, 90, 64, 64, "c");
        GBuffer buffer = new(64, 64);
        Array.Fill(buffer.Depth, 50f);
        RgbaImage image = RgbaImage.Filled(64, 64, new Rgba(0, 0, 0));
        PreviewMarker visible = new(1, "k", "near", new Vec3(40, 0, 0));
        PreviewMarker hidden = new(2, "k", "far", new Vec3(400, 100, 100));
        PreviewMarker behind = new(3, "k", "behind", new Vec3(-40, 0, 0));
        PreviewMarker outside = new(4, "k", "outside", new Vec3(10, 100, 0));

        IReadOnlyList<int> drawn = Annotator.Markers(image, camera.View(), buffer, [visible, hidden, behind, outside], 1);

        Assert.Equal([1, 2], drawn);
        Assert.Equal(Annotator.MarkerColour, image.Get(32, 32));
        Assert.Equal(Annotator.HiddenMarkerColour, image.Get(24, 24));
    }

    [Fact]
    public void TheScaleFigureIsHiddenBehindTheScene()
    {
        PreviewCamera camera = PreviewCamera.FromAngles("c", CameraKind.Custom, new Vec3(0, 0, 72), 0, 0, 90, 64, 64, "c");
        GBuffer open = new(64, 64);
        GBuffer wall = new(64, 64);
        Array.Fill(wall.Depth, 10f);
        RgbaImage a = RgbaImage.Filled(64, 64, new Rgba(0, 0, 0));
        RgbaImage b = RgbaImage.Filled(64, 64, new Rgba(0, 0, 0));

        Annotator.ScaleFigure(a, camera.View(), open, new Vec3(128, 0, 0), 48, 82);
        Annotator.ScaleFigure(b, camera.View(), wall, new Vec3(128, 0, 0), 48, 82);

        Assert.Contains(Annotator.FigureColour, Enumerable.Range(0, 64 * 64).Select(i => a.Get(i % 64, i / 64)));
        Assert.DoesNotContain(Annotator.FigureColour, Enumerable.Range(0, 64 * 64).Select(i => b.Get(i % 64, i / 64)));
    }

    [Fact]
    public void LabelsScaleBarsAndLinesDrawInsideTheImage()
    {
        RgbaImage image = RgbaImage.Filled(200, 100, new Rgba(0, 0, 0));
        PreviewCamera top = new("t", CameraKind.Overview, new Vec3(0, 0, 100), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 0, 50, 200, 100, "t");

        Annotator.Label(image, "HELLO", 1);
        Annotator.ScaleBar(image, top.View(), 1);
        Annotator.Line(image, -10, -10, 300, 300, new Rgba(1, 2, 3));

        Assert.Equal(Annotator.LabelBacking, image.Get(1, 8));
        Assert.Equal(new Rgba(1, 2, 3), image.Get(50, 50));
    }

    [Fact]
    public void TheContactSheetLaysTilesOutInRows()
    {
        RgbaImage[] tiles = [.. Enumerable.Range(0, 4).Select(i => RgbaImage.Filled(10, 5, new Rgba((byte)(i + 1), 0, 0)))];

        RgbaImage sheet = ContactSheet.Compose(tiles, 3);

        Assert.Equal(ContactSheet.Gap + (3 * (10 + ContactSheet.Gap)), sheet.Width);
        Assert.Equal(ContactSheet.Gap + (2 * (5 + ContactSheet.Gap)), sheet.Height);
        Assert.Equal(4, sheet.Get(ContactSheet.Gap, ContactSheet.Gap + 5 + ContactSheet.Gap).R);
        Assert.Equal(ContactSheet.Border, sheet.Get(0, 0));
        Assert.Throws<ArgumentException>(() => ContactSheet.Compose([], 3));
        Assert.Throws<ArgumentException>(() => ContactSheet.Compose([tiles[0], RgbaImage.Filled(3, 3, default)], 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactSheet.Compose(tiles, 0));
    }

    [Fact]
    public async Task ASessionRendersARoomWithFindingsAndAReport()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(true, Red, Red2, Blue, Barrel);
        PreviewTestMaps.Light(bsp, PreviewTestMaps.Sample(255));
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync(("materials/unit/plain.vmt", "\"LightmappedGeneric\" { \"$color\" \"[1 1 1]\" }"u8.ToArray()));
        PreviewCamera extra = PreviewCamera.FromAngles("cam1", CameraKind.Custom, new Vec3(256, 256, 128), 10, 0, 90, 1, 1, "custom");

        PreviewResult result = await new PreviewSession(game).RenderAsync(
            bsp, "room", PreviewOptions.Default with { TileSize = (64, 48), ExtraCameras = [extra] });

        Assert.Equal("sheet.png", result.Images[0].FileName);
        Assert.Single(result.Images);
        PreviewReport report = result.Report;
        Assert.Equal(["overview", "iso", "spawn1", "spawn2", "spawn3", "cam1"], report.Cameras.Select(c => c.Id));
        Assert.Equal([64, 48], report.Cameras[5].Size);
        MissingModelReport missing = Assert.Single(report.Checks.MissingModels);
        Assert.Equal("models/lost.mdl", missing.Model);
        Assert.Contains(report.Markers, m => m.Number == missing.Marker && m.Kind == "missing_model");
        Assert.Equal(3, report.Checks.Spawns.Count);
        Assert.Equal(3, report.Checks.EntityClasses["info_player_teamspawn"]);
        Assert.True(report.Checks.Lighting.Luxels > 0);
        Assert.Equal(1f, report.Checks.Lighting.Mean, 2);
        Assert.Equal("sky_test", report.Checks.Skybox.Name);
        Assert.Equal(0, report.Checks.Skybox.FacesLoaded);
    }

    [Fact]
    public async Task IndividualViewsAreWrittenAndTheSheetIsScaledDown()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Red);
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();

        PreviewResult result = await new PreviewSession(game).RenderAsync(
            bsp, "room", PreviewOptions.Default with { Individual = true, ViewSize = (96, 72), TileSize = (48, 36) });

        Assert.Equal(["sheet.png", "views/01-overview.png", "views/02-iso.png", "views/03-spawn1.png"], result.Images.Select(i => i.FileName));
        Assert.True(result.Report.Checks.MissingMaterials.Count > 0); // the room's materials are not in this content
    }

    [Fact]
    public async Task ARenderIsByteForByteRepeatable()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(true, Red, Barrel);
        PreviewTestMaps.Light(bsp, PreviewTestMaps.Sample(200, -2));
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();
        PreviewOptions options = PreviewOptions.Default with { TileSize = (80, 60) };

        PreviewResult a = await new PreviewSession(game).RenderAsync(bsp, "room", options);
        PreviewResult b = await new PreviewSession(game).RenderAsync(bsp, "room", options with { Render = RenderSettings.Default with { MaxDegreeOfParallelism = 1 } });

        Assert.Equal(a.Images[0].Png, b.Images[0].Png);
        Assert.Equal(a.ReportJson(), b.ReportJson());
    }

    [Fact]
    public async Task AMapsPakIsSearchedBeforeTheGame()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Red);
        bsp.SetLump(BspLump.PakFile, PakWith("materials/unit/plain.vmt", "\"UnlitGeneric\" { \"$color\" \"[1 0 0]\" }"));
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();

        PreviewResult result = await new PreviewSession(game).RenderAsync(bsp, "room", PreviewOptions.Default with { TileSize = (32, 24) });

        Assert.DoesNotContain(result.Report.Checks.MissingMaterials, m => m.Name == "unit/plain");
    }

    [Fact]
    public async Task ACancelledRenderThrowsAndLeavesNothingBehind()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Red);
        await using ContentFileSystem game = await PreviewTestMaps.ContentAsync();
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PreviewSession(game).RenderAsync(bsp, "room", null, cancel.Token));
    }

    [Fact]
    public void TheReportIsSnakeCaseJsonInAFixedOrder()
    {
        PreviewReport report = new(
            "m", 20, [[0, 0, 0], [1.234f, 2, 3]], new SceneCounts(1, 2, 3, 4, 5, 6), ["sheet.png"],
            [new CameraReport("c", "custom", "d", [1, 2, 3], 4, 5, 90, null, [8, 6], [new EntityRef(1, "light", null), new EntityRef(2, "door", "d1")],
                [new ModelCount("models/a.mdl", 2)], [1], new LuminanceReport(0.5f, 0.1f, 0.9f, 0.2f, 0.3f))],
            [new MarkerReport(1, "missing_model", "t", [1, 2, 3], ["c"])],
            new PreviewChecks([new MissingMaterialReport("x", "missing_material", 3, 1)], [new MissingModelReport("y", "r", 2, 1)],
                new LightingReport(10, 0.5f, 0.1f, 0.2f, 1, false), new SkyboxReport(null, 0),
                [new SpawnReport("info_player_start", 0, [0, 0, 0], 90)], new SortedDictionary<string, int> { ["light"] = 1 }));

        byte[] json = PreviewReportWriter.Write(report);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal((byte)'\n', json[^1]);
        Assert.Equal(["map", "bsp_version", "bounds", "counts", "images", "cameras", "markers", "checks"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1.23, root.GetProperty("bounds")[1][0].GetDouble(), 3);
        JsonElement camera = root.GetProperty("cameras")[0];
        Assert.Equal(90, camera.GetProperty("fov").GetDouble());
        Assert.False(camera.TryGetProperty("ortho_half_height", out _));
        Assert.False(camera.GetProperty("visible_entities")[0].TryGetProperty("targetname", out _));
        Assert.Equal("d1", camera.GetProperty("visible_entities")[1].GetProperty("targetname").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("checks").GetProperty("skybox").GetProperty("name").ValueKind);
        Assert.Equal(1, root.GetProperty("checks").GetProperty("entity_classes").GetProperty("light").GetInt32());
    }

    [Fact]
    public void AnOrthographicCameraReportsItsHalfHeightNotAFieldOfView()
    {
        PreviewReport report = new("m", 20, [], new SceneCounts(0, 0, 0, 0, 0, 0), [],
            [new CameraReport("o", "overview", "d", [0, 0, 0], 90, 0, null, 512, [8, 6], [], [], [], new LuminanceReport(0, 0, 0, 0, 0))],
            [], new PreviewChecks([], [], new LightingReport(0, 0, 0, 0, 0, true), new SkyboxReport("s", 6), [], new SortedDictionary<string, int>()));

        using JsonDocument doc = JsonDocument.Parse(PreviewReportWriter.Write(report));
        JsonElement camera = doc.RootElement.GetProperty("cameras")[0];

        Assert.False(camera.TryGetProperty("fov", out _));
        Assert.Equal(512, camera.GetProperty("ortho_half_height").GetDouble());
        Assert.Equal("s", doc.RootElement.GetProperty("checks").GetProperty("skybox").GetProperty("name").GetString());
    }

    /// <summary>A stored (uncompressed) zip with one file, as a map's pak lump holds it.</summary>
    private static byte[] PakWith(string name, string text)
    {
        using MemoryStream stream = new();
        using (System.IO.Compression.ZipArchive zip = new(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
            using Stream body = entry.Open();
            body.Write(Encoding.ASCII.GetBytes(text));
        }

        return stream.ToArray();
    }
}
