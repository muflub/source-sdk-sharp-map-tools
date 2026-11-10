//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Scene;

namespace SourceSharp.MapPreview.Cameras;

/// <summary>A camera the planner chose, with what the view leaves out.</summary>
/// <param name="Camera">The camera.</param>
/// <param name="Filter">The cutaway, if any.</param>
public readonly record struct PlannedView(PreviewCamera Camera, ViewFilter Filter);

/// <summary>What the planner produces.</summary>
public sealed record CameraPlanOptions
{
    /// <summary>Gets the default plan: overview, isometric and up to four spawns.</summary>
    public static CameraPlanOptions Default { get; } = new();

    /// <summary>Gets the image width.</summary>
    public int Width { get; init; } = 1024;

    /// <summary>Gets the image height.</summary>
    public int Height { get; init; } = 768;

    /// <summary>Gets the most spawn views to plan; one per team first.</summary>
    public int MaxSpawns { get; init; } = 4;

    /// <summary>Gets the player's eye height above the spawn origin.</summary>
    public float EyeHeight { get; init; } = 64f;

    /// <summary>Gets the field of view for player's-eye views, horizontal degrees.</summary>
    public float FovDegrees { get; init; } = 90f;

    /// <summary>
    /// Gets how far above the highest spawn the overview cuts geometry away,
    /// so roofs do not hide interiors.
    /// </summary>
    public float CutAboveSpawn { get; init; } = 200f;
}

/// <summary>
/// Chooses the standard views of a map: a top-down overview with a roof
/// cutaway, an isometric overview, and player's-eye views from the spawns.
/// </summary>
/// <remarks>
/// Every choice is a pure function of the scene and the options, so the same
/// map always gets the same cameras and a diff against a previous preview
/// compares like with like.
/// </remarks>
public static class CameraPlanner
{
    /// <summary>Margin added around the content when framing an overview.</summary>
    public const float FrameMargin = 1.05f;

    /// <summary>Plans the views.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="options">The plan options.</param>
    /// <returns>The views, overview first.</returns>
    public static IReadOnlyList<PlannedView> Plan(PreviewScene scene, CameraPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        CameraPlanOptions o = options ?? CameraPlanOptions.Default;
        (Vec3 min, Vec3 max) = ContentBounds(scene);
        List<PlannedView> views = [];

        float? cut = scene.Spawns.Count > 0 ? scene.Spawns.Max(s => s.Origin.Z) + o.CutAboveSpawn : null;
        views.Add(new PlannedView(Overview(min, max, o.Width, o.Height, cut), new ViewFilter(cut)));
        views.Add(new PlannedView(Isometric(min, max, o.Width, o.Height), new ViewFilter(null)));

        foreach ((PreviewSpawn spawn, int n) in PickSpawns(scene.Spawns, o.MaxSpawns).Select((s, i) => (s, i)))
        {
            string team = spawn.Team switch { 2 => "red", 3 => "blue", 0 => string.Empty, _ => "team " + spawn.Team.ToString(CultureInfo.InvariantCulture) };
            string named = spawn.Name.Length > 0 ? $" \"{spawn.Name}\"" : string.Empty;
            string description = $"spawn {spawn.ClassName}{named} {team}".TrimEnd() + $" at {Format(spawn.Origin)} yaw {spawn.Yaw:0}";
            PreviewCamera camera = PreviewCamera.FromAngles(
                $"spawn{n + 1}",
                CameraKind.Spawn,
                spawn.Origin + new Vec3(0, 0, o.EyeHeight),
                8f,
                spawn.Yaw,
                o.FovDegrees,
                o.Width,
                o.Height,
                description);
            views.Add(new PlannedView(camera, new ViewFilter(null)));
        }

        return views;
    }

    /// <summary>
    /// The bounds of what a player can see: every non-sky surface's vertices.
    /// The world model's own bounds include the skybox shell, which would
    /// frame the overview around empty sky.
    /// </summary>
    /// <param name="scene">The scene.</param>
    /// <returns>The bounds; the world bounds when there is nothing else.</returns>
    public static (Vec3 Min, Vec3 Max) ContentBounds(PreviewScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        bool any = false;
        foreach (PreviewTriangle tri in scene.Triangles)
        {
            PreviewSurface surface = scene.Surfaces[tri.Surface];
            if (surface.Sky || surface.SkyboxArea || surface.Kind == SurfaceKind.ScaleFigure)
            {
                continue;
            }

            foreach (int v in (ReadOnlySpan<int>)[tri.A, tri.B, tri.C])
            {
                Vec3 p = scene.Vertices[v].Position;
                minX = MathF.Min(minX, p.X);
                minY = MathF.Min(minY, p.Y);
                minZ = MathF.Min(minZ, p.Z);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
                maxZ = MathF.Max(maxZ, p.Z);
                any = true;
            }
        }

        return any ? (new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ)) : (scene.Min, scene.Max);
    }

    /// <summary>A top-down orthographic camera framing the bounds, north up.</summary>
    /// <param name="min">Lower bound.</param>
    /// <param name="max">Upper bound.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="cut">The cutaway height, for the description.</param>
    /// <returns>The camera.</returns>
    public static PreviewCamera Overview(Vec3 min, Vec3 max, int width, int height, float? cut)
    {
        Vec3 centre = (min + max) * 0.5f;
        float halfY = (max.Y - min.Y) * 0.5f;
        float halfX = (max.X - min.X) * 0.5f;
        float half = MathF.Max(halfY, halfX * height / width) * FrameMargin;
        string description = cut is { } z ? $"overview top-down, cut above z {z:0}" : "overview top-down";
        return new PreviewCamera(
            "overview",
            CameraKind.Overview,
            new Vec3(centre.X, centre.Y, max.Z + 64f),
            new Vec3(0, 0, -1),
            new Vec3(0, 1, 0),
            0,
            MathF.Max(half, 1f),
            width,
            height,
            description);
    }

    /// <summary>
    /// An orthographic three-quarter view from the south-west, looking 35
    /// degrees down, framed so the whole box fits.
    /// </summary>
    /// <param name="min">Lower bound.</param>
    /// <param name="max">Upper bound.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <returns>The camera.</returns>
    public static PreviewCamera Isometric(Vec3 min, Vec3 max, int width, int height)
    {
        Vec3 centre = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 1f);
        Vec3 forward = PreviewCamera.Direction(35f, 45f);
        PreviewCamera probe = new("iso", CameraKind.Isometric, centre - (forward * (radius * 2f)), forward, new Vec3(0, 0, 1), 0, 1f, width, height, string.Empty);
        CameraView view = probe.View();
        float loX = float.MaxValue, hiX = float.MinValue, loY = float.MaxValue, hiY = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            Vec3 p = new((corner & 1) != 0 ? max.X : min.X, (corner & 2) != 0 ? max.Y : min.Y, (corner & 4) != 0 ? max.Z : min.Z);
            Vec3 v = view.ToView(p);
            loX = MathF.Min(loX, v.X);
            hiX = MathF.Max(hiX, v.X);
            loY = MathF.Min(loY, v.Y);
            hiY = MathF.Max(hiY, v.Y);
        }

        Vec3 shift = (view.Right * ((loX + hiX) * 0.5f)) + (view.Up * ((loY + hiY) * 0.5f));
        float half = MathF.Max((hiY - loY) * 0.5f, (hiX - loX) * 0.5f * height / width) * FrameMargin;
        return probe with
        {
            Position = probe.Position + shift,
            OrthoHalfHeight = MathF.Max(half, 1f),
            Description = "overview isometric from south-west",
        };
    }

    /// <summary>
    /// How far apart two spawns facing the same way must stand for both to
    /// get a view: spawns are laid out in rows a player width or two apart,
    /// and four views from one row show the same street four times.
    /// </summary>
    public const float SpawnViewSpread = 384f;

    /// <summary>How far apart in yaw two spawns' views must look to count as different views from the same place.</summary>
    public const float SpawnViewTurn = 45f;

    /// <summary>
    /// Picks spawns to view, so the views show different places: the first
    /// of each team in lump order; then the first of each named group of a
    /// team not yet shown (a map's arrival spawns, say); then, while under
    /// the cap, the spawn farthest from those picked, as long as it does not
    /// stand within <see cref="SpawnViewSpread"/> of a picked spawn facing
    /// within <see cref="SpawnViewTurn"/> of the same way.
    /// </summary>
    /// <param name="spawns">The map's spawns.</param>
    /// <param name="max">The most to pick.</param>
    /// <returns>The picked spawns.</returns>
    public static IReadOnlyList<PreviewSpawn> PickSpawns(IReadOnlyList<PreviewSpawn> spawns, int max)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        List<PreviewSpawn> picked = [];
        HashSet<int> teams = [];
        foreach (PreviewSpawn spawn in spawns)
        {
            if (picked.Count < max && teams.Add(spawn.Team))
            {
                picked.Add(spawn);
            }
        }

        HashSet<(int, string)> groups = [.. picked.Select(s => (s.Team, s.Name))];
        foreach (PreviewSpawn spawn in spawns)
        {
            if (picked.Count < max && groups.Add((spawn.Team, spawn.Name)))
            {
                picked.Add(spawn);
            }
        }

        while (picked.Count < max)
        {
            PreviewSpawn? next = null;
            float best = -1;
            foreach (PreviewSpawn spawn in spawns)
            {
                if (picked.Contains(spawn) || picked.Any(p => SameView(p, spawn)))
                {
                    continue;
                }

                float nearest = picked.Count == 0 ? float.MaxValue : picked.Min(p => (p.Origin - spawn.Origin).LengthSquared());
                if (nearest > best)
                {
                    (next, best) = (spawn, nearest);
                }
            }

            if (next is not { } chosen)
            {
                break;
            }

            picked.Add(chosen);
        }

        return picked;
    }

    /// <summary>Whether two spawns' views would show nearly the same thing: close together and facing nearly the same way.</summary>
    private static bool SameView(PreviewSpawn a, PreviewSpawn b)
    {
        float turn = MathF.Abs(((a.Yaw - b.Yaw) % 360f + 540f) % 360f - 180f);
        return (a.Origin - b.Origin).LengthSquared() < SpawnViewSpread * SpawnViewSpread && turn < SpawnViewTurn;
    }

    private static string Format(Vec3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"({v.X:0} {v.Y:0} {v.Z:0})");
}
