//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Materials;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Materials;
using Xunit;

namespace SourceSharp.Tests.MapPreview.Render;

/// <summary>
/// Facts for <see cref="SceneRenderer"/>: where each surface's light comes
/// from, the sky, blending and alpha test, what views leave out, and what the
/// rendered view reports.
/// </summary>
public class SceneRendererTests
{
    private static readonly Rgba White = new(255, 255, 255);
    private static readonly PreviewSky NoSky = new(new RgbaImage?[6], new Rgba(10, 20, 30));
    private static readonly ColorSpace Colors = new();

    private static PreviewCamera Forward(int size = 16) =>
        PreviewCamera.FromAngles("t", CameraKind.Custom, Vec3.Zero, 0, 0, 90, size, size, "t");

    private static Rgba Centre(RenderedView view) => view.Image.Get(view.Image.Width / 2, view.Image.Height / 2);

    private static RenderedView Render(PreviewScene scene, PreviewMaterial[] materials, PreviewCamera? camera = null, PreviewSky? sky = null, ViewFilter filter = default, RenderSettings? settings = null) =>
        new SceneRenderer(scene, materials, sky ?? NoSky, Colors).Render(camera ?? Forward(), filter, settings ?? RenderSettings.Default, CancellationToken.None);

    [Fact]
    public void AVertexLitSurfaceIsTexelTimesVertexLight()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, new Vec3(0.5f, 0.5f, 0.5f))], 1);

        Rgba pixel = Centre(Render(scene, [PreviewTestMaps.Flat(White)]));

        Assert.Equal(Colors.ToSrgb(0.5f), pixel.R);
    }

    [Fact]
    public void ALightmappedSurfaceUsesItsLightmapNotItsVertices()
    {
        PreviewLightmap lightmap = new(2, 2, [new Vec3(0.25f, 0.25f, 0.25f), new Vec3(0.25f, 0.25f, 0.25f), new Vec3(0.25f, 0.25f, 0.25f), new Vec3(0.25f, 0.25f, 0.25f)]);
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, new Vec3(1, 1, 1), lightmap: 0)], 1, [lightmap]);

        Assert.Equal(Colors.ToSrgb(0.25f), Centre(Render(scene, [PreviewTestMaps.Flat(White)])).R);
    }

    [Fact]
    public void AnUnlitMaterialShowsItsTexelAsIs()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Vec3.Zero)], 1);

        Assert.Equal(255, Centre(Render(scene, [PreviewTestMaps.Flat(White, unlit: true)])).R);
    }

    [Fact]
    public void AMissingMaterialDrawsTheCheckerAtFullBrightness()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Vec3.Zero)], 1);

        RenderedView view = Render(scene, [PreviewMaterial.Missing("x", MaterialProblem.MissingMaterial)]);

        Assert.Contains(view.Image.Get(4, 4), new[] { PreviewMaterial.CheckerA, PreviewMaterial.CheckerB });
    }

    [Fact]
    public void ExposureScalesLight()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, new Vec3(0.25f, 0.25f, 0.25f))], 1);

        Rgba pixel = Centre(Render(scene, [PreviewTestMaps.Flat(White)], settings: RenderSettings.Default with { Exposure = 2 }));

        Assert.Equal(Colors.ToSrgb(0.5f), pixel.R);
    }

    [Fact]
    public void ATintScalesTheTexel()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, new Vec3(1, 1, 1))], 1);
        PreviewMaterial tinted = PreviewTestMaps.Flat(White) with { Colour = new Rgba(255, 0, 0) };

        Rgba pixel = Centre(Render(scene, [tinted]));

        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
    }

    [Fact]
    public void EmptyPerspectiveBackgroundShowsTheSkyFaceItLooksAt()
    {
        PreviewScene scene = PreviewTestMaps.Scene([], 0);
        RgbaImage[] faces = [.. Enumerable.Range(0, 6).Select(i => RgbaImage.Filled(4, 4, new Rgba((byte)(i * 40), 0, 0)))];

        RenderedView view = Render(scene, [], sky: new PreviewSky(faces, White));

        Assert.Equal(0, Centre(view).R); // looking along +X: the rt face, index 0
    }

    [Fact]
    public void AMissingSkyFaceDrawsTheFallback()
    {
        PreviewScene scene = PreviewTestMaps.Scene([], 0);

        Assert.Equal(NoSky.Fallback, Centre(Render(scene, [])));
    }

    [Fact]
    public void ASkySurfaceShowsTheSkyInPerspectiveAndIsLeftOutOrthographically()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, new Vec3(1, 1, 1), sky: true)], 1);
        PreviewCamera along = new("o", CameraKind.Overview, Vec3.Zero, new Vec3(1, 0, 0), new Vec3(0, 0, 1), 0, 100, 16, 16, "o");

        Assert.Equal(NoSky.Fallback, Centre(Render(scene, [PreviewTestMaps.Flat(White)])));
        RenderedView ortho = Render(scene, [PreviewTestMaps.Flat(White)], camera: along, settings: RenderSettings.Default with { Background = new Rgba(1, 2, 3) });
        Assert.Equal(new Rgba(1, 2, 3), Centre(ortho));
        Assert.Equal(-1, ortho.Buffer.Triangle[(8 * 16) + 8]);
    }

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 1)]
    [InlineData(-1, 0, 0, 2)]
    [InlineData(0, -1, 0, 3)]
    [InlineData(0, 0, 1, 4)]
    [InlineData(0, 0, -1, 5)]
    public void SkyFacesFollowTheEngineNaming(float x, float y, float z, int face)
    {
        (int found, float s, float t) = SceneRenderer.SkyFace(new Vec3(x, y, z));
        Assert.Equal(face, found);
        Assert.Equal(0, s, 4);
        Assert.Equal(0, t, 4);
    }

    [Fact]
    public void ATranslucentSurfaceBlendsOverWhatIsBehindIt()
    {
        PreviewScene scene = PreviewTestMaps.Scene(
            [PreviewTestMaps.Wall(200, 1000, 0, new Vec3(1, 1, 1)), PreviewTestMaps.Wall(100, 1000, 1, new Vec3(1, 1, 1))], 2);
        PreviewMaterial glass = PreviewTestMaps.Flat(new Rgba(0, 0, 0), MaterialOpacity.Translucent, alpha: 0.5f);

        Rgba pixel = Centre(Render(scene, [PreviewTestMaps.Flat(White), glass]));

        Assert.Equal(Colors.ToSrgb(0.5f), pixel.R);
    }

    [Fact]
    public void ATranslucentSurfaceBehindAnOpaqueOneIsHidden()
    {
        PreviewScene scene = PreviewTestMaps.Scene(
            [PreviewTestMaps.Wall(100, 1000, 0, new Vec3(1, 1, 1)), PreviewTestMaps.Wall(200, 1000, 1, new Vec3(1, 1, 1))], 2);
        PreviewMaterial glass = PreviewTestMaps.Flat(new Rgba(0, 0, 0), MaterialOpacity.Translucent, alpha: 0.5f);

        Assert.Equal(255, Centre(Render(scene, [PreviewTestMaps.Flat(White), glass])).R);
    }

    [Fact]
    public void AnAlphaTestedHoleShowsWhatIsBehind()
    {
        PreviewScene scene = PreviewTestMaps.Scene(
            [PreviewTestMaps.Wall(200, 1000, 0, new Vec3(1, 1, 1)), PreviewTestMaps.Wall(100, 1000, 1, new Vec3(1, 1, 1))], 2);
        PreviewMaterial fence = PreviewTestMaps.Flat(new Rgba(0, 0, 0, 0), MaterialOpacity.AlphaTest);

        RenderedView view = Render(scene, [PreviewTestMaps.Flat(White), fence]);

        Assert.Equal(255, Centre(view).R);
        Assert.True(view.Buffer.Triangle[(8 * 16) + 8] is 0 or 1);
    }

    [Fact]
    public void TheCutawayLeavesOutTrianglesWhollyAboveIt()
    {
        var roof = (new Vec3(-50, -50, 300), new Vec3(50, -50, 300), new Vec3(50, 50, 300), new Vec3(-50, 50, 300), new Vec3(0, 0, 1), 0, new Vec3(1, 1, 1), -1, -1, false);
        var floor = (new Vec3(-50, -50, 0), new Vec3(50, -50, 0), new Vec3(50, 50, 0), new Vec3(-50, 50, 0), new Vec3(0, 0, 1), 1, new Vec3(1, 1, 1), -1, -1, false);
        PreviewScene scene = PreviewTestMaps.Scene([roof, floor], 2);
        PreviewCamera top = new("o", CameraKind.Overview, new Vec3(0, 0, 500), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 0, 40, 16, 16, "o");

        RenderedView uncut = Render(scene, [PreviewTestMaps.Flat(White), PreviewTestMaps.Flat(new Rgba(9, 9, 9))], camera: top);
        RenderedView cut = Render(scene, [PreviewTestMaps.Flat(White), PreviewTestMaps.Flat(new Rgba(9, 9, 9))], camera: top, filter: new ViewFilter(100));

        Assert.True(uncut.Buffer.Triangle[(8 * 16) + 8] is 0 or 1);
        Assert.True(cut.Buffer.Triangle[(8 * 16) + 8] is 2 or 3);
    }

    [Fact]
    public void TheThreeDSkyboxAreaIsNeverDrawn()
    {
        PreviewScene source = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, new Vec3(1, 1, 1))], 1);
        PreviewSurface[] surfaces = [source.Surfaces[0] with { SkyboxArea = true }];
        PreviewScene scene = new(source.Vertices, source.Triangles, surfaces, source.MaterialNames, source.Lightmaps, [], [], [], source.Min, source.Max, null);

        Assert.All(Render(scene, [PreviewTestMaps.Flat(White)]).Buffer.Triangle, t => Assert.Equal(-1, t));
    }

    [Fact]
    public void TheViewReportsVisibleEntitiesAndBrightness()
    {
        PreviewScene scene = PreviewTestMaps.Scene(
            [PreviewTestMaps.Wall(100, 20, 0, new Vec3(1, 1, 1), entity: 7), PreviewTestMaps.Wall(300, 1000, 0, new Vec3(0.01f, 0.01f, 0.01f))], 1);

        RenderedView view = Render(scene, [PreviewTestMaps.Flat(White)], camera: Forward(32));

        Assert.Equal([7], view.VisibleEntities);
        Assert.Empty(view.VisibleProps);
        Assert.Equal(32 * 32, view.Luminance.SurfacePixels);
        Assert.True(view.Luminance.BlackFraction > 0.5f);
        Assert.True(view.Luminance.ClippedFraction > 0f);
        Assert.Equal(0.01f, view.Luminance.P5, 3);
    }

    [Fact]
    public void AViewOfNothingReportsZeroBrightness()
    {
        RenderedView view = Render(PreviewTestMaps.Scene([], 0), []);
        Assert.Equal(0, view.Luminance.SurfacePixels);
        Assert.Equal(0f, view.Luminance.Mean);
    }

    [Fact]
    public void MaterialsMustMatchTheScene()
    {
        PreviewScene scene = PreviewTestMaps.Scene([], 2);
        Assert.Throws<ArgumentException>(() => new SceneRenderer(scene, [PreviewTestMaps.Flat(White)], NoSky, Colors));
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        PreviewScene scene = PreviewTestMaps.Scene(
            [PreviewTestMaps.Wall(150, 60, 0, new Vec3(0.3f, 0.6f, 0.9f)), PreviewTestMaps.Wall(300, 1000, 1, new Vec3(0.7f, 0.2f, 0.1f))], 2);
        PreviewMaterial[] materials = [PreviewTestMaps.Flat(new Rgba(200, 150, 100)), PreviewTestMaps.Flat(new Rgba(90, 180, 40))];

        byte[] a = Render(scene, materials, camera: Forward(64), settings: RenderSettings.Default with { MaxDegreeOfParallelism = 1 }).Image.ToPng();
        byte[] b = Render(scene, materials, camera: Forward(64), settings: RenderSettings.Default with { MaxDegreeOfParallelism = 8 }).Image.ToPng();

        Assert.Equal(a, b);
    }
}
