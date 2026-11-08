//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapPreview.Scene;

/// <summary>
/// One corner of a preview triangle: where it is, where it samples its
/// texture and its lightmap, and the light a vertex-lit surface carries.
/// </summary>
/// <param name="Position">World position.</param>
/// <param name="U">Texture coordinate across, in texture widths (1.0 is one repeat).</param>
/// <param name="V">Texture coordinate down, in texture heights.</param>
/// <param name="LuxelS">Lightmap coordinate across, in luxels from the face's lightmap origin.</param>
/// <param name="LuxelT">Lightmap coordinate down, in luxels.</param>
/// <param name="Light">
/// Linear light at the vertex, for surfaces with no lightmap (props); 1.0 is
/// the light that shows a texture at its authored brightness.
/// </param>
public readonly record struct PreviewVertex(Vec3 Position, float U, float V, float LuxelS, float LuxelT, Vec3 Light);

/// <summary>One triangle: three vertex indices, the surface it belongs to and its facing.</summary>
/// <param name="A">The first vertex.</param>
/// <param name="B">The second vertex.</param>
/// <param name="C">The third vertex.</param>
/// <param name="Surface">Index into <see cref="PreviewScene.Surfaces"/>.</param>
/// <param name="Normal">
/// The side the triangle is seen from, unit length. Carried explicitly rather
/// than derived from winding: brush faces take their plane's normal and model
/// triangles the side their vertex normals agree on, so no winding convention
/// has to be assumed anywhere.
/// </param>
public readonly record struct PreviewTriangle(int A, int B, int C, int Surface, Vec3 Normal);

/// <summary>What kind of map object a surface came from.</summary>
public enum SurfaceKind
{
    /// <summary>A face of the world model (brushes and func_detail).</summary>
    World,

    /// <summary>A displacement surface.</summary>
    Displacement,

    /// <summary>A face of a brush entity's model (doors, func_brush, ...).</summary>
    BrushEntity,

    /// <summary>A static prop from the game lump.</summary>
    StaticProp,

    /// <summary>A prop entity (prop_dynamic, prop_physics, ...).</summary>
    PropEntity,

    /// <summary>A stand-in box for a model that could not be loaded.</summary>
    MissingModel,

    /// <summary>A scale reference figure added by a camera, not part of the map.</summary>
    ScaleFigure,
}

/// <summary>
/// A run of triangles that share a material and a lighting source: one brush
/// face, one displacement, or one mesh of one prop.
/// </summary>
/// <param name="Kind">Where it came from.</param>
/// <param name="Material">Index into <see cref="PreviewScene.MaterialNames"/>.</param>
/// <param name="Lightmap">Index into <see cref="PreviewScene.Lightmaps"/>, or -1 to use vertex light.</param>
/// <param name="Entity">Index into <see cref="PreviewScene.Entities"/>, or -1 for the world and static props.</param>
/// <param name="Face">The BSP face it was built from, or -1.</param>
/// <param name="Sky">Whether the surface is a sky face: drawn as the skybox seen through it.</param>
/// <param name="Prop">Index into <see cref="PreviewScene.Props"/> for prop surfaces, or -1.</param>
public readonly record struct PreviewSurface(
    SurfaceKind Kind,
    int Material,
    int Lightmap,
    int Entity,
    int Face,
    bool Sky,
    int Prop = -1)
{
    /// <summary>
    /// Gets a value indicating whether the surface is in the 3D skybox's area,
    /// which views leave out.
    /// </summary>
    public bool SkyboxArea { get; init; }
}

/// <summary>One face's style-zero lightmap, decoded to linear light.</summary>
/// <param name="Width">Samples across (luxels + 1).</param>
/// <param name="Height">Samples down.</param>
/// <param name="Samples">Row-major linear colours; 1.0 shows a texture at authored brightness.</param>
public sealed record PreviewLightmap(int Width, int Height, Vec3[] Samples)
{
    /// <summary>Bilinearly samples the lightmap at a luxel coordinate, clamped to its edges.</summary>
    /// <param name="s">Luxels across from the lightmap origin.</param>
    /// <param name="t">Luxels down.</param>
    /// <returns>The interpolated light.</returns>
    public Vec3 Sample(float s, float t)
    {
        float x = Math.Clamp(s, 0f, Width - 1);
        float y = Math.Clamp(t, 0f, Height - 1);
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        float fx = x - x0;
        float fy = y - y0;
        Vec3 top = Lerp(Samples[(y0 * Width) + x0], Samples[(y0 * Width) + x1], fx);
        Vec3 bottom = Lerp(Samples[(y1 * Width) + x0], Samples[(y1 * Width) + x1], fx);
        return Lerp(top, bottom, fy);
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + ((b - a) * t);
}

/// <summary>A placed model: a static prop or a prop entity.</summary>
/// <param name="Model">The model path as the map names it.</param>
/// <param name="Origin">World origin.</param>
/// <param name="Angles">Pitch, yaw, roll in degrees.</param>
/// <param name="Skin">The skin family.</param>
/// <param name="Entity">The entity index for prop entities, or -1 for static props.</param>
/// <param name="Loaded">Whether its mesh loaded; false draws a stand-in box.</param>
public sealed record PreviewProp(string Model, Vec3 Origin, Vec3 Angles, int Skin, int Entity, bool Loaded);

/// <summary>A player spawn point.</summary>
/// <param name="ClassName">The entity class.</param>
/// <param name="Team">The team number (TF2 <c>TeamNum</c>: 2 red, 3 blue), or 0 for none.</param>
/// <param name="Origin">The spawn's origin, at the player's feet.</param>
/// <param name="Yaw">The facing, in degrees.</param>
public readonly record struct PreviewSpawn(string ClassName, int Team, Vec3 Origin, float Yaw);

/// <summary>
/// Everything a preview draws, in one immutable, renderer-agnostic form: built
/// once per map revision and shared, read-only, by every camera.
/// </summary>
/// <remarks>
/// Triangles reference vertices by index and surfaces by index; surfaces name
/// materials by index into <see cref="MaterialNames"/>, which a material
/// library resolves separately (that is the IO step), so building a scene
/// from a BSP is pure computation and testable without any game content.
/// </remarks>
public sealed class PreviewScene
{
    /// <summary>Creates a scene.</summary>
    /// <param name="vertices">The vertices.</param>
    /// <param name="triangles">The triangles.</param>
    /// <param name="surfaces">The surfaces.</param>
    /// <param name="materialNames">The materials surfaces refer to, as the map spells them.</param>
    /// <param name="lightmaps">The face lightmaps.</param>
    /// <param name="entities">The map's entities.</param>
    /// <param name="props">The placed models.</param>
    /// <param name="spawns">The player spawns.</param>
    /// <param name="min">The world model's lower bound.</param>
    /// <param name="max">The world model's upper bound.</param>
    /// <param name="skyName">The 2D skybox name from worldspawn, or null.</param>
    public PreviewScene(
        PreviewVertex[] vertices,
        PreviewTriangle[] triangles,
        PreviewSurface[] surfaces,
        string[] materialNames,
        PreviewLightmap[] lightmaps,
        IReadOnlyList<BspEntity> entities,
        IReadOnlyList<PreviewProp> props,
        IReadOnlyList<PreviewSpawn> spawns,
        Vec3 min,
        Vec3 max,
        string? skyName)
    {
        Vertices = vertices;
        Triangles = triangles;
        Surfaces = surfaces;
        MaterialNames = materialNames;
        Lightmaps = lightmaps;
        Entities = entities;
        Props = props;
        Spawns = spawns;
        Min = min;
        Max = max;
        SkyName = skyName;
    }

    /// <summary>Gets the vertices.</summary>
    public PreviewVertex[] Vertices { get; }

    /// <summary>Gets the triangles.</summary>
    public PreviewTriangle[] Triangles { get; }

    /// <summary>Gets the surfaces.</summary>
    public PreviewSurface[] Surfaces { get; }

    /// <summary>Gets the material names surfaces refer to.</summary>
    public string[] MaterialNames { get; }

    /// <summary>Gets the lightmaps.</summary>
    public PreviewLightmap[] Lightmaps { get; }

    /// <summary>Gets the map's entities, in lump order.</summary>
    public IReadOnlyList<BspEntity> Entities { get; }

    /// <summary>Gets the placed models.</summary>
    public IReadOnlyList<PreviewProp> Props { get; }

    /// <summary>Gets the player spawns.</summary>
    public IReadOnlyList<PreviewSpawn> Spawns { get; }

    /// <summary>Gets the world model's lower bound.</summary>
    public Vec3 Min { get; }

    /// <summary>Gets the world model's upper bound.</summary>
    public Vec3 Max { get; }

    /// <summary>Gets the 2D skybox name, or null when worldspawn names none.</summary>
    public string? SkyName { get; }
}
