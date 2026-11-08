//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Scene;
using Xunit;

namespace SourceSharp.Tests.MapPreview.Render;

/// <summary>
/// Facts for <see cref="Rasterizer"/>: coverage, the edge and depth rules that
/// make images deterministic, near-plane clipping, perspective-correct
/// weights, culling, and that thread count never changes the result.
/// </summary>
public class RasterizerTests
{
    private static readonly Vec3 Lit = new(1, 1, 1);

    private static PreviewCamera LookingAlongX(int width = 64, int height = 48, float fov = 90) =>
        PreviewCamera.FromAngles("t", CameraKind.Custom, Vec3.Zero, 0, 0, fov, width, height, "test");

    private static GBuffer Draw(PreviewScene scene, PreviewCamera camera, int threads = -1, bool[]? twoSided = null)
    {
        CameraView view = camera.View();
        int[] all = Rasterizer.Select(scene, view, _ => true, twoSided ?? new bool[scene.Surfaces.Length]);
        return Rasterizer.Opaque(scene, view, all, null, threads, CancellationToken.None);
    }

    [Fact]
    public void AWallFillingTheViewCoversEveryPixel()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Lit)], 1);

        GBuffer buffer = Draw(scene, LookingAlongX());

        Assert.All(buffer.Triangle, t => Assert.True(t >= 0));
        Assert.All(buffer.Depth, d => Assert.Equal(100f, d, 2));
    }

    [Fact]
    public void TwoTrianglesSharingAnEdgeLeaveNoGap()
    {
        // The wall's diagonal crosses the view; with inclusive edges no pixel
        // centre on it can fall between the two triangles.
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 40, 0, Lit)], 1);

        GBuffer buffer = Draw(scene, LookingAlongX(64, 64));

        // The wall spans 40 / 100 of the half-width either side: pixels 19..44.
        for (int y = 21; y < 43; y++)
        {
            for (int x = 21; x < 43; x++)
            {
                Assert.True(buffer.Triangle[(y * 64) + x] >= 0, $"gap at {x},{y}");
            }
        }
    }

    [Fact]
    public void APixelOnASharedEdgeIsDrawnByExactlyOneTriangle()
    {
        // Counting every pixel each triangle covers: a quad's two halves
        // together cover each pixel once, never twice.
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Lit)], 1);
        CameraView view = LookingAlongX(64, 64).View();
        int[] hits = new int[64 * 64];

        Rasterizer.Draw(scene, view, [0, 1], () => new Counter(hits), 1, CancellationToken.None);

        Assert.All(hits, h => Assert.Equal(1, h));
    }

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(-1, 0, 0, false)]
    [InlineData(0, 0, 1, true)]
    [InlineData(0, 0, -1, false)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, -1, 0, false)]
    public void OnlyDownwardOrRightwardEdgesOwnTheirPixels(float edge, float dx, float dy, bool inside)
    {
        Assert.Equal(inside, Rasterizer.Setup.Inside(edge, dx, dy));
    }

    [Fact]
    public void TheNearerSurfaceWins()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(200, 1000, 0, Lit), PreviewTestMaps.Wall(100, 1000, 0, Lit)], 1);

        GBuffer buffer = Draw(scene, LookingAlongX());

        Assert.All(buffer.Triangle, t => Assert.True(t is 2 or 3, $"triangle {t}"));
    }

    [Fact]
    public void AtEqualDepthTheEarlierTriangleWins()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Lit), PreviewTestMaps.Wall(100, 1000, 0, Lit)], 1);

        GBuffer buffer = Draw(scene, LookingAlongX());

        Assert.All(buffer.Triangle, t => Assert.True(t is 0 or 1, $"triangle {t}"));
    }

    [Fact]
    public void ABackFaceIsCulledUnlessTwoSided()
    {
        var wall = PreviewTestMaps.Wall(100, 1000, 0, Lit);
        wall.Normal = new Vec3(1, 0, 0);
        PreviewScene scene = PreviewTestMaps.Scene([wall], 1);

        Assert.All(Draw(scene, LookingAlongX()).Triangle, t => Assert.Equal(-1, t));
        Assert.All(Draw(scene, LookingAlongX(), twoSided: [true]).Triangle, t => Assert.True(t >= 0));
    }

    [Fact]
    public void GeometryBehindTheCameraIsNotDrawn()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(-100, 1000, 0, Lit)], 1);

        GBuffer buffer = Draw(scene, LookingAlongX(), twoSided: [true]);

        Assert.All(buffer.Triangle, t => Assert.Equal(-1, t));
    }

    [Fact]
    public void AFloorCrossingTheNearPlaneIsClippedAndKeepsItsWeights()
    {
        // A floor from behind the eye to far ahead: the near clip cuts it, and
        // the weights at any pixel still sum to one.
        var floor = (new Vec3(-500, -500, -50), new Vec3(5000, -500, -50), new Vec3(5000, 500, -50), new Vec3(-500, 500, -50),
            new Vec3(0, 0, 1), 0, Lit, -1, -1, false);
        PreviewScene scene = PreviewTestMaps.Scene([floor], 1);

        GBuffer buffer = Draw(scene, LookingAlongX(64, 48));

        int bottom = (47 * 64) + 32;
        Assert.True(buffer.Triangle[bottom] >= 0);
        float b0 = 1f - buffer.B1[bottom] - buffer.B2[bottom];
        Assert.InRange(b0, -1e-3f, 1.001f);
        Assert.True(buffer.Depth[bottom] >= PreviewCamera.NearPlane);
    }

    [Fact]
    public void WeightsArePerspectiveCorrect()
    {
        // A floor seen in perspective: the screen's vertical middle between
        // two depths is not the world midpoint. The interpolated world X at a
        // pixel must match the ray's hit on the plane z = -50.
        var floor = (new Vec3(10, -500, -50), new Vec3(1000, -500, -50), new Vec3(1000, 500, -50), new Vec3(10, 500, -50),
            new Vec3(0, 0, 1), 0, Lit, -1, -1, false);
        PreviewScene scene = PreviewTestMaps.Scene([floor], 1);
        PreviewCamera camera = LookingAlongX(64, 48);

        GBuffer buffer = Draw(scene, camera);

        int x = 32, y = 36, i = (y * 64) + x;
        int t = buffer.Triangle[i];
        PreviewTriangle tri = scene.Triangles[t];
        float b1 = buffer.B1[i], b2 = buffer.B2[i], b0 = 1 - b1 - b2;
        Vec3 hit = (scene.Vertices[tri.A].Position * b0) + (scene.Vertices[tri.B].Position * b1) + (scene.Vertices[tri.C].Position * b2);
        Vec3 ray = SceneRenderer.RayDirection(camera.View(), x, y);
        float along = -50f / ray.Z;
        Assert.Equal(ray.X * along, hit.X, 1);
    }

    [Fact]
    public void ThreadCountDoesNotChangeTheResult()
    {
        PreviewScene scene = PreviewTestMaps.Scene(
            [PreviewTestMaps.Wall(300, 60, 0, Lit), PreviewTestMaps.Wall(200, 30, 0, Lit), PreviewTestMaps.Wall(250, 45, 0, Lit)], 1);

        GBuffer one = Draw(scene, LookingAlongX(200, 150), threads: 1);
        GBuffer many = Draw(scene, LookingAlongX(200, 150), threads: 8);

        Assert.Equal(one.Triangle, many.Triangle);
        Assert.Equal(one.Depth, many.Depth);
        Assert.Equal(one.B1, many.B1);
    }

    [Fact]
    public void AnOrthographicCameraDrawsWithUniformDepthWeights()
    {
        var floor = (new Vec3(-100, -100, 0), new Vec3(100, -100, 0), new Vec3(100, 100, 0), new Vec3(-100, 100, 0),
            new Vec3(0, 0, 1), 0, Lit, -1, -1, false);
        PreviewScene scene = PreviewTestMaps.Scene([floor], 1);
        PreviewCamera top = new("top", CameraKind.Overview, new Vec3(0, 0, 500), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 0, 100, 32, 32, "top");

        GBuffer buffer = Draw(scene, top);

        Assert.All(buffer.Triangle, t => Assert.True(t >= 0));
        Assert.All(buffer.Depth, d => Assert.Equal(500f, d, 2));
    }

    [Fact]
    public void ClipNearReturnsNothingForATriangleWhollyBehind()
    {
        Span<Rasterizer.ClipVertex> output = stackalloc Rasterizer.ClipVertex[4];
        ReadOnlySpan<Rasterizer.ClipVertex> input =
        [
            new(new Vec3(0, 0, -5), 1, 0, 0), new(new Vec3(1, 0, -5), 0, 1, 0), new(new Vec3(0, 1, -5), 0, 0, 1),
        ];

        Assert.Equal(0, Rasterizer.Setup.ClipNear(input, output, 1));
    }

    [Fact]
    public void ClipNearTurnsOneCornerBehindIntoAQuad()
    {
        Span<Rasterizer.ClipVertex> output = stackalloc Rasterizer.ClipVertex[4];
        ReadOnlySpan<Rasterizer.ClipVertex> input =
        [
            new(new Vec3(0, 0, -10), 1, 0, 0), new(new Vec3(10, 0, 10), 0, 1, 0), new(new Vec3(0, 10, 10), 0, 0, 1),
        ];

        int count = Rasterizer.Setup.ClipNear(input, output, 1);

        Assert.Equal(4, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(output[i].View.Z >= 1 - 1e-4f);
            Assert.Equal(1f, output[i].W0 + output[i].W1 + output[i].W2, 4);
        }
    }

    [Fact]
    public void ACoverageTestCanRejectPixels()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Lit)], 1);
        CameraView view = LookingAlongX().View();
        int[] all = Rasterizer.Select(scene, view, _ => true, new bool[1]);

        GBuffer buffer = Rasterizer.Opaque(scene, view, all, new RejectAll(), 1, CancellationToken.None);

        Assert.All(buffer.Triangle, t => Assert.Equal(-1, t));
    }

    [Fact]
    public void ACancelledPassThrows()
    {
        PreviewScene scene = PreviewTestMaps.Scene([PreviewTestMaps.Wall(100, 1000, 0, Lit)], 1);
        CameraView view = LookingAlongX().View();
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Rasterizer.Opaque(scene, view, [0, 1], null, 1, cancel.Token));
    }

    [Fact]
    public void ANewGBufferIsEmptyAndRejectsBadSizes()
    {
        GBuffer buffer = new(3, 2);
        Assert.All(buffer.Depth, d => Assert.True(float.IsPositiveInfinity(d)));
        Assert.All(buffer.Triangle, t => Assert.Equal(-1, t));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GBuffer(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GBuffer(2, 0));
    }

    private readonly struct Counter(int[] hits) : IPixelSink
    {
        public void Pixel(int index, int triangle, float depth, float b0, float b1, float b2) => hits[index]++;
    }

    private sealed class RejectAll : ICoverageTest
    {
        public bool NeedsTest(int triangle) => true;

        public bool Covers(int triangle, float b0, float b1, float b2) => false;
    }
}
