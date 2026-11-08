//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;

namespace SourceSharp.MapTools.Movement;

/// <summary>The result of sweeping a box through the world.</summary>
/// <param name="Fraction">How far along the sweep the box got before touching, 0..1.</param>
/// <param name="End">Where the box ended up (the start plus the fraction of the move).</param>
/// <param name="Normal">The plane it touched, facing the box; zero when it touched nothing.</param>
/// <param name="StartSolid">Whether the box began inside a solid.</param>
/// <param name="AllSolid">Whether the box stayed inside a solid for the whole sweep.</param>
/// <param name="Brush">The solid it touched (index into <see cref="HullTraceWorld.Solids"/>), or -1.</param>
public readonly record struct HullTrace(float Fraction, Vec3 End, Vec3 Normal, bool StartSolid, bool AllSolid, int Brush)
{
    /// <summary>Gets a value indicating whether the sweep touched anything.</summary>
    public bool Hit => Fraction < 1f || StartSolid;
}

/// <summary>
/// Sweeps the player's box through a map's player-solid collision: the
/// world's brushes (with the bevel planes the compiler adds), each
/// displacement's surface and each solid static prop's collision pieces.
/// </summary>
/// <remarks>
/// <para>
/// A box against a convex solid is the classic swept-box clip: each of the
/// solid's planes is pushed out by the box's extent along its normal, which
/// turns the box into a point against an expanded solid; the sweep enters
/// where it crosses the last entering plane and leaves at the first leaving
/// one. Positions stop <see cref="DistEpsilon"/> short of a surface, as the
/// engine's do, so a box resting on a floor is outside it, not touching.
/// </para>
/// <para>
/// Pushing planes out is exact on faces but rounds a solid's edges outward
/// into the planes' extensions unless the solid also has bevel planes there.
/// World brushes carry the compiler's bevels. Displacement and prop pieces do
/// not, so every solid is given its axis-aligned bounding planes as well:
/// they are valid supporting planes of any convex shape, so they never
/// change what a box touches except to remove that rounding at axial edges.
/// Slanted edges between two non-axial faces keep it; the calibration map
/// measures how often that matters.
/// </para>
/// <para>
/// The world is immutable once built and holds no per-trace state, so any
/// number of threads may trace through one instance at once.
/// </para>
/// </remarks>
public sealed class HullTraceWorld
{
    /// <summary>How far short of a surface a sweep stops, in units: the engine's distance epsilon.</summary>
    public const float DistEpsilon = 0.03125f;

    /// <summary>The edge of one cell of the spatial index, in units.</summary>
    public const double CellSize = 256;

    private readonly NavBrush[] _solids;
    private readonly Dictionary<(int, int), int[]> _cells;
    private readonly DNode[] _nodes = [];
    private readonly DPlane[] _planes = [];
    private readonly DLeaf[] _leaves = [];

    /// <summary>Builds a world over solids.</summary>
    /// <param name="solids">The convex solids the player collides with.</param>
    public HullTraceWorld(IReadOnlyList<NavBrush> solids)
        : this(solids, null)
    {
    }

    /// <summary>Builds a world over solids, with the map's BSP tree for telling open space from sealed void.</summary>
    /// <param name="solids">The convex solids the player collides with.</param>
    /// <param name="bsp">The compiled map whose tree answers <see cref="IsOpen"/>, or null to treat everywhere as open.</param>
    public HullTraceWorld(IReadOnlyList<NavBrush> solids, BspData? bsp)
    {
        ArgumentNullException.ThrowIfNull(solids);
        _solids = [.. solids];
        Dictionary<(int, int), List<int>> cells = [];
        for (int i = 0; i < _solids.Length; i++)
        {
            NavBrush s = _solids[i];
            for (int y = Cell(s.MinY); y <= Cell(s.MaxY); y++)
            {
                for (int x = Cell(s.MinX); x <= Cell(s.MaxX); x++)
                {
                    if (!cells.TryGetValue((x, y), out List<int>? list))
                    {
                        cells[(x, y)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }

        _cells = cells.ToDictionary(c => c.Key, c => c.Value.ToArray());
        if (bsp is not null)
        {
            _nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
            _planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
            _leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        }
    }

    /// <summary>
    /// Whether a point is in the map's open space: in a BSP leaf that is not
    /// solid. Space the compiler sealed off (inside the sky shell but
    /// unreachable from any entity) is filled solid in the tree though no
    /// brush is there, so a box traced through it meets nothing; this is what
    /// tells it from the playable world. A world built without a tree answers
    /// true everywhere.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>True when open.</returns>
    public bool IsOpen(Vec3 point)
    {
        if (_nodes.Length == 0 || _leaves.Length == 0)
        {
            return true;
        }

        int node = 0;
        for (int guard = 0; guard <= _nodes.Length && node >= 0; guard++)
        {
            DNode n = _nodes[node];
            DPlane plane = _planes[n.PlaneNum];
            node = Vec3.Dot(plane.Normal, point) - plane.Dist >= 0 ? n.Children[0] : n.Children[1];
        }

        int leaf = -1 - node;
        return leaf >= 0 && leaf < _leaves.Length && (_leaves[leaf].Contents & SolidContents) == 0;
    }

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    private const int SolidContents = 1;

    /// <summary>Gets the solids, in the order traces name them.</summary>
    public IReadOnlyList<NavBrush> Solids => _solids;

    /// <summary>
    /// Builds a map's player collision: its player-solid brushes, its
    /// displacements, and its solid static props, whose models come from the
    /// game content (a prop whose model the content lacks is left out, and
    /// named in the warnings).
    /// </summary>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="content">The game content for prop collision, or null to leave props out.</param>
    /// <param name="cancellationToken">Cancels loading the models.</param>
    /// <returns>The world and its warnings.</returns>
    public static async Task<(HullTraceWorld World, IReadOnlyList<string> Warnings)> FromBspAsync(
        BspData bsp, IContentFileSystem? content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?> hulls = new Dictionary<string, (Vec3, Vec3)?>();
        IReadOnlyDictionary<string, IReadOnlyList<NavPiece>?> pieces = new Dictionary<string, IReadOnlyList<NavPiece>?>();
        if (content is not null)
        {
            hulls = await NavModelBounds.LoadAsync(content, LevelNavFromBsp.Models(bsp), cancellationToken).ConfigureAwait(false);
            pieces = await NavModelBounds.LoadPiecesAsync(content, NavModelBounds.StaticPropModels(bsp), cancellationToken).ConfigureAwait(false);
        }

        (IReadOnlyList<NavBrush> solids, IReadOnlyList<string> warnings) = NavFloorCheck.Collision(
            bsp,
            m => hulls.TryGetValue(m, out (Vec3, Vec3)? hull) ? hull : null,
            m => pieces.TryGetValue(m, out IReadOnlyList<NavPiece>? found) ? found : null,
            Nav3dClipClass.Player);
        return (new HullTraceWorld(solids, bsp), warnings);
    }

    /// <summary>Sweeps a box from one position to another.</summary>
    /// <param name="start">Where the box's origin starts.</param>
    /// <param name="end">Where it would end with nothing in the way.</param>
    /// <param name="mins">The box's lower corner, relative to its origin.</param>
    /// <param name="maxs">The box's upper corner, relative to its origin.</param>
    /// <returns>The trace.</returns>
    public HullTrace Trace(Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs)
    {
        double fraction = 1;
        Vec3 normal = Vec3.Zero;
        bool startSolid = false, allSolid = false;
        int hit = -1;

        double loX = Math.Min(start.X, end.X) + mins.X - 1, hiX = Math.Max(start.X, end.X) + maxs.X + 1;
        double loY = Math.Min(start.Y, end.Y) + mins.Y - 1, hiY = Math.Max(start.Y, end.Y) + maxs.Y + 1;
        double loZ = Math.Min(start.Z, end.Z) + mins.Z - 1, hiZ = Math.Max(start.Z, end.Z) + maxs.Z + 1;
        HashSet<int>? seen = null;
        for (int cy = Cell(loY); cy <= Cell(hiY); cy++)
        {
            for (int cx = Cell(loX); cx <= Cell(hiX); cx++)
            {
                if (!_cells.TryGetValue((cx, cy), out int[]? list))
                {
                    continue;
                }

                foreach (int i in list)
                {
                    NavBrush s = _solids[i];
                    if (s.MaxX < loX || s.MinX > hiX || s.MaxY < loY || s.MinY > hiY || s.MaxZ < loZ || s.MinZ > hiZ)
                    {
                        continue;
                    }

                    seen ??= [];
                    if (!seen.Add(i))
                    {
                        continue;
                    }

                    ClipBox(s, start, end, mins, maxs, ref fraction, ref normal, ref startSolid, ref allSolid, ref hit, i);
                    if (allSolid)
                    {
                        return new HullTrace(0, start, normal, true, true, hit);
                    }
                }
            }
        }

        float f = (float)fraction;
        Vec3 stop = f >= 1f ? end : start + ((end - start) * f);
        return new HullTrace(f, stop, normal, startSolid, allSolid, hit);
    }

    /// <summary>Whether a box at a position is inside any solid.</summary>
    /// <param name="origin">The box's origin.</param>
    /// <param name="mins">The box's lower corner.</param>
    /// <param name="maxs">The box's upper corner.</param>
    /// <returns>True when it starts in a solid.</returns>
    public bool IsStuck(Vec3 origin, Vec3 mins, Vec3 maxs) => Trace(origin, origin, mins, maxs).StartSolid;

    /// <summary>
    /// Clips a swept box against one convex solid, updating the nearest hit so
    /// far. Planes are the solid's own and its six bounding planes.
    /// </summary>
    private static void ClipBox(
        NavBrush solid, Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs,
        ref double fraction, ref Vec3 normal, ref bool startSolid, ref bool allSolid, ref int hit, int index)
    {
        double enter = -1, leave = 1;
        (double X, double Y, double Z) clip = default;
        bool startOut = false, getOut = false;
        int planes = solid.PlaneCount;
        for (int p = 0; p < planes + 6; p++)
        {
            (double nx, double ny, double nz, double d) = p < planes ? solid.Plane(p) : Bounding(solid, p - planes);

            // Push the plane out by the box: the corner that reaches furthest
            // against the normal decides how far.
            double ox = nx < 0 ? maxs.X : mins.X;
            double oy = ny < 0 ? maxs.Y : mins.Y;
            double oz = nz < 0 ? maxs.Z : mins.Z;
            double dist = d - ((ox * nx) + (oy * ny) + (oz * nz));
            double d1 = (start.X * nx) + (start.Y * ny) + (start.Z * nz) - dist;
            double d2 = (end.X * nx) + (end.Y * ny) + (end.Z * nz) - dist;
            if (d2 > 0)
            {
                getOut = true;
            }

            if (d1 > 0)
            {
                startOut = true;
            }

            // Wholly in front of one plane: no contact with this solid.
            if (d1 > 0 && (d2 >= DistEpsilon || d2 >= d1))
            {
                return;
            }

            if (d1 <= 0 && d2 <= 0)
            {
                continue;
            }

            if (d1 > d2)
            {
                double f = (d1 - DistEpsilon) / (d1 - d2);
                if (f > enter)
                {
                    enter = f;
                    clip = (nx, ny, nz);
                }
            }
            else
            {
                double f = (d1 + DistEpsilon) / (d1 - d2);
                if (f < leave)
                {
                    leave = f;
                }
            }
        }

        if (!startOut)
        {
            startSolid = true;
            hit = index;
            if (!getOut)
            {
                allSolid = true;
                fraction = 0;
            }

            return;
        }

        if (enter < leave && enter > -1 && enter < fraction)
        {
            fraction = Math.Max(0, enter);
            normal = new Vec3((float)clip.X, (float)clip.Y, (float)clip.Z);
            hit = index;
        }
    }

    private static (double X, double Y, double Z, double D) Bounding(NavBrush s, int axis) => axis switch
    {
        0 => (1, 0, 0, s.MaxX),
        1 => (-1, 0, 0, -s.MinX),
        2 => (0, 1, 0, s.MaxY),
        3 => (0, -1, 0, -s.MinY),
        4 => (0, 0, 1, s.MaxZ),
        _ => (0, 0, -1, -s.MinZ),
    };

    private static int Cell(double v) => (int)Math.Floor(v / CellSize);
}
