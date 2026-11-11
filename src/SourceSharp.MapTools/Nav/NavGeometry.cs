//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>A dynamic obstacle as the room's compile found it: an entity the grid treats as open space and tags.</summary>
/// <param name="ClassName">Its classname.</param>
/// <param name="TargetName">Its <c>targetname</c> as written (possibly room-local), or null.</param>
/// <param name="HammerId">Its <c>hammerid</c>, or -1.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Brushes">Its solids: a brush entity's own brushes, or a prop's bounds as one box.</param>
public sealed record NavObstacleSource(
    string ClassName, string? TargetName, int HammerId, Nav3dObstacleKind Kind, IReadOnlyList<NavBrush> Brushes)
{
    /// <summary>The union of its brushes' bounds.</summary>
    public Box Bounds => new(
        new Vec3((float)Brushes.Min(b => b.MinX), (float)Brushes.Min(b => b.MinY), (float)Brushes.Min(b => b.MinZ)),
        new Vec3((float)Brushes.Max(b => b.MaxX), (float)Brushes.Max(b => b.MaxY), (float)Brushes.Max(b => b.MaxZ)));
}

/// <summary>
/// What the navigation is built from: the world's brushes (every contents:
/// solids, clips, water, ladders), the dynamic obstacles, and the ladder
/// volumes entities describe.
/// </summary>
/// <remarks>
/// <para>
/// <b>World brushes only are solid.</b> A compiled BSP holds the brushes of
/// every model: the world's, and each brush entity's. Only the world's are
/// static: a <c>func_door</c>'s brushes move, a <c>func_breakable</c>'s go
/// away, a trigger's never block. So the world's solids are the brushes the
/// world model's own tree reaches; those of an entity of a dynamic class
/// become that obstacle's solids, the rest (triggers, illusionaries, physics
/// clips) are dropped as nothing an agent walks into, and so are the brushes
/// no model's tree reaches: the compile writes every map brush to the lump,
/// a <c>func_viscluster</c>'s and a <c>func_occluder</c>'s among them, and
/// nothing collides with those. The compile stores the brushes
/// of an entity with an <c>origin</c> (a rotating door's origin brush gives
/// it one) relative to that origin, so they are moved by it, to where the
/// game places the model; its <c>angles</c> are not applied (a door's
/// angles name the way it moves, not a turn of its brushes).
/// </para>
/// <para>
/// <b>Props</b> (<c>prop_door_rotating</c>, <c>prop_physics</c>,
/// <c>prop_dynamic</c> and their variants) are point entities: they have no
/// brush, so they are never solid in the grid. Their model's movement hull,
/// turned by the entity's angles, is their obstacle box; a model the game's
/// content does not have is reported and left out.
/// </para>
/// <para>
/// <b>Ladders</b> come three ways, all flagging the voxels they overlap: a
/// <c>CONTENTS_LADDER</c> brush in the world, an <c>info_ladder</c>'s box
/// (<c>mins</c>/<c>maxs</c>, or the <c>mins.x</c> ... keys), and a
/// <c>func_useableladder</c>'s climb: the box spanning its <c>point0</c> and
/// <c>point1</c>, widened by 16 units, a player's half-width, since those are
/// where the player's origin runs while it climbs.
/// </para>
/// </remarks>
public sealed class NavGeometry
{
    /// <summary>A <c>func_useableladder</c>'s climb is widened this much on each side: a player's half-width.</summary>
    public const float LadderHalfWidth = 16f;

    /// <summary>The world's brushes, every contents: those the world model's tree holds.</summary>
    public required IReadOnlyList<NavBrush> Brushes { get; init; }

    /// <summary>The dynamic obstacles, in entity order.</summary>
    public IReadOnlyList<NavObstacleSource> Obstacles { get; init; } = [];

    /// <summary>The ladder volumes entities describe.</summary>
    public IReadOnlyList<Box> Ladders { get; init; } = [];

    /// <summary>What could not be read, one sentence each.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The same geometry with more world brushes: the solids a room assumes beyond its cell.</summary>
    /// <param name="extra">The brushes to add.</param>
    /// <returns>The geometry.</returns>
    public NavGeometry With(IEnumerable<NavBrush> extra) => new()
    {
        Brushes = [.. Brushes, .. extra],
        Obstacles = Obstacles,
        Ladders = Ladders,
        Warnings = Warnings,
    };

    /// <summary>The obstacle kind a classname is, or null when it is not a dynamic obstacle.</summary>
    /// <param name="className">The classname.</param>
    /// <returns>The kind, or null.</returns>
    public static Nav3dObstacleKind? KindOf(string className) => className.ToLowerInvariant() switch
    {
        "func_door" or "func_door_rotating" or "prop_door_rotating" => Nav3dObstacleKind.Door,
        "func_movelinear" or "func_train" or "func_tracktrain" or "func_rotating" or "func_plat" or "func_platrot" => Nav3dObstacleKind.Mover,
        "func_brush" or "func_wall_toggle" => Nav3dObstacleKind.Toggle,
        "func_breakable" or "func_breakable_surf" => Nav3dObstacleKind.Breakable,
        "func_physbox" or "func_physbox_multiplayer" or "prop_physics" or "prop_physics_override" or "prop_physics_multiplayer"
            or "prop_physics_respawnable" => Nav3dObstacleKind.Physics,
        "prop_dynamic" or "prop_dynamic_override" => Nav3dObstacleKind.Prop,
        _ => null,
    };

    /// <summary>The geometry of a compiled BSP.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <param name="modelBounds">A model's movement hull by its path as an entity names it, or null when unknown; null to leave props out.</param>
    /// <returns>The geometry.</returns>
    public static NavGeometry FromBsp(BspData bsp, Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds = null) => FromBsp(bsp, modelBounds, 1);

    /// <summary>The geometry of a compiled BSP, its brushes built on up to <paramref name="maxDegree"/> threads; the same at any degree.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <param name="modelBounds">A model's movement hull by its path as an entity names it, or null when unknown; null to leave props out.</param>
    /// <param name="maxDegree">How many brushes are built at once (<see cref="NavBrush.FromBspIndexed(BspData, int)"/>).</param>
    /// <returns>The geometry.</returns>
    public static NavGeometry FromBsp(BspData bsp, Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds, int maxDegree)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<(int Index, NavBrush Brush)> all = NavBrush.FromBspIndexed(bsp, maxDegree);
        Dictionary<int, NavBrush> byIndex = all.ToDictionary(b => b.Index, b => b.Brush);
        List<int>[] modelBrushes = ModelBrushes(bsp);

        List<NavObstacleSource> obstacles = [];
        List<Box> ladders = [];
        List<string> warnings = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            string className = entity.ClassName;
            string lower = className.ToLowerInvariant();
            if (lower == "info_ladder" && LadderBox(entity) is { } box)
            {
                ladders.Add(box);
                continue;
            }

            if (lower == "func_useableladder" && UseableLadder(entity) is { } climb)
            {
                ladders.Add(climb);
                continue;
            }

            if (KindOf(className) is not { } kind)
            {
                continue;
            }

            string? targetName = entity.Get("targetname") is { Length: > 0 } t ? t : null;
            int hammerId = int.TryParse(entity.Get("hammerid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1;
            string? model = entity.Get("model");
            List<NavBrush> solids = [];
            if (model is { Length: > 1 } && model[0] == '*')
            {
                if (int.TryParse(model.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index > 0
                    && index < modelBrushes.Length)
                {
                    // The compile stores the brushes of an entity with an
                    // origin relative to it, and the game places the model
                    // at the origin: move them back to where they stand.
                    Vec3 at = TryVector(entity.Get("origin"), out Vec3 origin) ? origin : default;
                    foreach (int b in modelBrushes[index])
                    {
                        if (byIndex.TryGetValue(b, out NavBrush? brush))
                        {
                            solids.Add(at == default ? brush : brush.Translated(at));
                        }
                    }
                }
            }
            else if (model is { Length: > 0 })
            {
                if (modelBounds?.Invoke(model) is { } hull && PropBox(entity, hull) is { } propBox)
                {
                    solids.Add(NavBrush.Box(propBox.Mins, propBox.Maxs, 1));
                }
                else
                {
                    warnings.Add($"{className} {(targetName ?? (hammerId >= 0 ? hammerId.ToString(CultureInfo.InvariantCulture) : "(unnamed)"))}: model \"{model}\" has no hull the content gives, so it is left out of the navigation's obstacles.");
                }
            }

            if (solids.Count > 0)
            {
                obstacles.Add(new NavObstacleSource(className, targetName, hammerId, kind, solids));
            }
        }

        return new NavGeometry
        {
            // The world's collision: the brushes its own tree's leaves name. The compile writes every
            // map brush to the lump (stock's EmitBrushes, writebsp.cpp), a func_viscluster's and a
            // func_occluder's too, which no model's tree holds and nothing collides with: taken for
            // the world's, ctf_2fort's doorways and the sides of its bridge were solid.
            Brushes = modelBrushes.Length == 0 ? [] : [.. modelBrushes[0].Where(byIndex.ContainsKey).Select(b => byIndex[b])],
            Obstacles = obstacles,
            Ladders = ladders,
            Warnings = warnings,
        };
    }

    /// <summary>The <c>CONTENTS_SOLID</c> bit a static prop's box is given.</summary>
    public const int StaticPropContents = 0x1;

    /// <summary>
    /// A compiled map's displacements as solids: each triangle of each
    /// displacement's surface, extruded <paramref name="depth"/> units into
    /// the side its base face's back is on, with the displacement's contents.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="depth">How far each triangle is extruded: the navigation's voxel, so the voxel the surface passes through is solid and the one above it stands on it.</param>
    /// <returns>The solids, displacement by displacement, triangle by triangle; a triangle tagged removed, or one with no area, adds none.</returns>
    /// <exception cref="SourceSharp.MapFormats.InvalidBspException">A displacement has no usable base face, or its lumps are out of range.</exception>
    /// <remarks>
    /// <para>
    /// <b>Why displacements need this.</b> A displacement is not a brush in
    /// the compiled map: the compile discards the brush whose side it
    /// replaced, and the game collides with the displacement's own triangle
    /// surface. Read as brushes alone, terrain and displaced floors are open
    /// space with nothing to stand on.
    /// </para>
    /// <para>
    /// <b>The shape.</b> A triangle facing up (its normal's z above
    /// <see cref="UpFacingNormalZ"/>) is extruded straight down to
    /// <paramref name="depth"/> below its lowest corner: its top is the
    /// surface, its sides are vertical, and its bottom is flat, so it has no
    /// sloped underside and is never an overhanging brush (which the file
    /// would list per voxel). Any other triangle (a wall or ceiling made of a
    /// displacement) is extruded along its normal, backwards: a ceiling's
    /// slab then has the sloped underside it really has. The surface is a
    /// thin shell, as it is in the game: space below a terrain displacement
    /// more than <paramref name="depth"/> down is open in the navigation as it
    /// is in the game, only unreachable.
    /// </para>
    /// <para>
    /// The surface's front is the side its base face faces, and each
    /// triangle's normal is turned to agree with it, so a triangle's winding
    /// does not decide which side is solid.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<NavBrush> Displacements(BspData bsp, double depth) => Displacements(bsp, depth, 1);

    /// <summary>
    /// <see cref="Displacements(BspData, double)"/> with the displacements'
    /// solids made on up to <paramref name="maxDegree"/> threads: each
    /// displacement's solids are made alone and the lists joined in
    /// displacement order, so the list is the same at any degree.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="depth">How far each triangle is extruded.</param>
    /// <param name="maxDegree">How many displacements are made at once.</param>
    /// <returns>The solids, displacement by displacement, triangle by triangle.</returns>
    public static IReadOnlyList<NavBrush> Displacements(BspData bsp, double depth, int maxDegree)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, 1);
        IReadOnlyList<Rad.DisplacementSurface> surfaces = Rad.DisplacementShadowCasters.Build(bsp);
        if (surfaces.Count == 0)
        {
            return [];
        }

        List<NavBrush>?[] each = new List<NavBrush>?[surfaces.Count];
        System.Runtime.ExceptionServices.ExceptionDispatchInfo?[] failed = new System.Runtime.ExceptionServices.ExceptionDispatchInfo?[surfaces.Count];
        System.Threading.Tasks.Parallel.For(0, surfaces.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegree }, i =>
        {
            try
            {
                each[i] = Solids(bsp, surfaces[i], depth);
            }
            catch (Exception e)
            {
                failed[i] = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            }
        });

        // A displacement that cannot be read throws as it did one at a
        // time: the first in order, as itself.
        List<NavBrush> result = [];
        for (int i = 0; i < surfaces.Count; i++)
        {
            failed[i]?.Throw();
            result.AddRange(each[i]!);
        }

        return result;
    }

    /// <summary>One displacement's solids, triangle by triangle: see <see cref="Displacements(BspData, double)"/>.</summary>
    private static List<NavBrush> Solids(BspData bsp, Rad.DisplacementSurface surface, double depth)
    {
        ReadOnlySpan<DispInfo> infos = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        ReadOnlySpan<DispTri> tags = BspStructView.As<DispTri>(bsp[BspLump.DispTris]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        List<NavBrush> result = [];
        DFace face = faces[surface.FaceIndex];
        DPlane plane = planes[face.PlaneNum];
        Vec3 front = face.Side == 0 ? plane.Normal : -plane.Normal;
        ReadOnlySpan<Vec3> vertices = surface.Vertices;
        ReadOnlySpan<int> indices = surface.TriangleIndices;
        int triangles = indices.Length / 3;
        int firstTag = infos[surface.Index].DispTriStart;
        bool tagged = firstTag >= 0 && firstTag + triangles <= tags.Length;
        for (int t = 0; t < triangles; t++)
        {
            if (tagged && (tags[firstTag + t].Tags & (ushort)DispTriTags.Remove) != 0)
            {
                continue;
            }

            if (Prism(vertices[indices[3 * t]], vertices[indices[(3 * t) + 1]], vertices[indices[(3 * t) + 2]], front, depth, (int)surface.Contents)
                is { } prism)
            {
                result.Add(prism);
            }
        }

        return result;
    }

    /// <summary>The least normal z of a displacement triangle extruded straight down; one below it is extruded along its normal.</summary>
    public const double UpFacingNormalZ = 0.01;

    /// <summary>A triangle extruded into a convex solid: see <see cref="Displacements(BspData, double)"/>.</summary>
    internal static NavBrush? Prism(Vec3 a, Vec3 b, Vec3 c, Vec3 front, double depth, int contents)
    {
        (double X, double Y, double Z) A = (a.X, a.Y, a.Z), B = (b.X, b.Y, b.Z), C = (c.X, c.Y, c.Z);
        (double X, double Y, double Z) n = Cross(Sub(B, A), Sub(C, A));
        double length = Math.Sqrt(Dot(n, n));
        if (length < 1e-6)
        {
            return null;
        }

        n = Scale(n, 1 / length);
        if (Dot(n, (front.X, front.Y, front.Z)) < 0)
        {
            n = Scale(n, -1);
        }

        List<(Vec3 Normal, float Dist)> faces = [(V(n), (float)Dot(n, A))];
        (double X, double Y, double Z) sweep;
        if (n.Z > UpFacingNormalZ)
        {
            double bottom = Math.Min(A.Z, Math.Min(B.Z, C.Z)) - depth;
            faces.Add((new Vec3(0, 0, -1), (float)-bottom));
            sweep = (0, 0, 1);
        }
        else
        {
            faces.Add((V(Scale(n, -1)), (float)(depth - Dot(n, A))));
            sweep = n;
        }

        // Each edge's side: the plane through the edge that holds the sweep
        // direction, turned away from the triangle's third corner.
        (double X, double Y, double Z)[] corners = [A, B, C];
        for (int e = 0; e < 3; e++)
        {
            (double X, double Y, double Z) p = corners[e];
            (double X, double Y, double Z) q = corners[(e + 1) % 3];
            (double X, double Y, double Z) r = corners[(e + 2) % 3];
            (double X, double Y, double Z) m = Cross(Sub(q, p), sweep);
            double size = Math.Sqrt(Dot(m, m));
            if (size < 1e-9)
            {
                return null;
            }

            m = Scale(m, 1 / size);
            if (Dot(m, Sub(r, p)) > 0)
            {
                m = Scale(m, -1);
            }

            faces.Add((V(m), (float)Dot(m, p)));
        }

        return NavBrush.FromPlanes(faces, contents);

        static (double X, double Y, double Z) Sub((double X, double Y, double Z) u, (double X, double Y, double Z) v) => (u.X - v.X, u.Y - v.Y, u.Z - v.Z);
        static (double X, double Y, double Z) Cross((double X, double Y, double Z) u, (double X, double Y, double Z) v) =>
            ((u.Y * v.Z) - (u.Z * v.Y), (u.Z * v.X) - (u.X * v.Z), (u.X * v.Y) - (u.Y * v.X));
        static double Dot((double X, double Y, double Z) u, (double X, double Y, double Z) v) => (u.X * v.X) + (u.Y * v.Y) + (u.Z * v.Z);
        static (double X, double Y, double Z) Scale((double X, double Y, double Z) u, double s) => (u.X * s, u.Y * s, u.Z * s);
        static Vec3 V((double X, double Y, double Z) u) => new((float)u.X, (float)u.Y, (float)u.Z);
    }

    /// <summary>
    /// A compiled map's static props as solids: each convex piece of each
    /// solid prop's collision, turned by its angles and placed, as the solid
    /// an agent stands on (<see cref="StandingShell"/>).
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="modelBounds">A model's movement hull by its path as the prop dictionary names it, or null when unknown.</param>
    /// <param name="modelPieces">
    /// A model's collision pieces by its path (<see cref="NavModelBounds.Pieces"/>),
    /// or null when unknown: a prop whose model has none is its hull, one
    /// box piece.
    /// </param>
    /// <returns>
    /// The solids in prop order, and one warning per model the content lacks
    /// naming how many props it leaves out. A prop with <c>SOLID_NONE</c> adds
    /// nothing, as it blocks nothing in the game.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Static props are not entities: the compile moves them into the
    /// <c>sprp</c> game lump, and the game makes them solid from their model.
    /// They never move, so they are world solids here, not obstacles.
    /// </para>
    /// <para>
    /// <b>The top is exact, the sides an over-estimate.</b> The game collides
    /// with a prop's <c>.phy</c> convex pieces. Each piece is solid up to its
    /// own upward faces, so a floor the navigation finds on a prop is one the
    /// game has; its sides are its outline straight down to its lowest
    /// corner, which can only make the navigation block more, never less. A
    /// box around each piece put floors over nothing beside a sloped, turned
    /// or rounded piece.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<NavBrush> Brushes, IReadOnlyList<string> Warnings) StaticProps(
        BspData bsp, Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds, Func<string, IReadOnlyList<NavPiece>?>? modelPieces = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        if (StaticPropLumpOf(bsp) is not { } lump)
        {
            return ([], []);
        }

        List<NavBrush> solids = [];
        SortedDictionary<string, int> missing = new(StringComparer.Ordinal);
        foreach (StaticProp prop in lump.Props)
        {
            if (prop.Solid == 0 || prop.PropType >= lump.ModelNames.Count)
            {
                continue;
            }

            string model = lump.ModelNames[prop.PropType];
            IReadOnlyList<NavPiece>? pieces = modelPieces?.Invoke(model) is { Count: > 0 } found ? found
                : modelBounds?.Invoke(model) is { } hull ? [NavPiece.Box(hull.Mins, hull.Maxs)]
                : null;
            if (pieces is null)
            {
                missing[model] = missing.GetValueOrDefault(model) + 1;
                continue;
            }

            foreach (NavPiece piece in pieces)
            {
                if (StandingShell(TurnedPoints(prop.Origin, prop.Angles, piece), piece.Triangles, StaticPropContents) is { } shell)
                {
                    solids.Add(shell);
                }
            }
        }

        return (solids, [.. missing.Select(m => string.Create(CultureInfo.InvariantCulture,
            $"static prop model \"{m.Key}\" has no hull the content gives, so its {m.Value} solid prop{(m.Value == 1 ? string.Empty : "s")} {(m.Value == 1 ? "is" : "are")} left out of the navigation's solids."))]);
    }

    /// <summary>A map's static prop lump, or null when it has none.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The lump.</returns>
    /// <exception cref="SourceSharp.MapFormats.InvalidBspException">The lump is malformed.</exception>
    public static StaticPropLump? StaticPropLumpOf(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id == GameLumpId.MakeId(GameLumpId.StaticProps))
            {
                return StaticPropLump.Read(entry);
            }
        }

        return null;
    }

    /// <summary>Each model's brush indices, found by walking its tree to its leaves' brush lists.</summary>
    /// <remarks>
    /// The leaves are read in the layout the lump's own version names: a map
    /// whose leaf lump is version 0 (stock maps of BSP version 19, such as
    /// <c>dm_lockdown</c>) keeps each leaf's ambient cube inline, 56 bytes a
    /// leaf against version 1's 32. Read as version 1, its brush lists came
    /// out of the wrong bytes and the world lost brushes to the entities.
    /// </remarks>
    private static List<int>[] ModelBrushes(BspData bsp)
    {
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        (ushort First, ushort Count)[] leafs = LeafBrushRuns(bsp);
        ReadOnlySpan<ushort> leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]);
        List<int>[] result = new List<int>[models.Length];
        Stack<int> pending = new();
        for (int m = 0; m < models.Length; m++)
        {
            SortedSet<int> brushes = [];
            pending.Clear();
            pending.Push(models[m].HeadNode);
            while (pending.Count > 0)
            {
                int node = pending.Pop();
                if (node < 0)
                {
                    int leaf = -(node + 1);
                    if (leaf < leafs.Length)
                    {
                        for (int b = 0; b < leafs[leaf].Count; b++)
                        {
                            int at = leafs[leaf].First + b;
                            if (at < leafBrushes.Length)
                            {
                                brushes.Add(leafBrushes[at]);
                            }
                        }
                    }

                    continue;
                }

                if (node < nodes.Length)
                {
                    pending.Push(nodes[node].Children[1]);
                    pending.Push(nodes[node].Children[0]);
                }
            }

            result[m] = [.. brushes];
        }

        return result;
    }

    /// <summary>Each leaf's run in the leaf-brush list, from either leaf layout.</summary>
    private static (ushort First, ushort Count)[] LeafBrushRuns(BspData bsp)
    {
        BspLumpData lump = bsp[BspLump.Leafs];
        if (lump.Version == 0)
        {
            ReadOnlySpan<DLeafVersion0> old = BspStructView.As<DLeafVersion0>(lump);
            (ushort, ushort)[] runs = new (ushort, ushort)[old.Length];
            for (int i = 0; i < old.Length; i++)
            {
                runs[i] = (old[i].FirstLeafBrush, old[i].NumLeafBrushes);
            }

            return runs;
        }

        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(lump);
        (ushort, ushort)[] result = new (ushort, ushort)[leafs.Length];
        for (int i = 0; i < leafs.Length; i++)
        {
            result[i] = (leafs[i].FirstLeafBrush, leafs[i].NumLeafBrushes);
        }

        return result;
    }

    /// <summary>An <c>info_ladder</c>'s box: <c>mins</c> and <c>maxs</c>, or their <c>.x</c>, <c>.y</c>, <c>.z</c> keys.</summary>
    internal static Box? LadderBox(BspEntity entity)
    {
        Vec3? Corner(string name)
        {
            if (TryVector(entity.Get(name), out Vec3 v))
            {
                return v;
            }

            float[] parts = new float[3];
            string[] axes = ["x", "y", "z"];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(entity.Get($"{name}.{axes[i]}"), NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i]))
                {
                    return null;
                }
            }

            return new Vec3(parts[0], parts[1], parts[2]);
        }

        if (Corner("mins") is not { } a || Corner("maxs") is not { } b)
        {
            return null;
        }

        return new Box(Min(a, b), Max(a, b));
    }

    /// <summary>A <c>func_useableladder</c>'s climb: the box spanning its two points, widened by a player's half-width.</summary>
    internal static Box? UseableLadder(BspEntity entity)
    {
        if (!TryVector(entity.Get("point0"), out Vec3 a) || !TryVector(entity.Get("point1"), out Vec3 b))
        {
            return null;
        }

        Vec3 grow = new(LadderHalfWidth, LadderHalfWidth, 0);
        return new Box(Min(a, b) - grow, Max(a, b) + grow);
    }

    /// <summary>A prop's obstacle box: its model's hull turned by its angles and moved to its origin; the box of the turned corners.</summary>
    internal static Box? PropBox(BspEntity entity, (Vec3 Mins, Vec3 Maxs) hull)
    {
        if (!TryVector(entity.Get("origin"), out Vec3 origin))
        {
            return null;
        }

        Vec3 angles = TryVector(entity.Get("angles"), out Vec3 a) ? a : default;
        return TurnedBox(origin, angles, hull);
    }

    /// <summary>A hull turned by Source angles (pitch, yaw, roll) and moved to an origin: the box of the turned corners, or null when it is empty.</summary>
    internal static Box? TurnedBox(Vec3 origin, Vec3 angles, (Vec3 Mins, Vec3 Maxs) hull)
    {
        double[] m = TurnMatrix(angles);
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        for (int c = 0; c < 8; c++)
        {
            double px = (c & 1) == 0 ? hull.Mins.X : hull.Maxs.X;
            double py = (c & 2) == 0 ? hull.Mins.Y : hull.Maxs.Y;
            double pz = (c & 4) == 0 ? hull.Mins.Z : hull.Maxs.Z;
            for (int k = 0; k < 3; k++)
            {
                double v = (m[3 * k] * px) + (m[(3 * k) + 1] * py) + (m[(3 * k) + 2] * pz);
                lo[k] = Math.Min(lo[k], v);
                hi[k] = Math.Max(hi[k], v);
            }
        }

        Vec3 mins = new((float)(origin.X + lo[0]), (float)(origin.Y + lo[1]), (float)(origin.Z + lo[2]));
        Vec3 maxs = new((float)(origin.X + hi[0]), (float)(origin.Y + hi[1]), (float)(origin.Z + hi[2]));
        return mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z ? new Box(mins, maxs) : null;
    }

    /// <summary>
    /// A piece's corners turned by Source angles and moved to an origin, in
    /// double: the same turn <see cref="TurnedBox"/> gives a hull.
    /// </summary>
    internal static (double X, double Y, double Z)[] TurnedPoints(Vec3 origin, Vec3 angles, NavPiece piece)
    {
        double[] m = TurnMatrix(angles);
        (double X, double Y, double Z)[] points = new (double, double, double)[piece.Points.Count];
        for (int i = 0; i < points.Length; i++)
        {
            Vec3 p = piece.Points[i];
            points[i] = (
                origin.X + ((m[0] * p.X) + (m[1] * p.Y) + (m[2] * p.Z)),
                origin.Y + ((m[3] * p.X) + (m[4] * p.Y) + (m[5] * p.Z)),
                origin.Z + ((m[6] * p.X) + (m[7] * p.Y) + (m[8] * p.Z)));
        }

        return points;
    }

    /// <summary>
    /// A convex piece as a brush: one plane per face, each turned away from
    /// the corners' centroid, so the brush is the piece itself. Faces that
    /// share a plane give it once; a face with no area gives none.
    /// </summary>
    /// <param name="points">The piece's corners, placed.</param>
    /// <param name="triangles">Its faces, three corner indices each.</param>
    /// <param name="contents">The brush's contents.</param>
    /// <returns>The brush, or null when the faces bound no volume.</returns>
    internal static NavBrush? ConvexBrush(IReadOnlyList<(double X, double Y, double Z)> points, IReadOnlyList<int> triangles, int contents) =>
        PlaneBrush([.. FacePlanes(points, triangles).Select(p => (p.X, p.Y, p.Z))], points, contents);

    /// <summary>The outward unit planes of a convex piece's faces, distinct, in face order.</summary>
    internal static List<(double X, double Y, double Z, double D)> FacePlanes(IReadOnlyList<(double X, double Y, double Z)> points, IReadOnlyList<int> triangles)
    {
        double cx = 0, cy = 0, cz = 0;
        foreach ((double x, double y, double z) in points)
        {
            cx += x;
            cy += y;
            cz += z;
        }

        cx /= Math.Max(1, points.Count);
        cy /= Math.Max(1, points.Count);
        cz /= Math.Max(1, points.Count);
        List<(double X, double Y, double Z, double D)> planes = [];
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            (double X, double Y, double Z) a = points[triangles[t]];
            (double X, double Y, double Z) b = points[triangles[t + 1]];
            (double X, double Y, double Z) c = points[triangles[t + 2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
            double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (length < 1e-9)
            {
                continue;
            }

            nx /= length;
            ny /= length;
            nz /= length;
            double d = (nx * a.X) + (ny * a.Y) + (nz * a.Z);
            if ((nx * cx) + (ny * cy) + (nz * cz) > d)
            {
                (nx, ny, nz, d) = (-nx, -ny, -nz, -d);
            }

            if (!planes.Any(p => Math.Abs(p.X - nx) < 1e-6 && Math.Abs(p.Y - ny) < 1e-6 && Math.Abs(p.Z - nz) < 1e-6 && Math.Abs(p.D - d) < 1e-4))
            {
                planes.Add((nx, ny, nz, d));
            }
        }

        return planes;
    }

    /// <summary>
    /// A brush of planes that each support a set of points: every normal is
    /// rounded to <c>float32</c>, and its distance is the farthest point
    /// along that rounded normal, rounded up, so the brush holds every point
    /// whatever the rounding.
    /// </summary>
    /// <param name="normals">The outward normals, unit length.</param>
    /// <param name="points">The points the brush must hold.</param>
    /// <param name="contents">Its contents.</param>
    /// <returns>The brush, or null when the planes bound no volume.</returns>
    internal static NavBrush? PlaneBrush(
        IReadOnlyList<(double X, double Y, double Z)> normals, IReadOnlyList<(double X, double Y, double Z)> points, int contents)
    {
        List<(Vec3 Normal, float Dist)> faces = new(normals.Count);
        foreach ((double x, double y, double z) in normals)
        {
            Vec3 n = new((float)x, (float)y, (float)z);
            double d = double.MinValue;
            foreach ((double px, double py, double pz) in points)
            {
                d = Math.Max(d, (n.X * px) + (n.Y * py) + (n.Z * pz));
            }

            float dist = (float)d;
            if (dist < d)
            {
                dist = MathF.BitIncrement(dist);
            }

            faces.Add((n, dist));
        }

        return NavBrush.FromPlanes(faces, contents);
    }

    /// <summary>
    /// A convex piece as the solid the navigation stands an agent on: its
    /// upward faces over its footprint, down to its lowest corner.
    /// </summary>
    /// <param name="points">The piece's corners, placed.</param>
    /// <param name="triangles">Its faces, three corner indices each.</param>
    /// <param name="contents">The brush's contents.</param>
    /// <returns>The brush, or null when the piece has no volume.</returns>
    /// <remarks>
    /// <para>
    /// <b>Its top is the piece's top.</b> Over any point of the piece's
    /// footprint (the outline of its corners seen from above), the highest
    /// solid is where the piece's upward faces are, exactly; a floor the
    /// navigation finds on it is a floor the game's collision has. A box
    /// around the piece put its top everywhere over the box: over the low end
    /// of a slope or a heap, beside a turned bar, and over the air past a
    /// rounded edge, floors that were not there (on <c>dm_lockdown</c>, 61
    /// places a player stood with nothing within a step under it).
    /// </para>
    /// <para>
    /// <b>Its sides and underside are over-estimated.</b> The sides are
    /// the footprint's edges, straight up, and the bottom is flat at the
    /// lowest corner, so the space under an overhanging face is solid. That
    /// holds the whole piece (the navigation never blocks less than the
    /// game), and leaves no downward sloped face, so the file never lists it
    /// as an overhanging brush (section 5.3 of the format). Only the side
    /// is over-estimated, which no floor stands on.
    /// </para>
    /// <para>
    /// A normal within a millionth of an axis is snapped to it, so a box
    /// piece read from a <c>.phy</c>'s float corners, or turned by a quarter
    /// turn, is an axial box brush; snapping keeps every plane outside the
    /// corners (<see cref="PlaneBrush"/>), so it never cuts the piece.
    /// </para>
    /// </remarks>
    internal static NavBrush? StandingShell(IReadOnlyList<(double X, double Y, double Z)> points, IReadOnlyList<int> triangles, int contents)
    {
        List<(double X, double Y, double Z)> normals = [];
        void Add(double x, double y, double z)
        {
            x = Math.Abs(x) < AxisSnap ? 0 : x;
            y = Math.Abs(y) < AxisSnap ? 0 : y;
            z = Math.Abs(z) < AxisSnap ? 0 : z;
            double length = Math.Sqrt((x * x) + (y * y) + (z * z));
            (double X, double Y, double Z) n = (x / length, y / length, z / length);
            if (!normals.Any(m => Math.Abs(m.X - n.X) < 1e-9 && Math.Abs(m.Y - n.Y) < 1e-9 && Math.Abs(m.Z - n.Z) < 1e-9))
            {
                normals.Add(n);
            }
        }

        foreach ((double x, double y, double z, _) in FacePlanes(points, triangles))
        {
            if (z >= AxisSnap)
            {
                Add(x, y, z);
            }
        }

        List<(double X, double Y)> outline = Outline(points);
        if (normals.Count == 0 || outline.Count < 3)
        {
            return null;
        }

        for (int i = 0; i < outline.Count; i++)
        {
            (double X, double Y) a = outline[i];
            (double X, double Y) b = outline[(i + 1) % outline.Count];
            Add(b.Y - a.Y, a.X - b.X, 0);
        }

        Add(0, 0, -1);
        if (normals.All(n => (n.X != 0 ? 1 : 0) + (n.Y != 0 ? 1 : 0) + (n.Z != 0 ? 1 : 0) == 1))
        {
            double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
            double[] hi = [double.MinValue, double.MinValue, double.MinValue];
            foreach ((double x, double y, double z) in points)
            {
                lo = [Math.Min(lo[0], x), Math.Min(lo[1], y), Math.Min(lo[2], z)];
                hi = [Math.Max(hi[0], x), Math.Max(hi[1], y), Math.Max(hi[2], z)];
            }

            Vec3 mins = new((float)lo[0], (float)lo[1], (float)lo[2]);
            Vec3 maxs = new((float)hi[0], (float)hi[1], (float)hi[2]);
            return mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z ? NavBrush.Box(mins, maxs, contents) : null;
        }

        return PlaneBrush(normals, points, contents);
    }

    /// <summary>How close to an axis a normal is snapped onto it.</summary>
    private const double AxisSnap = 1e-6;

    /// <summary>The outline of points seen from above: their convex hull in x and y, counter-clockwise, no three in a line.</summary>
    private static List<(double X, double Y)> Outline(IReadOnlyList<(double X, double Y, double Z)> points)
    {
        List<(double X, double Y)> sorted = [.. points.Select(p => (p.X, p.Y)).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y)];
        if (sorted.Count < 3)
        {
            return sorted;
        }

        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

        List<(double X, double Y)> hull = [];
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach ((double X, double Y) p in pass == 0 ? sorted : Enumerable.Reverse(sorted))
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 1e-9)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    /// <summary>The rotation of Source angles (pitch, yaw, roll) as a row-major 3 x 3 matrix, exact at quarter turns.</summary>
    private static double[] TurnMatrix(Vec3 angles)
    {
        (double sp, double cp) = SinCos(angles.X);
        (double sy, double cy) = SinCos(angles.Y);
        (double sr, double cr) = SinCos(angles.Z);

        // Source's angle order: yaw about z, pitch about y, roll about x.
        return
        [
            cp * cy, (sr * sp * cy) - (cr * sy), (cr * sp * cy) + (sr * sy),
            cp * sy, (sr * sp * sy) + (cr * cy), (cr * sp * sy) - (sr * cy),
            -sp, sr * cp, cr * cp,
        ];
    }

    /// <summary>
    /// An entity key's vector: three numbers separated by spaces, as the
    /// entity lump writes <c>origin</c>, <c>angles</c> and the ladder points.
    /// Not the VMF parser's bracketed <c>[x y z]</c> form, which entity keys
    /// never use; anything but exactly three numbers is no vector.
    /// </summary>
    internal static bool TryVector(string? text, out Vec3 vector)
    {
        vector = default;
        if (text is null)
        {
            return false;
        }

        Span<Range> parts = stackalloc Range[4];
        ReadOnlySpan<char> span = text.AsSpan();
        if (span.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) != 3)
        {
            return false;
        }

        Span<float> v = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(span[parts[i]], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
            {
                return false;
            }
        }

        vector = new Vec3(v[0], v[1], v[2]);
        return true;
    }

    private static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    private static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>A sine and cosine of degrees, exact at quarter turns so a prop turned by one keeps an exact box.</summary>
    private static (double Sin, double Cos) SinCos(double degrees)
    {
        double turns = degrees / 90.0;
        if (turns == Math.Floor(turns))
        {
            int q = (int)(((long)turns % 4 + 4) % 4);
            return q switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
        }

        double radians = degrees * (Math.PI / 180.0);
        return (DetMath.Sin(radians), DetMath.Cos(radians));
    }
}
