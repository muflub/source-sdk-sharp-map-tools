//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Materials;
using SourceSharp.Tests.MapTools.Bsp;
using Xunit;

namespace SourceSharp.Tests.MapPreview.Scene;

/// <summary>
/// Facts for <see cref="BspSceneBuilder"/> and the lighting it reads
/// (<see cref="AmbientLight"/>, <see cref="LocalLights"/>), over a room the
/// in-process vbsp compiles.
/// </summary>
public class BspSceneBuilderTests
{
    private static readonly PreviewTestMaps.TestEntity Spawn = new(
        "info_player_teamspawn", [("origin", "128 256 16"), ("angles", "0 90 0"), ("TeamNum", "2")]);

    private static readonly PreviewTestMaps.TestEntity Door = new(
        "func_brush", [("targetname", "door1")], (new Vec3(240, 240, 0), new Vec3(272, 272, 96)));

    private static readonly PreviewTestMaps.TestEntity Trigger = new(
        "trigger_multiple", [], (new Vec3(300, 300, 0), new Vec3(340, 340, 64)), "unit/trigger");

    private static readonly PreviewTestMaps.TestEntity Barrel = new(
        "prop_dynamic", [("origin", "400 100 0"), ("model", "models/Missing/Barrel.MDL"), ("skin", "2")]);

    [Fact]
    public async Task TheRoomsInsideFacesFaceInward()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Spawn);

        PreviewScene scene = BspSceneBuilder.Build(bsp);

        Vec3 centre = new(256, 256, 128);
        List<PreviewTriangle> world = [.. scene.Triangles.Where(t => scene.Surfaces[t.Surface].Kind == SurfaceKind.World)];
        Assert.NotEmpty(world);
        Assert.All(world, t => Assert.True(Vec3.Dot(t.Normal, centre - scene.Vertices[t.A].Position) > 0, $"triangle facing {t.Normal}"));
    }

    [Fact]
    public async Task SpawnsCarryTheirTeamAndFacing()
    {
        PreviewScene scene = BspSceneBuilder.Build(await PreviewTestMaps.RoomAsync(false, Spawn));

        PreviewSpawn spawn = Assert.Single(scene.Spawns);
        Assert.Equal(2, spawn.Team);
        Assert.Equal(90f, spawn.Yaw);
        Assert.Equal(new Vec3(128, 256, 16), spawn.Origin);
        Assert.Equal("sky_test", scene.SkyName);
    }

    [Fact]
    public async Task BrushEntitiesDrawAndTriggersAreHiddenUnlessToolsAreAsked()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Door, Trigger);

        PreviewScene plain = BspSceneBuilder.Build(bsp);
        PreviewScene tools = BspSceneBuilder.Build(bsp, options: new PreviewSceneOptions { IncludeTools = true });

        int Count(PreviewScene s, string cls) => s.Surfaces.Count(x => x.Kind == SurfaceKind.BrushEntity && s.Entities[x.Entity].ClassName == cls);
        Assert.True(Count(plain, "func_brush") > 0);
        Assert.Equal(0, Count(plain, "trigger_multiple"));
        Assert.True(Count(tools, "trigger_multiple") > 0);
    }

    [Fact]
    public async Task SkyFacesAreMarkedSky()
    {
        PreviewScene scene = BspSceneBuilder.Build(await PreviewTestMaps.RoomAsync(true));

        Assert.Contains(scene.Surfaces, s => s.Sky);
        Assert.All(scene.Surfaces.Where(s => s.Sky), s => Assert.Equal(-1, s.Lightmap));
    }

    [Fact]
    public async Task AModelThatDidNotLoadBecomesAStandInBox()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Barrel);

        Assert.Equal(["models/missing/barrel.mdl"], BspSceneBuilder.ModelNames(bsp));
        PreviewScene scene = BspSceneBuilder.Build(bsp);

        PreviewProp prop = Assert.Single(scene.Props);
        Assert.False(prop.Loaded);
        Assert.Equal(2, prop.Skin);
        PreviewSurface box = Assert.Single(scene.Surfaces, s => s.Kind == SurfaceKind.MissingModel);
        Assert.Equal(BspSceneBuilder.MissingModelMaterial, scene.MaterialNames[box.Material]);
        Assert.Equal(12, scene.Triangles.Count(t => t.Surface == Array.IndexOf(scene.Surfaces, box)));
    }

    [Fact]
    public async Task ALoadedModelIsPlacedSkinnedAndLit()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, Barrel);
        StudioTriangle t = new(new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 10, 0), new Vec3(0, 0, 1), new Vec3(0, 0, 1), new Vec3(0, 0, 1), (0, 0), (1, 0), (0, 1), 0);
        PreviewModel model = new("models/missing/barrel.mdl", [t], [["skin0/a"], ["skin1/a"], ["skin2/a"]], new Vec3(-5, -5, 0), new Vec3(5, 5, 20));

        PreviewScene scene = BspSceneBuilder.Build(bsp, new Dictionary<string, PreviewModel> { [model.Path] = model });

        Assert.True(Assert.Single(scene.Props).Loaded);
        PreviewSurface surface = Assert.Single(scene.Surfaces, s => s.Kind == SurfaceKind.PropEntity);
        Assert.Equal("skin2/a", scene.MaterialNames[surface.Material]);
        PreviewTriangle placed = Assert.Single(scene.Triangles, x => scene.Surfaces[x.Surface].Kind == SurfaceKind.PropEntity);
        Assert.Equal(new Vec3(400, 100, 0), scene.Vertices[placed.A].Position);
        Assert.Equal(new Vec3(0, 0, 1), placed.Normal);
        Assert.Equal(AmbientLight.Fallback, scene.Vertices[placed.A].Light);
    }

    [Fact]
    public async Task AFaceSumsItsLightStyles()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false);
        PreviewTestMaps.Light(bsp, PreviewTestMaps.Sample(255), PreviewTestMaps.Sample(51));

        PreviewScene scene = BspSceneBuilder.Build(bsp);

        PreviewSurface lit = scene.Surfaces.First(s => s.Lightmap >= 0);
        Vec3 sample = scene.Lightmaps[lit.Lightmap].Samples[0];
        Assert.Equal(1.2f, sample.X, 4);
    }

    [Fact]
    public async Task ABumpLitFaceReadsOnlyItsFlatMapPerStyle()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false);
        int face = 0;
        PreviewTestMaps.Light(bsp, PreviewTestMaps.Sample(255), PreviewTestMaps.Sample(51), bumpedFace: face);

        PreviewScene scene = BspSceneBuilder.Build(bsp);

        PreviewSurface bumped = scene.Surfaces.First(s => s.Face == face);
        Assert.All(scene.Lightmaps[bumped.Lightmap].Samples, s => Assert.Equal(1.2f, s.X, 4));
    }

    [Fact]
    public async Task HdrIsReadWhenAskedAndPresentElseLdr()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false);
        PreviewTestMaps.Light(bsp, PreviewTestMaps.Sample(255));

        PreviewScene ldr = BspSceneBuilder.Build(bsp, options: new PreviewSceneOptions { Hdr = true });
        Assert.Contains(ldr.Surfaces, s => s.Lightmap >= 0);

        bsp.SetLump(BspLump.LightingHdr, bsp[BspLump.Lighting].Data);
        bsp.SetLump(BspLump.Lighting, ReadOnlyMemory<byte>.Empty);
        PreviewScene hdrOnly = BspSceneBuilder.Build(bsp);
        Assert.Contains(hdrOnly.Surfaces, s => s.Lightmap >= 0);
    }

    [Fact]
    public async Task AnUnlitMapHasNoLightmaps()
    {
        PreviewScene scene = BspSceneBuilder.Build(await PreviewTestMaps.RoomAsync(false));
        Assert.Empty(scene.Lightmaps);
    }

    [Fact]
    public async Task AmbientCubesComeFromTheLeafHoldingThePoint()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false);
        int leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).Length;
        DLeafAmbientIndex[] index = new DLeafAmbientIndex[leaves];
        DLeafAmbientLighting[] samples = new DLeafAmbientLighting[leaves];
        for (int i = 0; i < leaves; i++)
        {
            index[i] = new DLeafAmbientIndex { AmbientSampleCount = 1, FirstAmbientSample = (ushort)i };
            for (int f = 0; f < 6; f++)
            {
                samples[i].Cube.Color[f] = PreviewTestMaps.Sample((byte)(f * 40 + 10));
            }
        }

        bsp.SetLump(BspLump.LeafAmbientIndex, MemoryMarshal.AsBytes(index.AsSpan()).ToArray());
        bsp.SetLump(BspLump.LeafAmbientLighting, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
        AmbientLight ambient = AmbientLight.FromBsp(bsp, hdr: false);

        Vec3[]? cube = ambient.CubeAt(new Vec3(256, 256, 128));

        Assert.NotNull(cube);
        Assert.Equal(10f / 255f, cube![0].X, 5);
        Assert.Equal(210f / 255f, cube[5].X, 5);
        Assert.True(ambient.LeafOf(new Vec3(256, 256, 128)) >= 0);
        Assert.True(ambient.AreaOf(new Vec3(256, 256, 128)) >= 0);
    }

    [Fact]
    public async Task AMapWithNoAmbientDataHasNoCube()
    {
        AmbientLight ambient = AmbientLight.FromBsp(await PreviewTestMaps.RoomAsync(false), hdr: true);
        Assert.Null(ambient.CubeAt(new Vec3(256, 256, 128)));
    }

    [Fact]
    public void ACubeIsWeightedByTheNormalSquared()
    {
        Vec3[] cube = [new(1, 0, 0), new(2, 0, 0), new(0, 1, 0), new(0, 2, 0), new(0, 0, 1), new(0, 0, 2)];

        Assert.Equal(new Vec3(1, 0, 0), AmbientLight.Evaluate(cube, new Vec3(1, 0, 0)));
        Assert.Equal(new Vec3(0, 0, 2), AmbientLight.Evaluate(cube, new Vec3(0, 0, -1)));
        Vec3 diagonal = AmbientLight.Evaluate(cube, new Vec3(0.70710677f, -0.70710677f, 0));
        Assert.Equal(0.5f, diagonal.X, 4);
        Assert.Equal(1.0f, diagonal.Y, 4);
    }

    [Fact]
    public async Task LocalLightsFallOffAndRespectSpotCones()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false);
        DWorldLight point = new() { Origin = new Vec3(0, 0, 100), Intensity = new Vec3(10000, 10000, 10000), Type = (int)EmitType.Point, QuadraticAttn = 1 };
        DWorldLight spot = new()
        {
            Origin = new Vec3(500, 0, 100), Intensity = new Vec3(10000, 10000, 10000), Type = (int)EmitType.Spotlight, QuadraticAttn = 1,
            Normal = new Vec3(0, 0, -1), StopDot = 0.9f, StopDot2 = 0.8f, Exponent = 1,
        };
        DWorldLight surface = new() { Origin = new Vec3(0, 0, 50), Intensity = new Vec3(1, 1, 1), Type = (int)EmitType.Surface };
        bsp.SetLump(BspLump.WorldLights, MemoryMarshal.AsBytes(new[] { point, spot, surface }.AsSpan()).ToArray());
        LocalLights lights = LocalLights.FromBsp(bsp, hdr: false);

        Assert.Equal(2, lights.Count);
        int[] picked = lights.Pick(new Vec3(0, 0, 0));
        Assert.Equal(0, picked[0]);
        Vec3 under = lights.At(picked, Vec3.Zero, new Vec3(0, 0, 1));
        Assert.Equal(1f, under.X, 3);                     // 10000 / 100²
        Assert.Equal(Vec3.Zero, lights.At(picked, Vec3.Zero, new Vec3(0, 0, -1)));
        Assert.Equal(1f, lights.At([1], new Vec3(500, 0, 0), new Vec3(0, 0, 1)).X, 3);
        Assert.Equal(Vec3.Zero, lights.At([1], new Vec3(600, 0, 0), new Vec3(0, 0, 1)));
    }

    [Fact]
    public async Task ARadiusCutsALightOff()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false);
        DWorldLight point = new() { Origin = new Vec3(0, 0, 100), Intensity = new Vec3(100, 100, 100), Type = (int)EmitType.Point, ConstantAttn = 1, Radius = 50 };
        bsp.SetLump(BspLump.WorldLightsHdr, MemoryMarshal.AsBytes(new[] { point }.AsSpan()).ToArray());
        LocalLights lights = LocalLights.FromBsp(bsp, hdr: true);

        Assert.Empty(lights.Pick(Vec3.Zero));
        Assert.Equal(100f, lights.At([0], new Vec3(0, 0, 60), new Vec3(0, 0, 1)).X, 3);
    }

    [Theory]
    [InlineData("1 2 3", 1, 2, 3)]
    [InlineData("  4.5   -6  ", 4.5f, -6, 0)]
    [InlineData("x y z", 0, 0, 0)]
    [InlineData(null, 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    public void EntityVectorsParseLeniently(string? text, float x, float y, float z) =>
        Assert.Equal(new Vec3(x, y, z), BspSceneBuilder.ParseVector(text));

    [Theory]
    [InlineData("func_brush", null, true)]
    [InlineData("func_door", null, true)]
    [InlineData("trigger_hurt", null, false)]
    [InlineData("func_respawnroom", null, false)]
    [InlineData("func_brush", "10", false)]
    public void OnlyDrawnBrushEntitiesAreVisible(string classname, string? rendermode, bool visible)
    {
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", classname));
        if (rendermode is not null)
        {
            entity.Pairs.Add(new BspKeyValue("rendermode", rendermode));
        }

        Assert.Equal(visible, BspSceneBuilder.IsVisibleBrushEntity(entity));
    }

    [Fact]
    public void ALightmapSamplesBilinearlyAndClampsAtItsEdges()
    {
        PreviewLightmap lightmap = new(2, 2, [new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0)]);

        Assert.Equal(new Vec3(0.5f, 0.5f, 0), lightmap.Sample(0.5f, 0.5f));
        Assert.Equal(new Vec3(0, 0, 0), lightmap.Sample(-5, -5));
        Assert.Equal(new Vec3(1, 1, 0), lightmap.Sample(9, 9));
    }

    [Fact]
    public void ModelMaterialsFallBackToSkinZero()
    {
        PreviewModel model = new("m", [], [["a", "b"], ["c"]], Vec3.Zero, Vec3.Zero);

        Assert.Equal("c", model.MaterialFor(1, 0));
        Assert.Equal("b", model.MaterialFor(7, 1));
        Assert.Null(model.MaterialFor(1, 5));
        Assert.Null(model.MaterialFor(0, -1));
        Assert.Equal("models/a/b.mdl", PreviewModel.Key(" Models\\A\\B.MDL "));
    }
}
