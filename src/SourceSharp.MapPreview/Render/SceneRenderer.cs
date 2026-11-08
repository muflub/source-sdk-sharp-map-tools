//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Materials;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapPreview.Render;

/// <summary>How a view is rendered.</summary>
public sealed record RenderSettings
{
    /// <summary>Gets the default settings.</summary>
    public static RenderSettings Default { get; } = new();

    /// <summary>Gets the multiplier applied to linear light before display; 1 shows the lightmaps as baked.</summary>
    public float Exposure { get; init; } = 1f;

    /// <summary>Gets the tiles shaded at once; -1 for the runtime's default.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>Gets the colour behind an orthographic view where nothing was drawn.</summary>
    public Rgba Background { get; init; } = new(36, 40, 48);
}

/// <summary>Which triangles a view leaves out, beyond back faces.</summary>
/// <param name="CutAboveZ">
/// Leaves out every triangle lying wholly above this height: the cutaway that
/// lets a top-down view see into buildings. Null keeps everything.
/// </param>
public readonly record struct ViewFilter(float? CutAboveZ);

/// <summary>Brightness figures for one view, over the pixels showing map surfaces (not sky or background).</summary>
/// <param name="Mean">Mean linear luminance after exposure.</param>
/// <param name="P5">5th percentile.</param>
/// <param name="P95">95th percentile.</param>
/// <param name="BlackFraction">Share of pixels below 0.02: unlit or nearly so.</param>
/// <param name="ClippedFraction">Share at or above 1.0: blown out on an LDR display.</param>
/// <param name="SurfacePixels">How many pixels the figures cover.</param>
public readonly record struct ViewLuminance(float Mean, float P5, float P95, float BlackFraction, float ClippedFraction, int SurfacePixels);

/// <summary>A rendered view and what is in it.</summary>
/// <param name="Camera">The camera.</param>
/// <param name="Image">The image.</param>
/// <param name="Buffer">The opaque pass, for overlays that need depth or ids.</param>
/// <param name="VisibleEntities">Entity indices with at least one visible pixel, ascending.</param>
/// <param name="VisibleProps">Prop indices with at least one visible pixel, ascending.</param>
/// <param name="Luminance">Brightness figures.</param>
public sealed record RenderedView(
    PreviewCamera Camera,
    RgbaImage Image,
    GBuffer Buffer,
    IReadOnlyList<int> VisibleEntities,
    IReadOnlyList<int> VisibleProps,
    ViewLuminance Luminance);

/// <summary>
/// Renders views of a scene: opaque pass, deferred shading, sorted
/// translucency, display encoding.
/// </summary>
/// <remarks>
/// <para>
/// A lit surface's colour is its texel, converted to linear light, times the
/// light reaching it: the face's baked lightmap for brushes and
/// displacements, the vertex light from the ambient cubes for models, and 1
/// for unlit shaders. Lightmap values are linear with 1.0 showing a texture at
/// its authored brightness, so with an exposure of 1 a surface vrad lit to
/// full brightness looks as its texture does.
/// </para>
/// <para>
/// Sky faces and empty background in a perspective view show the 2D skybox in
/// the pixel's direction. Translucent surfaces draw after shading, sorted far
/// to near by centroid depth (ties broken by scene order), each blended over
/// whatever is behind it; the sort is per triangle, which is enough for
/// windows, water and grates but not for interpenetrating translucent meshes.
/// </para>
/// </remarks>
public sealed class SceneRenderer
{
    private readonly PreviewScene _scene;
    private readonly PreviewMaterial[] _materials;
    private readonly PreviewSky _sky;
    private readonly ColorSpace _colors;
    private readonly bool[] _twoSided;

    /// <summary>Creates a renderer over a scene and its resolved materials.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="materials">One material per <see cref="PreviewScene.MaterialNames"/> entry.</param>
    /// <param name="sky">The skybox.</param>
    /// <param name="colors">The colour tables.</param>
    public SceneRenderer(PreviewScene scene, PreviewMaterial[] materials, PreviewSky sky, ColorSpace colors)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(sky);
        ArgumentNullException.ThrowIfNull(colors);
        if (materials.Length != scene.MaterialNames.Length)
        {
            throw new ArgumentException($"{materials.Length} materials for {scene.MaterialNames.Length} names", nameof(materials));
        }

        _scene = scene;
        _materials = materials;
        _sky = sky;
        _colors = colors;
        _twoSided = new bool[scene.Surfaces.Length];
        for (int s = 0; s < scene.Surfaces.Length; s++)
        {
            _twoSided[s] = materials[scene.Surfaces[s].Material].NoCull;
        }
    }

    /// <summary>Renders one view.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="filter">What the view leaves out.</param>
    /// <param name="settings">Exposure, threads, background.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The view.</returns>
    public RenderedView Render(PreviewCamera camera, ViewFilter filter, RenderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(settings);
        CameraView view = camera.View();
        bool orthographic = camera.IsOrthographic;
        int[] opaque = Rasterizer.Select(_scene, view, t => Keep(t, filter, orthographic) && !IsTranslucent(t), _twoSided);
        GBuffer buffer = Rasterizer.Opaque(_scene, view, opaque, new AlphaCoverage(this), settings.MaxDegreeOfParallelism, cancellationToken);

        int count = camera.Width * camera.Height;
        Vec3[] colour = new Vec3[count];
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = settings.MaxDegreeOfParallelism == 0 ? -1 : settings.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };
        Vec3 background = Linear(settings.Background);
        Parallel.For(0, camera.Height, options, y =>
        {
            for (int x = 0; x < camera.Width; x++)
            {
                int i = (y * camera.Width) + x;
                int t = buffer.Triangle[i];
                if (t < 0 || _scene.Surfaces[_scene.Triangles[t].Surface].Sky)
                {
                    colour[i] = camera.IsOrthographic && t < 0 ? background : SkyColour(RayDirection(view, x, y));
                    continue;
                }

                float b1 = buffer.B1[i], b2 = buffer.B2[i];
                colour[i] = Shade(t, 1f - b1 - b2, b1, b2, out _) * settings.Exposure;
            }
        });

        int[] translucent = Rasterizer.Select(_scene, view, t => Keep(t, filter, orthographic) && IsTranslucent(t), _twoSided);
        if (translucent.Length > 0)
        {
            translucent = SortFarToNear(view, translucent);
            Rasterizer.Draw(
                _scene,
                view,
                translucent,
                () => new BlendSink(this, buffer, colour, settings.Exposure),
                // Blending is order-dependent within a pixel; tiles still run in
                // parallel because they never share one.
                settings.MaxDegreeOfParallelism,
                cancellationToken);
        }

        RgbaImage image = new(camera.Width, camera.Height, new byte[count * 4]);
        for (int i = 0; i < count; i++)
        {
            int at = i * 4;
            image.Pixels[at] = _colors.ToSrgb(colour[i].X);
            image.Pixels[at + 1] = _colors.ToSrgb(colour[i].Y);
            image.Pixels[at + 2] = _colors.ToSrgb(colour[i].Z);
            image.Pixels[at + 3] = 255;
        }

        (IReadOnlyList<int> entities, IReadOnlyList<int> props) = Visible(buffer);
        return new RenderedView(camera, image, buffer, entities, props, Measure(buffer, colour));
    }

    /// <summary>The direction of the ray through a pixel's centre.</summary>
    /// <param name="view">The camera view.</param>
    /// <param name="x">The pixel column.</param>
    /// <param name="y">The pixel row.</param>
    /// <returns>A unit direction.</returns>
    internal static Vec3 RayDirection(CameraView view, int x, int y)
    {
        if (view.Camera.IsOrthographic)
        {
            return view.Forward;
        }

        float nx = (((x + 0.5f) / view.Camera.Width) * 2f) - 1f;
        float ny = 1f - (((y + 0.5f) / view.Camera.Height) * 2f);
        Vec3 d = view.Forward + (view.Right * (nx * view.HalfWidth)) + (view.Up * (ny * view.HalfHeight));
        return d * (1f / d.Length());
    }

    /// <summary>
    /// The skybox colour in a direction. Faces follow the engine's naming:
    /// rt looks along +X, lf +Y, bk -X, ft -Y, up +Z, dn -Z.
    /// </summary>
    /// <param name="d">A unit direction.</param>
    /// <returns>Linear colour.</returns>
    internal Vec3 SkyColour(Vec3 d)
    {
        (int face, float s, float t) = SkyFace(d);
        RgbaImage? image = _sky.Faces[face];
        if (image is null)
        {
            return Linear(_sky.Fallback);
        }

        float u = Math.Clamp((s + 1f) * 0.5f, 0f, 1f);
        float v = Math.Clamp((1f - t) * 0.5f, 0f, 1f);
        int x = Math.Min(image.Width - 1, (int)(u * image.Width));
        int y = Math.Min(image.Height - 1, (int)(v * image.Height));
        return Linear(image.Get(x, y));
    }

    /// <summary>
    /// Which skybox face a direction lands on (index into <see cref="PreviewSky.Faces"/>)
    /// and where on it, as s across and t up, each -1..1.
    /// </summary>
    /// <param name="d">A direction.</param>
    /// <returns>The face and position.</returns>
    internal static (int Face, float S, float T) SkyFace(Vec3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az)
        {
            return d.X >= 0 ? (0, -d.Y / ax, d.Z / ax) : (2, d.Y / ax, d.Z / ax);
        }

        if (ay >= az)
        {
            return d.Y >= 0 ? (1, d.X / ay, d.Z / ay) : (3, -d.X / ay, d.Z / ay);
        }

        return d.Z >= 0 ? (4, -d.Y / az, -d.X / az) : (5, -d.Y / az, d.X / az);
    }

    /// <summary>Shades one point of a triangle: texel times light, before exposure.</summary>
    /// <param name="triangle">The scene triangle.</param>
    /// <param name="b0">Weight of its first vertex.</param>
    /// <param name="b1">Weight of its second.</param>
    /// <param name="b2">Weight of its third.</param>
    /// <param name="alpha">The texel's alpha times the material's.</param>
    /// <returns>Linear colour.</returns>
    internal Vec3 Shade(int triangle, float b0, float b1, float b2, out float alpha)
    {
        PreviewTriangle tri = _scene.Triangles[triangle];
        PreviewSurface surface = _scene.Surfaces[tri.Surface];
        PreviewMaterial material = _materials[surface.Material];
        PreviewVertex a = _scene.Vertices[tri.A], b = _scene.Vertices[tri.B], c = _scene.Vertices[tri.C];
        float u = (b0 * a.U) + (b1 * b.U) + (b2 * c.U);
        float v = (b0 * a.V) + (b1 * b.V) + (b2 * c.V);
        Rgba texel = material.Sample(u, v);
        Rgba tint = material.Texture is null ? new Rgba(255, 255, 255) : material.Colour;
        alpha = (texel.A / 255f) * material.Alpha;
        Vec3 albedo = new(
            _colors.ToLinear(texel.R) * (tint.R / 255f),
            _colors.ToLinear(texel.G) * (tint.G / 255f),
            _colors.ToLinear(texel.B) * (tint.B / 255f));

        Vec3 light;
        if (material.Unlit || material.IsMissing)
        {
            light = new Vec3(1, 1, 1);
        }
        else if (surface.Lightmap >= 0)
        {
            float s = (b0 * a.LuxelS) + (b1 * b.LuxelS) + (b2 * c.LuxelS);
            float t = (b0 * a.LuxelT) + (b1 * b.LuxelT) + (b2 * c.LuxelT);
            light = _scene.Lightmaps[surface.Lightmap].Sample(s, t);
        }
        else
        {
            light = (a.Light * b0) + (b.Light * b1) + (c.Light * b2);
        }

        return new Vec3(albedo.X * light.X, albedo.Y * light.Y, albedo.Z * light.Z);
    }

    /// <summary>
    /// Whether a view draws a triangle: never the 3D skybox's miniature, no
    /// sky faces from outside the map (an orthographic view looks at the
    /// sky shell from beyond it), and nothing wholly above a cutaway.
    /// </summary>
    private bool Keep(int triangle, ViewFilter filter, bool orthographic)
    {
        PreviewTriangle tri = _scene.Triangles[triangle];
        PreviewSurface surface = _scene.Surfaces[tri.Surface];
        if (surface.SkyboxArea || (orthographic && surface.Sky))
        {
            return false;
        }

        if (filter.CutAboveZ is not { } cut)
        {
            return true;
        }

        float lowest = MathF.Min(_scene.Vertices[tri.A].Position.Z, MathF.Min(_scene.Vertices[tri.B].Position.Z, _scene.Vertices[tri.C].Position.Z));
        return lowest <= cut;
    }

    private bool IsTranslucent(int triangle)
    {
        PreviewSurface surface = _scene.Surfaces[_scene.Triangles[triangle].Surface];
        if (surface.Sky)
        {
            return false;
        }

        PreviewMaterial material = _materials[surface.Material];
        return material.Opacity == MaterialOpacity.Translucent || material.Alpha < 1f;
    }

    private int[] SortFarToNear(CameraView view, int[] triangles)
    {
        (float Depth, int Index)[] keyed = new (float, int)[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            PreviewTriangle tri = _scene.Triangles[triangles[i]];
            Vec3 centre = (_scene.Vertices[tri.A].Position + _scene.Vertices[tri.B].Position + _scene.Vertices[tri.C].Position) * (1f / 3f);
            keyed[i] = (view.ToView(centre).Z, triangles[i]);
        }

        Array.Sort(keyed, (x, y) => x.Depth != y.Depth ? y.Depth.CompareTo(x.Depth) : x.Index.CompareTo(y.Index));
        return [.. keyed.Select(k => k.Index)];
    }

    private (IReadOnlyList<int> Entities, IReadOnlyList<int> Props) Visible(GBuffer buffer)
    {
        SortedSet<int> entities = [];
        SortedSet<int> props = [];
        foreach (int t in buffer.Triangle)
        {
            if (t < 0)
            {
                continue;
            }

            PreviewSurface surface = _scene.Surfaces[_scene.Triangles[t].Surface];
            if (surface.Entity >= 0)
            {
                entities.Add(surface.Entity);
            }

            if (surface.Prop >= 0)
            {
                props.Add(surface.Prop);
            }
        }

        return ([.. entities], [.. props]);
    }

    private ViewLuminance Measure(GBuffer buffer, Vec3[] colour)
    {
        List<float> values = [];
        int black = 0, clipped = 0;
        double sum = 0;
        for (int i = 0; i < colour.Length; i++)
        {
            int t = buffer.Triangle[i];
            if (t < 0 || _scene.Surfaces[_scene.Triangles[t].Surface].Sky)
            {
                continue;
            }

            float y = (0.2126f * colour[i].X) + (0.7152f * colour[i].Y) + (0.0722f * colour[i].Z);
            values.Add(y);
            sum += y;
            black += y < 0.02f ? 1 : 0;
            clipped += y >= 1f ? 1 : 0;
        }

        if (values.Count == 0)
        {
            return new ViewLuminance(0, 0, 0, 0, 0, 0);
        }

        values.Sort();
        float Percentile(float p) => values[Math.Clamp((int)(p * (values.Count - 1)), 0, values.Count - 1)];
        return new ViewLuminance(
            (float)(sum / values.Count),
            Percentile(0.05f),
            Percentile(0.95f),
            black / (float)values.Count,
            clipped / (float)values.Count,
            values.Count);
    }

    private Vec3 Linear(Rgba c) => new(_colors.ToLinear(c.R), _colors.ToLinear(c.G), _colors.ToLinear(c.B));

    /// <summary>The alpha test for the opaque pass: alpha-tested surfaces drop texels under their reference.</summary>
    private sealed class AlphaCoverage(SceneRenderer renderer) : ICoverageTest
    {
        public bool NeedsTest(int triangle)
        {
            PreviewSurface surface = renderer._scene.Surfaces[renderer._scene.Triangles[triangle].Surface];
            return !surface.Sky && renderer._materials[surface.Material].Opacity == MaterialOpacity.AlphaTest;
        }

        public bool Covers(int triangle, float b0, float b1, float b2)
        {
            PreviewTriangle tri = renderer._scene.Triangles[triangle];
            PreviewMaterial material = renderer._materials[renderer._scene.Surfaces[tri.Surface].Material];
            PreviewVertex a = renderer._scene.Vertices[tri.A], b = renderer._scene.Vertices[tri.B], c = renderer._scene.Vertices[tri.C];
            Rgba texel = material.Sample((b0 * a.U) + (b1 * b.U) + (b2 * c.U), (b0 * a.V) + (b1 * b.V) + (b2 * c.V));
            return texel.A / 255f >= material.AlphaReference;
        }
    }

    /// <summary>Blends translucent texels over the shaded image where they are in front of it.</summary>
    private readonly struct BlendSink(SceneRenderer renderer, GBuffer buffer, Vec3[] colour, float exposure) : IPixelSink
    {
        public void Pixel(int index, int triangle, float depth, float b0, float b1, float b2)
        {
            if (!(depth < buffer.Depth[index]))
            {
                return;
            }

            Vec3 src = renderer.Shade(triangle, b0, b1, b2, out float alpha) * exposure;
            alpha = Math.Clamp(alpha, 0f, 1f);
            colour[index] = (colour[index] * (1f - alpha)) + (src * alpha);
        }
    }
}
