//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapPreview.Scene;

namespace SourceSharp.MapPreview.Render;

/// <summary>
/// The per-pixel result of the opaque pass: which triangle won the depth test
/// and where on it, so shading runs once per visible pixel.
/// </summary>
/// <remarks>
/// Barycentric weights are perspective-correct and refer to the scene
/// triangle's own three vertices (a triangle clipped against the near plane
/// carries its original weights through the clip), so shading interpolates
/// the scene's vertex attributes directly.
/// </remarks>
public sealed class GBuffer
{
    /// <summary>Creates an empty buffer: infinite depth, no triangle.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    public GBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        int count = width * height;
        Depth = new float[count];
        Triangle = new int[count];
        B1 = new float[count];
        B2 = new float[count];
        Array.Fill(Depth, float.PositiveInfinity);
        Array.Fill(Triangle, -1);
    }

    /// <summary>Gets the width.</summary>
    public int Width { get; }

    /// <summary>Gets the height.</summary>
    public int Height { get; }

    /// <summary>Gets the view depth of the nearest surface per pixel; infinity where nothing drew.</summary>
    public float[] Depth { get; }

    /// <summary>Gets the scene triangle per pixel, or -1.</summary>
    public int[] Triangle { get; }

    /// <summary>Gets the weight of the triangle's second vertex per pixel.</summary>
    public float[] B1 { get; }

    /// <summary>Gets the weight of the triangle's third vertex per pixel.</summary>
    public float[] B2 { get; }
}

/// <summary>Decides, per pixel, whether an alpha-tested triangle covers it.</summary>
public interface ICoverageTest
{
    /// <summary>Whether a triangle needs the test at all; most do not and skip it.</summary>
    /// <param name="triangle">The scene triangle.</param>
    /// <returns>True for alpha-tested surfaces.</returns>
    bool NeedsTest(int triangle);

    /// <summary>Whether the triangle is solid at these weights.</summary>
    /// <param name="triangle">The scene triangle.</param>
    /// <param name="b0">Weight of its first vertex.</param>
    /// <param name="b1">Weight of its second vertex.</param>
    /// <param name="b2">Weight of its third vertex.</param>
    /// <returns>True when the texel is opaque enough to keep.</returns>
    bool Covers(int triangle, float b0, float b1, float b2);
}

/// <summary>Receives the pixels a triangle covers, for passes other than the opaque one.</summary>
public interface IPixelSink
{
    /// <summary>One covered pixel.</summary>
    /// <param name="index">The pixel's index, row-major.</param>
    /// <param name="triangle">The scene triangle.</param>
    /// <param name="depth">The view depth.</param>
    /// <param name="b0">Weight of its first vertex.</param>
    /// <param name="b1">Weight of its second vertex.</param>
    /// <param name="b2">Weight of its third vertex.</param>
    void Pixel(int index, int triangle, float depth, float b0, float b1, float b2);
}

/// <summary>
/// A tiled, deterministic triangle rasteriser.
/// </summary>
/// <remarks>
/// <para>
/// The screen is cut into <see cref="TileSize"/>-pixel tiles; each triangle is
/// binned into the tiles its bounds touch, in scene order, and tiles are
/// filled in parallel. Within a tile triangles are drawn in bin order, so the
/// result is the same however many threads run.
/// </para>
/// <para>
/// Edges are inclusive (a pixel centre exactly on an edge belongs to both
/// triangles that share it, so a seam can never open), and the depth test is
/// strictly less, so of two triangles at the same depth the earlier one in
/// scene order wins. That pairing makes every image a pure function of the
/// scene and the camera.
/// </para>
/// <para>
/// Triangles are clipped against the near plane in view space before
/// projection; a clipped corner carries the original triangle's barycentric
/// weights, so interpolation downstream is unaffected by the clip.
/// </para>
/// </remarks>
public static class Rasterizer
{
    /// <summary>The tile edge in pixels.</summary>
    public const int TileSize = 32;

    /// <summary>Selects the triangles a pass draws.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="view">The camera.</param>
    /// <param name="include">Per triangle, whether this pass wants it (material, filter).</param>
    /// <param name="twoSided">Per surface, whether back faces draw.</param>
    /// <returns>The front-facing included triangles, in scene order.</returns>
    public static int[] Select(PreviewScene scene, CameraView view, Func<int, bool> include, bool[] twoSided)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(twoSided);
        List<int> picked = [];
        for (int t = 0; t < scene.Triangles.Length; t++)
        {
            PreviewTriangle tri = scene.Triangles[t];
            if (!include(t))
            {
                continue;
            }

            if (!twoSided[tri.Surface] && !view.Faces(scene.Vertices[tri.A].Position, tri.Normal))
            {
                continue;
            }

            picked.Add(t);
        }

        return [.. picked];
    }

    /// <summary>Rasterises opaque triangles into a G-buffer.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="view">The camera.</param>
    /// <param name="triangles">The triangles to draw, in draw order.</param>
    /// <param name="coverage">The alpha test, or null when nothing is alpha-tested.</param>
    /// <param name="maxDegreeOfParallelism">Tiles filled at once; -1 for the runtime's default.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The G-buffer.</returns>
    public static GBuffer Opaque(
        PreviewScene scene,
        CameraView view,
        IReadOnlyList<int> triangles,
        ICoverageTest? coverage,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(triangles);
        GBuffer buffer = new(view.Camera.Width, view.Camera.Height);
        Setup setup = Setup.Build(scene, view, triangles);
        RunTiles(
            setup,
            maxDegreeOfParallelism,
            tile =>
            {
                OpaqueSink sink = new(buffer, coverage);
                setup.FillTile(tile, ref sink);
            },
            cancellationToken);
        return buffer;
    }

    /// <summary>
    /// Rasterises triangles into a caller's sink, tile by tile. Each tile's
    /// sink sees its triangles in the given order; tiles run in parallel and
    /// never share a pixel, so a sink may write per-pixel state without locks.
    /// </summary>
    /// <typeparam name="TSink">The sink.</typeparam>
    /// <param name="scene">The scene.</param>
    /// <param name="view">The camera.</param>
    /// <param name="triangles">The triangles, in draw order.</param>
    /// <param name="makeSink">Makes a sink for one tile.</param>
    /// <param name="maxDegreeOfParallelism">Tiles filled at once.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public static void Draw<TSink>(
        PreviewScene scene,
        CameraView view,
        IReadOnlyList<int> triangles,
        Func<TSink> makeSink,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
        where TSink : struct, IPixelSink
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(makeSink);
        Setup setup = Setup.Build(scene, view, triangles);
        RunTiles(
            setup,
            maxDegreeOfParallelism,
            tile =>
            {
                TSink sink = makeSink();
                setup.FillTile(tile, ref sink);
            },
            cancellationToken);
    }

    private static void RunTiles(Setup setup, int maxDegreeOfParallelism, Action<int> fill, CancellationToken cancellationToken)
    {
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism == 0 ? -1 : maxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };
        Parallel.For(0, setup.TileCount, options, fill);
    }

    private struct OpaqueSink(GBuffer buffer, ICoverageTest? coverage) : IPixelSink
    {
        public readonly void Pixel(int index, int triangle, float depth, float b0, float b1, float b2)
        {
            if (!(depth < buffer.Depth[index]))
            {
                return;
            }

            if (coverage is not null && coverage.NeedsTest(triangle) && !coverage.Covers(triangle, b0, b1, b2))
            {
                return;
            }

            buffer.Depth[index] = depth;
            buffer.Triangle[index] = triangle;
            buffer.B1[index] = b1;
            buffer.B2[index] = b2;
        }
    }

    /// <summary>One projected (possibly clipped) triangle, ready to scan.</summary>
    internal readonly struct ScreenTriangle
    {
        public ScreenTriangle(int triangle, ScreenVertex a, ScreenVertex b, ScreenVertex c)
        {
            Triangle = triangle;
            A = a;
            B = b;
            C = c;
        }

        public int Triangle { get; }

        public ScreenVertex A { get; }

        public ScreenVertex B { get; }

        public ScreenVertex C { get; }
    }

    /// <summary>
    /// A projected corner: pixel position, view depth, the reciprocal used for
    /// perspective-correct interpolation (1 for an orthographic camera), and
    /// its weights against the scene triangle.
    /// </summary>
    internal readonly record struct ScreenVertex(float X, float Y, float Depth, float Recip, float W0, float W1, float W2);

    /// <summary>Projected triangles and their tile bins for one pass.</summary>
    internal sealed class Setup
    {
        private readonly List<ScreenTriangle> _triangles;
        private readonly List<int>[] _bins;
        private readonly int _tilesX;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _orthographic;

        private Setup(List<ScreenTriangle> triangles, List<int>[] bins, int tilesX, int width, int height, bool orthographic)
        {
            _triangles = triangles;
            _bins = bins;
            _tilesX = tilesX;
            _width = width;
            _height = height;
            _orthographic = orthographic;
        }

        public int TileCount => _bins.Length;

        internal IReadOnlyList<ScreenTriangle> Triangles => _triangles;

        public static Setup Build(PreviewScene scene, CameraView view, IReadOnlyList<int> triangles)
        {
            int width = view.Camera.Width;
            int height = view.Camera.Height;
            int tilesX = (width + TileSize - 1) / TileSize;
            int tilesY = (height + TileSize - 1) / TileSize;
            List<int>[] bins = new List<int>[tilesX * tilesY];
            for (int i = 0; i < bins.Length; i++)
            {
                bins[i] = [];
            }

            List<ScreenTriangle> screen = [];
            float near = view.Camera.IsOrthographic ? 0f : PreviewCamera.NearPlane;
            Span<ClipVertex> input = stackalloc ClipVertex[3];
            Span<ClipVertex> output = stackalloc ClipVertex[4];
            foreach (int t in triangles)
            {
                PreviewTriangle tri = scene.Triangles[t];
                input[0] = new ClipVertex(view.ToView(scene.Vertices[tri.A].Position), 1, 0, 0);
                input[1] = new ClipVertex(view.ToView(scene.Vertices[tri.B].Position), 0, 1, 0);
                input[2] = new ClipVertex(view.ToView(scene.Vertices[tri.C].Position), 0, 0, 1);
                int count = ClipNear(input, output, near);
                for (int k = 1; k + 1 < count; k++)
                {
                    ScreenVertex a = Project(view, output[0]);
                    ScreenVertex b = Project(view, output[k]);
                    ScreenVertex c = Project(view, output[k + 1]);
                    float minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
                    float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
                    float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                    float maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                    if (maxX < 0 || maxY < 0 || minX >= width || minY >= height)
                    {
                        continue;
                    }

                    int index = screen.Count;
                    screen.Add(new ScreenTriangle(t, a, b, c));
                    int tx0 = Math.Clamp((int)MathF.Floor(minX) / TileSize, 0, tilesX - 1);
                    int tx1 = Math.Clamp((int)MathF.Floor(maxX) / TileSize, 0, tilesX - 1);
                    int ty0 = Math.Clamp((int)MathF.Floor(minY) / TileSize, 0, tilesY - 1);
                    int ty1 = Math.Clamp((int)MathF.Floor(maxY) / TileSize, 0, tilesY - 1);
                    for (int ty = ty0; ty <= ty1; ty++)
                    {
                        for (int tx = tx0; tx <= tx1; tx++)
                        {
                            bins[(ty * tilesX) + tx].Add(index);
                        }
                    }
                }
            }

            return new Setup(screen, bins, tilesX, width, height, view.Camera.IsOrthographic);
        }

        public void FillTile<TSink>(int tile, ref TSink sink)
            where TSink : struct, IPixelSink
        {
            int x0 = (tile % _tilesX) * TileSize;
            int y0 = (tile / _tilesX) * TileSize;
            int x1 = Math.Min(x0 + TileSize, _width);
            int y1 = Math.Min(y0 + TileSize, _height);
            foreach (int index in _bins[tile])
            {
                Scan(_triangles[index], x0, y0, x1, y1, ref sink);
            }
        }

        private void Scan<TSink>(in ScreenTriangle tri, int x0, int y0, int x1, int y1, ref TSink sink)
            where TSink : struct, IPixelSink
        {
            ScreenVertex a = tri.A, b = tri.B, c = tri.C;
            float area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
            if (area == 0 || float.IsNaN(area))
            {
                return;
            }

            if (area < 0)
            {
                (b, c) = (c, b);
                area = -area;
            }

            int minX = Math.Max(x0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            int maxX = Math.Min(x1 - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int minY = Math.Max(y0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int maxY = Math.Min(y1 - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            float inverseArea = 1f / area;
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float e0 = Edge(b.X, b.Y, c.X, c.Y, px, py);
                    float e1 = Edge(c.X, c.Y, a.X, a.Y, px, py);
                    float e2 = Edge(a.X, a.Y, b.X, b.Y, px, py);
                    if (e0 < 0 || e1 < 0 || e2 < 0)
                    {
                        continue;
                    }

                    float l0 = e0 * inverseArea, l1 = e1 * inverseArea, l2 = e2 * inverseArea;
                    float depth;
                    float p0, p1, p2;
                    if (_orthographic)
                    {
                        depth = (l0 * a.Depth) + (l1 * b.Depth) + (l2 * c.Depth);
                        p0 = l0;
                        p1 = l1;
                        p2 = l2;
                    }
                    else
                    {
                        float q0 = l0 * a.Recip, q1 = l1 * b.Recip, q2 = l2 * c.Recip;
                        float sum = q0 + q1 + q2;
                        depth = 1f / sum;
                        p0 = q0 * depth;
                        p1 = q1 * depth;
                        p2 = q2 * depth;
                    }

                    float w0 = (p0 * a.W0) + (p1 * b.W0) + (p2 * c.W0);
                    float w1 = (p0 * a.W1) + (p1 * b.W1) + (p2 * c.W1);
                    float w2 = (p0 * a.W2) + (p1 * b.W2) + (p2 * c.W2);
                    sink.Pixel((y * _width) + x, tri.Triangle, depth, w0, w1, w2);
                }
            }
        }

        private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
            ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

        private static ScreenVertex Project(CameraView view, ClipVertex v)
        {
            (float x, float y, float depth) = view.ToScreen(v.View);
            float recip = view.Camera.IsOrthographic ? 1f : 1f / depth;
            return new ScreenVertex(x, y, depth, recip, v.W0, v.W1, v.W2);
        }

        /// <summary>
        /// Clips a triangle to view depth at least <paramref name="near"/>
        /// (Sutherland-Hodgman against one plane), carrying the weights.
        /// </summary>
        /// <returns>How many corners the clipped polygon has: 0, 3 or 4.</returns>
        internal static int ClipNear(ReadOnlySpan<ClipVertex> input, Span<ClipVertex> output, float near)
        {
            int count = 0;
            for (int i = 0; i < 3; i++)
            {
                ClipVertex current = input[i];
                ClipVertex next = input[(i + 1) % 3];
                bool currentIn = current.View.Z >= near;
                bool nextIn = next.View.Z >= near;
                if (currentIn)
                {
                    output[count++] = current;
                }

                if (currentIn != nextIn)
                {
                    float t = (near - current.View.Z) / (next.View.Z - current.View.Z);
                    output[count++] = new ClipVertex(
                        current.View + ((next.View - current.View) * t),
                        current.W0 + ((next.W0 - current.W0) * t),
                        current.W1 + ((next.W1 - current.W1) * t),
                        current.W2 + ((next.W2 - current.W2) * t));
                }
            }

            return count;
        }
    }

    /// <summary>A corner in view space with its weights against the scene triangle.</summary>
    internal readonly record struct ClipVertex(Vec3 View, float W0, float W1, float W2);
}
