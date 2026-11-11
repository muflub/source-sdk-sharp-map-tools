//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// One convex brush as the navigation tests it: its bounding planes, its
/// corners, its edge directions and its contents, with the exact test of
/// whether an axis-aligned box overlaps it and how far a box can grow before
/// it does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why brushes.</b> The engine's movement code sweeps an axis-aligned box
/// against the world's brushes (with their bevel planes), not against the
/// drawn faces, so the free space an agent has is exactly the space its box
/// can occupy without overlapping a brush of a contents it collides with.
/// The navigation works on the compiled BSP's brushes for that reason: they
/// are what a trace in the game sees, clips and grates included, which the
/// faces are not.
/// </para>
/// <para>
/// <b>An exact overlap test.</b> A box overlaps a convex brush exactly when
/// no separating axis exists among the box's three axes, the brush's plane
/// normals, and the cross products of the brush's edges with the box's
/// axes (the separating axis theorem for two convex polyhedra). All three
/// families are tested, so a slope or a wedge is neither too fat nor too
/// thin. Touching is not overlapping: an agent standing on a floor touches
/// it, and a separation of less than <see cref="Epsilon"/> counts as
/// touching, which absorbs the float rounding of a compiled plane.
/// </para>
/// <para>
/// <b>Why it lives beside the reader.</b> The <c>.nav3d</c> file answers most
/// clearance questions from stored numbers, but a brush with a sloped
/// underside (an overhang) cannot be summed up by numbers that are exact for
/// every agent size, so the file stores such a brush's planes and the reader
/// runs this very test on it (<see cref="Nav3dReader"/>). One implementation,
/// in the dependency-free assembly the game mod references, means the
/// compile and the game decide those voxels with the same arithmetic.
/// </para>
/// <para>
/// Arithmetic is in double and uses only IEEE-exact operations (add,
/// multiply, divide, square root), in a fixed order, so the answer is the
/// same on every machine. The separating axes are worked out once, when the
/// brush is made, so a test allocates nothing.
/// </para>
/// </remarks>
public sealed class NavBrush
{
    /// <summary>How close two solids may come and still count as touching, not overlapping, in units.</summary>
    public const double Epsilon = 1e-3;

    // Where the corners' coordinates are rounded to find duplicates.
    private const double VertexWeld = 1e-4;

    // Half the side of the square each plane's winding starts from: beyond
    // any map's extent.
    private const double Huge = 1 << 20;

    private readonly double[] _planes;
    private readonly double[] _vertices;
    private readonly double[] _edges;

    // The edge axes (edge × box axis, unit length) with the brush's extent
    // along each: x, y, z, lo, hi per axis.
    private readonly double[] _edgeAxes;

    // How fast a box growing sideways (NavGrowth.Sideways) widens along each
    // plane normal and each edge axis: the rate the growth test works out
    // per call, worked out once.
    private readonly double[] _planeSidewaysRates;
    private readonly double[] _edgeSidewaysRates;

    // The planes and edge axes whose normal has a z part: the only ones
    // whose projection of a column's voxels changes from voxel to voxel
    // (PrepareSidewaysColumn).
    private readonly int[] _zPlanes;
    private readonly int[] _zEdges;

    private NavBrush(double[] planes, double[] vertices, double[] edges, int contents)
    {
        _planes = planes;
        _vertices = vertices;
        _edges = edges;
        Contents = contents;
        IsAxial = true;
        for (int i = 0; i < planes.Length; i += 4)
        {
            int nonZero = (planes[i] != 0 ? 1 : 0) + (planes[i + 1] != 0 ? 1 : 0) + (planes[i + 2] != 0 ? 1 : 0);
            IsAxial &= nonZero == 1;
            IsOverhang |= planes[i + 2] < 0 && (planes[i] != 0 || planes[i + 1] != 0);
        }

        MinX = MinY = MinZ = double.MaxValue;
        MaxX = MaxY = MaxZ = double.MinValue;
        for (int i = 0; i < vertices.Length; i += 3)
        {
            MinX = Math.Min(MinX, vertices[i]);
            MinY = Math.Min(MinY, vertices[i + 1]);
            MinZ = Math.Min(MinZ, vertices[i + 2]);
            MaxX = Math.Max(MaxX, vertices[i]);
            MaxY = Math.Max(MaxY, vertices[i + 1]);
            MaxZ = Math.Max(MaxZ, vertices[i + 2]);
        }

        List<double> axes = [];
        for (int i = 0; i < edges.Length; i += 3)
        {
            double ex = edges[i];
            double ey = edges[i + 1];
            double ez = edges[i + 2];
            for (int axis = 0; axis < 3; axis++)
            {
                // edge × box axis
                (double ax, double ay, double az) = axis switch
                {
                    0 => (0.0, ez, -ey),
                    1 => (-ez, 0.0, ex),
                    _ => (ey, -ex, 0.0),
                };
                double length = Math.Sqrt((ax * ax) + (ay * ay) + (az * az));
                if (length < 1e-9)
                {
                    continue;
                }

                ax /= length;
                ay /= length;
                az /= length;
                (double lo, double hi) = Project(ax, ay, az);
                axes.AddRange([ax, ay, az, lo, hi]);
            }
        }

        _edgeAxes = [.. axes];
        NavGrowth sideways = NavGrowth.Sideways;
        _planeSidewaysRates = new double[planes.Length / 4];
        for (int i = 0; i < _planeSidewaysRates.Length; i++)
        {
            _planeSidewaysRates[i] = LowRate(planes[i * 4], planes[(i * 4) + 1], planes[(i * 4) + 2], sideways);
        }

        _edgeSidewaysRates = new double[_edgeAxes.Length / 5];
        for (int i = 0; i < _edgeSidewaysRates.Length; i++)
        {
            _edgeSidewaysRates[i] = LowRate(_edgeAxes[i * 5], _edgeAxes[(i * 5) + 1], _edgeAxes[(i * 5) + 2], sideways);
        }

        _zPlanes = [.. Enumerable.Range(0, planes.Length / 4).Where(k => planes[(k * 4) + 2] != 0)];
        _zEdges = [.. Enumerable.Range(0, _edgeAxes.Length / 5).Where(k => _edgeAxes[(k * 5) + 2] != 0)];
    }

    /// <summary>The brush's <c>CONTENTS_*</c> bits.</summary>
    public int Contents { get; }

    /// <summary>
    /// Whether every plane is axial, so the brush is its bounding box: then
    /// the box-axis test alone is exact, and the plane and edge axes (which
    /// are the same three axes again) are skipped. Most of a room's brushes
    /// are boxes, and this is most of the navigation's time saved.
    /// </summary>
    public bool IsAxial { get; }

    /// <summary>
    /// Whether the brush has a sloped face turned downward: a face whose
    /// normal points below the horizon and is not straight down.
    /// </summary>
    /// <remarks>
    /// Only such a brush can make a box's fit depend on its width and height
    /// together along a slant (a wider agent under a sloped ceiling has less
    /// head room), which a clearance staircase cannot state exactly. Every
    /// other brush grows narrower (or stays as wide) going up, so what it
    /// blocks is decided by the box's width alone once the box reaches up to
    /// its bottom; the <c>.nav3d</c> clearance records rely on that and store
    /// an overhanging brush's planes instead.
    /// </remarks>
    public bool IsOverhang { get; }

    /// <summary>How many bounding planes the brush has.</summary>
    public int PlaneCount => _planes.Length / 4;

    /// <summary>How many corners the brush has.</summary>
    public int VertexCount => _vertices.Length / 3;

    /// <summary>
    /// How many edge axes the brush has: one per edge and box axis that are
    /// not parallel, the edge crossed with the axis. With the face normals
    /// and the three box axes they are every axis that can separate a box
    /// from the brush.
    /// </summary>
    public int EdgeAxisCount => _edgeAxes.Length / 5;

    /// <summary>The bounding box's low x.</summary>
    public double MinX { get; }

    /// <summary>The bounding box's low y.</summary>
    public double MinY { get; }

    /// <summary>The bounding box's low z.</summary>
    public double MinZ { get; }

    /// <summary>The bounding box's high x.</summary>
    public double MaxX { get; }

    /// <summary>The bounding box's high y.</summary>
    public double MaxY { get; }

    /// <summary>The bounding box's high z.</summary>
    public double MaxZ { get; }

    /// <summary>One bounding plane: outward normal and distance, the inside being <c>n·x ≤ d</c>.</summary>
    /// <param name="index">The plane.</param>
    /// <returns>Its normal and distance.</returns>
    public (double X, double Y, double Z, double D) Plane(int index) =>
        (_planes[index * 4], _planes[(index * 4) + 1], _planes[(index * 4) + 2], _planes[(index * 4) + 3]);

    /// <summary>
    /// One edge axis (unit length) and the brush's extent along it. The two
    /// planes <c>a·x ≤ Hi</c> and <c>-a·x ≤ -Lo</c> support the brush; a
    /// swept-box clip that includes them is exact at the brush's slanted
    /// edges, as the compiler's bevel planes make it for world brushes.
    /// </summary>
    /// <param name="index">The axis.</param>
    /// <returns>Its direction and the brush's lowest and highest projection on it.</returns>
    public (double X, double Y, double Z, double Lo, double Hi) EdgeAxis(int index) =>
        (_edgeAxes[index * 5], _edgeAxes[(index * 5) + 1], _edgeAxes[(index * 5) + 2], _edgeAxes[(index * 5) + 3], _edgeAxes[(index * 5) + 4]);

    /// <summary>An axis-aligned box brush.</summary>
    /// <param name="mins">The low corner.</param>
    /// <param name="maxs">The high corner.</param>
    /// <param name="contents">Its contents.</param>
    /// <returns>The brush.</returns>
    /// <exception cref="ArgumentException">The box is empty.</exception>
    public static NavBrush Box(Vec3 mins, Vec3 maxs, int contents)
    {
        if (!(mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z))
        {
            throw new ArgumentException("a box brush has positive extent on every axis.", nameof(maxs));
        }

        double[] planes =
        [
            1, 0, 0, maxs.X, -1, 0, 0, -mins.X,
            0, 1, 0, maxs.Y, 0, -1, 0, -mins.Y,
            0, 0, 1, maxs.Z, 0, 0, -1, -mins.Z,
        ];
        double[] vertices = new double[24];
        for (int i = 0; i < 8; i++)
        {
            vertices[i * 3] = (i & 1) == 0 ? mins.X : maxs.X;
            vertices[(i * 3) + 1] = (i & 2) == 0 ? mins.Y : maxs.Y;
            vertices[(i * 3) + 2] = (i & 4) == 0 ? mins.Z : maxs.Z;
        }

        return new NavBrush(planes, vertices, [1, 0, 0, 0, 1, 0, 0, 0, 1], contents);
    }

    /// <summary>A brush bounded by planes, its corners and edges found by clipping each plane by the rest.</summary>
    /// <param name="planes">The outward normals (unit length) and distances.</param>
    /// <param name="contents">Its contents.</param>
    /// <returns>The brush, or null when the planes bound nothing with volume.</returns>
    public static NavBrush? FromPlanes(IReadOnlyList<(Vec3 Normal, float Dist)> planes, int contents)
    {
        ArgumentNullException.ThrowIfNull(planes);
        double[] p = new double[planes.Count * 4];
        for (int i = 0; i < planes.Count; i++)
        {
            p[i * 4] = planes[i].Normal.X;
            p[(i * 4) + 1] = planes[i].Normal.Y;
            p[(i * 4) + 2] = planes[i].Normal.Z;
            p[(i * 4) + 3] = planes[i].Dist;
        }

        return FromPlanes(p, contents);
    }

    /// <summary>A brush from planes stored as <c>float32</c> quadruples (normal x, y, z, distance), as the <c>.nav3d</c> file stores them.</summary>
    /// <param name="planes">Four floats per plane.</param>
    /// <param name="contents">Its contents.</param>
    /// <returns>The brush, or null when the planes bound nothing with volume.</returns>
    /// <remarks>
    /// The corners and edges are derived here, in double, by the same clipping
    /// the compile ran on the same float planes, so a reader that makes the
    /// brush from the file tests exactly the shape the compile tested.
    /// </remarks>
    public static NavBrush? FromPlaneFloats(ReadOnlySpan<float> planes, int contents)
    {
        if (planes.Length % 4 != 0)
        {
            throw new ArgumentException("planes come in fours: normal x, y, z and distance.", nameof(planes));
        }

        double[] p = new double[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            p[i] = planes[i];
        }

        return FromPlanes(p, contents);
    }

    /// <summary>Every brush of a BSP's brush lump, with its contents.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The brushes, in lump order; a brush whose planes bound no volume is left out.</returns>
    /// <remarks>
    /// Bevel planes are kept: they are supporting planes of the brush, so
    /// they change neither its shape nor the overlap test's answer, and a
    /// box sliding along a bevelled edge meets the bevel in the game too.
    /// </remarks>
    public static List<NavBrush> FromBsp(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<NavBrush> result = [];
        foreach ((_, NavBrush brush) in FromBspIndexed(bsp))
        {
            result.Add(brush);
        }

        return result;
    }

    /// <summary>Every brush of a BSP's brush lump with its index in the lump, so a caller can tell which model owns it.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The brushes with their lump index, in lump order; a brush whose planes bound no volume is left out.</returns>
    public static List<(int Index, NavBrush Brush)> FromBspIndexed(BspData bsp) => FromBspIndexed(bsp, 1);

    /// <summary>
    /// <see cref="FromBspIndexed(BspData)"/> with the brushes built on up to
    /// <paramref name="maxDegree"/> threads: each brush is built alone from
    /// its own planes, so the list is the same at any degree.
    /// </summary>
    /// <param name="bsp">The BSP.</param>
    /// <param name="maxDegree">How many brushes are built at once.</param>
    /// <returns>The brushes with their lump index, in lump order; a brush whose planes bound no volume is left out.</returns>
    public static List<(int Index, NavBrush Brush)> FromBspIndexed(BspData bsp, int maxDegree)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, 1);
        int count = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).Length;
        NavBrush?[] built = new NavBrush?[count];
        ExceptionDispatchInfo?[] failed = new ExceptionDispatchInfo?[count];
        const int Chunk = 256;
        System.Threading.Tasks.Parallel.For(0, (count + Chunk - 1) / Chunk, new ParallelOptions { MaxDegreeOfParallelism = maxDegree }, k =>
        {
            for (int b = k * Chunk; b < Math.Min(count, (k + 1) * Chunk); b++)
            {
                try
                {
                    built[b] = Build(bsp, b);
                }
                catch (Exception e)
                {
                    failed[b] = ExceptionDispatchInfo.Capture(e);
                    return;
                }
            }
        });

        // A brush that cannot be read throws as it did one brush at a
        // time: the first in lump order, as itself.
        List<(int, NavBrush)> result = new(count);
        for (int b = 0; b < count; b++)
        {
            failed[b]?.Throw();
            if (built[b] is { } brush)
            {
                result.Add((b, brush));
            }
        }

        return result;
    }

    /// <summary>One brush of a BSP's brush lump, from its sides' planes; null when they bound no volume.</summary>
    private static NavBrush? Build(BspData bsp, int b)
    {
        ReadOnlySpan<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        DBrush brush = brushes[b];
        double[] p = new double[brush.NumSides * 4];
        for (int s = 0; s < brush.NumSides; s++)
        {
            DPlane plane = planes[sides[brush.FirstSide + s].PlaneNum];
            p[s * 4] = plane.Normal.X;
            p[(s * 4) + 1] = plane.Normal.Y;
            p[(s * 4) + 2] = plane.Normal.Z;
            p[(s * 4) + 3] = plane.Dist;
        }

        return FromPlanes(p, brush.Contents);
    }

    /// <summary>Whether a box's interior overlaps the brush's, by more than <see cref="Epsilon"/>.</summary>
    /// <param name="box">The box.</param>
    /// <returns>True when no separating axis exists.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Overlaps(in NavBox box)
    {
        const double e = Epsilon;
        if (MaxX <= box.MinX + e || MinX >= box.MaxX - e
            || MaxY <= box.MinY + e || MinY >= box.MaxY - e
            || MaxZ <= box.MinZ + e || MinZ >= box.MaxZ - e)
        {
            return false;
        }

        if (IsAxial)
        {
            return true;
        }

        for (int i = 0; i < PlaneCount; i++)
        {
            if (FaceSeparates(i, box))
            {
                return false;
            }
        }

        double cx = (box.MinX + box.MaxX) * 0.5;
        double cy = (box.MinY + box.MaxY) * 0.5;
        double cz = (box.MinZ + box.MaxZ) * 0.5;
        double hx = (box.MaxX - box.MinX) * 0.5;
        double hy = (box.MaxY - box.MinY) * 0.5;
        double hz = (box.MaxZ - box.MinZ) * 0.5;
        for (int i = 0; i < _edgeAxes.Length; i += 5)
        {
            double ax = _edgeAxes[i];
            double ay = _edgeAxes[i + 1];
            double az = _edgeAxes[i + 2];
            double centre = (ax * cx) + (ay * cy) + (az * cz);
            double radius = (Math.Abs(ax) * hx) + (Math.Abs(ay) * hy) + (Math.Abs(az) * hz);
            if (_edgeAxes[i + 4] <= centre - radius + e || _edgeAxes[i + 3] >= centre + radius - e)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether one of the brush's planes has the whole box on its outside (touching allowed).</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="box">The box.</param>
    /// <returns>True when the plane separates the box from the brush.</returns>
    public bool FaceSeparates(int plane, in NavBox box)
    {
        double nx = _planes[plane * 4];
        double ny = _planes[(plane * 4) + 1];
        double nz = _planes[(plane * 4) + 2];
        double d = _planes[(plane * 4) + 3];

        // The box's least projection on the normal: its corner furthest
        // against the normal.
        double least = (nx >= 0 ? nx * box.MinX : nx * box.MaxX)
            + (ny >= 0 ? ny * box.MinY : ny * box.MaxY)
            + (nz >= 0 ? nz * box.MinZ : nz * box.MaxZ);
        return least >= d - Epsilon;
    }

    /// <summary>
    /// How far a box can grow before it overlaps the brush: the box's faces
    /// move out linearly with a parameter <c>t</c>, and the answer is the
    /// threshold <c>t*</c> such that the grown box overlaps the brush exactly
    /// when <c>t &gt; t*</c>.
    /// </summary>
    /// <param name="box">The box at <c>t = 0</c>.</param>
    /// <param name="growth">How fast each face moves out with <c>t</c>; every rate is 0 or more.</param>
    /// <param name="contactZ">
    /// The z of the unit direction, from the brush toward the box, of the
    /// separating axis that gives way at <c>t*</c>: the normal of the surface
    /// the growing box meets first. 1 for a flat top met from above.
    /// </param>
    /// <returns>
    /// <c>t*</c>; <see cref="double.PositiveInfinity"/> when the box never
    /// overlaps however far it grows, <see cref="double.NegativeInfinity"/>
    /// when it overlaps at every <c>t</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why one threshold is exact.</b> Along any axis the grown box's
    /// projection is an interval whose ends move linearly in <c>t</c>, out
    /// and never in, because every rate is non-negative. So each separating
    /// condition of the overlap test (<see cref="Overlaps"/>) reads
    /// <c>k·t ≤ q</c> with <c>k ≥ 0</c>: it holds for <c>t</c> up to some
    /// <c>q/k</c>, or for every <c>t</c>, or for none. The box is apart from
    /// the brush while any condition holds, so it overlaps exactly past the
    /// largest of those limits. The same axes as <see cref="Overlaps"/>, the
    /// same <see cref="Epsilon"/>: the threshold agrees with testing the
    /// grown box, up to the last bit of double rounding.
    /// </para>
    /// <para>
    /// The navigation uses it three ways: how far a box may widen before a
    /// brush blocks it (the clearance records' widths), how far a voxel's
    /// floor face may sink before it meets the surface under it (a leaf's
    /// floor height, and from <paramref name="contactZ"/> whether that
    /// surface is walkable), and, in the reader, the head room and width a
    /// voxel has beneath an overhanging brush.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double GrowthThreshold(in NavBox box, in NavGrowth growth, out double contactZ) =>
        growth == NavGrowth.Sideways ? SidewaysThreshold(box, out contactZ) : AnyGrowthThreshold(box, growth, out contactZ);

    /// <summary><see cref="GrowthThreshold"/> for any growth, working out each axis's rates as it goes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal double AnyGrowthThreshold(in NavBox box, in NavGrowth growth, out double contactZ)
    {
        double threshold = double.NegativeInfinity;
        contactZ = 0;

        // The box axes, against the bounding box.
        if (Axis(1, 0, 0, MinX, MaxX, box, growth, twoSided: true, ref threshold, ref contactZ)
            || Axis(0, 1, 0, MinY, MaxY, box, growth, twoSided: true, ref threshold, ref contactZ)
            || Axis(0, 0, 1, MinZ, MaxZ, box, growth, twoSided: true, ref threshold, ref contactZ))
        {
            contactZ = 0;
            return double.PositiveInfinity;
        }

        if (IsAxial)
        {
            return threshold;
        }

        for (int i = 0; i < _planes.Length; i += 4)
        {
            if (Axis(_planes[i], _planes[i + 1], _planes[i + 2], double.NegativeInfinity, _planes[i + 3], box, growth,
                twoSided: false, ref threshold, ref contactZ))
            {
                contactZ = 0;
                return double.PositiveInfinity;
            }
        }

        for (int i = 0; i < _edgeAxes.Length; i += 5)
        {
            if (Axis(_edgeAxes[i], _edgeAxes[i + 1], _edgeAxes[i + 2], _edgeAxes[i + 3], _edgeAxes[i + 4], box, growth,
                twoSided: true, ref threshold, ref contactZ))
            {
                contactZ = 0;
                return double.PositiveInfinity;
            }
        }

        return threshold;
    }

    /// <summary>
    /// <see cref="GrowthThreshold"/> for <see cref="NavGrowth.Sideways"/>,
    /// the clearance builder's hot path: the same axes in the same order,
    /// with the same arithmetic, but each plane's and edge axis's rate taken
    /// from the brush instead of worked out per call. Sideways growth widens
    /// a box at the same rate on its low and high sides, so one rate serves
    /// both of an edge axis's conditions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private double SidewaysThreshold(in NavBox box, out double contactZ)
    {
        NavGrowth growth = NavGrowth.Sideways;
        double threshold = double.NegativeInfinity;
        contactZ = 0;
        if (Axis(1, 0, 0, MinX, MaxX, box, growth, twoSided: true, ref threshold, ref contactZ)
            || Axis(0, 1, 0, MinY, MaxY, box, growth, twoSided: true, ref threshold, ref contactZ)
            || Axis(0, 0, 1, MinZ, MaxZ, box, growth, twoSided: true, ref threshold, ref contactZ))
        {
            contactZ = 0;
            return double.PositiveInfinity;
        }

        if (IsAxial)
        {
            return threshold;
        }

        double[] planes = _planes;
        double[] planeRates = _planeSidewaysRates;
        for (int i = 0, k = 0; i < planes.Length; i += 4, k++)
        {
            double ax = planes[i];
            double ay = planes[i + 1];
            double az = planes[i + 2];
            double m0 = (ax >= 0 ? ax * box.MinX : ax * box.MaxX)
                + (ay >= 0 ? ay * box.MinY : ay * box.MaxY)
                + (az >= 0 ? az * box.MinZ : az * box.MaxZ);
            double q = m0 + Epsilon - planes[i + 3];
            double mu = planeRates[k];
            if (mu > 0)
            {
                double limit = q / mu;
                if (limit > threshold)
                {
                    threshold = limit;
                    contactZ = az;
                }
            }
            else if (q >= 0)
            {
                contactZ = 0;
                return double.PositiveInfinity;
            }
        }

        double[] axes = _edgeAxes;
        double[] edgeRates = _edgeSidewaysRates;
        for (int i = 0, k = 0; i < axes.Length; i += 5, k++)
        {
            double ax = axes[i];
            double ay = axes[i + 1];
            double az = axes[i + 2];
            double rate = edgeRates[k];
            double m0 = (ax >= 0 ? ax * box.MinX : ax * box.MaxX)
                + (ay >= 0 ? ay * box.MinY : ay * box.MaxY)
                + (az >= 0 ? az * box.MinZ : az * box.MaxZ);
            double q = m0 + Epsilon - axes[i + 4];
            if (rate > 0)
            {
                double limit = q / rate;
                if (limit > threshold)
                {
                    threshold = limit;
                    contactZ = az;
                }
            }
            else if (q >= 0)
            {
                contactZ = 0;
                return double.PositiveInfinity;
            }

            double x0 = (ax >= 0 ? ax * box.MaxX : ax * box.MinX)
                + (ay >= 0 ? ay * box.MaxY : ay * box.MinY)
                + (az >= 0 ? az * box.MaxZ : az * box.MinZ);
            double q2 = axes[i + 3] + Epsilon - x0;
            if (rate > 0)
            {
                double limit = q2 / rate;
                if (limit > threshold)
                {
                    threshold = limit;
                    contactZ = -az;
                }
            }
            else if (q2 >= 0)
            {
                contactZ = 0;
                return double.PositiveInfinity;
            }
        }

        return threshold;
    }

    /// <summary>How many numbers <see cref="PrepareSidewaysColumn"/> writes for this brush.</summary>
    public int SidewaysColumnLength => 1 + _zPlanes.Length + (2 * _zEdges.Length);

    /// <summary>
    /// Works out, for a column of boxes that share one footprint, the part of
    /// the sideways growth test (<see cref="GrowthThreshold"/> with
    /// <see cref="NavGrowth.Sideways"/>) that does not depend on the boxes'
    /// heights, so <see cref="SidewaysThreshold(ReadOnlySpan{double}, double, double)"/>
    /// finishes it for each box with the z terms alone.
    /// </summary>
    /// <param name="minX">The footprint's low x.</param>
    /// <param name="minY">The footprint's low y.</param>
    /// <param name="maxX">The footprint's high x.</param>
    /// <param name="maxY">The footprint's high y.</param>
    /// <param name="prepared">Where the numbers go: <see cref="SidewaysColumnLength"/> of them.</param>
    /// <remarks>
    /// <para>
    /// The test projects the box on each axis as a sum of three terms, x
    /// part plus y part, then plus z part, in that order. The first sum is
    /// the same for every box of the column, so it is kept here and only the
    /// z part is added per box: the same operations on the same values, so
    /// the same bits.
    /// </para>
    /// <para>
    /// An axis with no z part (the box's x and y axes, the vertical faces,
    /// the edge axes crossed with the z axis) gives the same limit for every
    /// box of the column: its z term is a zero, which can change only the
    /// sign of a zero sum, and the epsilon added next takes that away. Those
    /// limits are folded here into the first number, the largest of them, or
    /// plus infinity when one of them separates the column from the brush
    /// for every width. The threshold is the largest limit over all axes, or
    /// plus infinity when any axis separates, so splitting the axes into
    /// these two groups changes nothing.
    /// </para>
    /// </remarks>
    public void PrepareSidewaysColumn(double minX, double minY, double maxX, double maxY, Span<double> prepared)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prepared.Length, SidewaysColumnLength, nameof(prepared));
        NavBox box = new(minX, minY, 0, maxX, maxY, 0);
        NavGrowth growth = NavGrowth.Sideways;
        double threshold = double.NegativeInfinity;
        double contactZ = 0;
        prepared[0] = double.PositiveInfinity;
        if (Axis(1, 0, 0, MinX, MaxX, box, growth, twoSided: true, ref threshold, ref contactZ)
            || Axis(0, 1, 0, MinY, MaxY, box, growth, twoSided: true, ref threshold, ref contactZ))
        {
            return;
        }

        if (!IsAxial)
        {
            double[] planes = _planes;
            for (int i = 0, k = 0; i < planes.Length; i += 4, k++)
            {
                if (planes[i + 2] == 0
                    && Axis(planes[i], planes[i + 1], planes[i + 2], double.NegativeInfinity, planes[i + 3], box, growth,
                        twoSided: false, ref threshold, ref contactZ))
                {
                    return;
                }
            }

            double[] axes = _edgeAxes;
            for (int i = 0; i < axes.Length; i += 5)
            {
                if (axes[i + 2] == 0
                    && Axis(axes[i], axes[i + 1], axes[i + 2], axes[i + 3], axes[i + 4], box, growth, twoSided: true, ref threshold, ref contactZ))
                {
                    return;
                }
            }

            int at = 1;
            foreach (int k in _zPlanes)
            {
                double ax = planes[k * 4];
                double ay = planes[(k * 4) + 1];
                prepared[at++] = (ax >= 0 ? ax * minX : ax * maxX) + (ay >= 0 ? ay * minY : ay * maxY);
            }

            foreach (int k in _zEdges)
            {
                double ax = axes[k * 5];
                double ay = axes[(k * 5) + 1];
                prepared[at++] = (ax >= 0 ? ax * minX : ax * maxX) + (ay >= 0 ? ay * minY : ay * maxY);
                prepared[at++] = (ax >= 0 ? ax * maxX : ax * minX) + (ay >= 0 ? ay * maxY : ay * minY);
            }
        }

        prepared[0] = threshold;
    }

    /// <summary>
    /// <see cref="GrowthThreshold"/> for <see cref="NavGrowth.Sideways"/> of a
    /// box in a column <see cref="PrepareSidewaysColumn"/> prepared: the same
    /// threshold, bit for bit, from the box's heights alone.
    /// </summary>
    /// <param name="prepared">The column's numbers.</param>
    /// <param name="minZ">The box's low z.</param>
    /// <param name="maxZ">The box's high z.</param>
    /// <returns>The threshold, as <see cref="GrowthThreshold"/> answers it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public double SidewaysThreshold(ReadOnlySpan<double> prepared, double minZ, double maxZ)
    {
        double threshold = prepared[0];
        if (double.IsPositiveInfinity(threshold))
        {
            return threshold;
        }

        // The box's z axis grows at no rate, so it separates outright or
        // never. Its projections are the box's heights plus a zero x and y
        // part, which can change only the sign of a zero; the comparisons
        // with zero below give the same answers either way.
        if (minZ + Epsilon - MaxZ >= 0 || MinZ + Epsilon - maxZ >= 0)
        {
            return double.PositiveInfinity;
        }

        if (IsAxial)
        {
            return threshold;
        }

        double[] planes = _planes;
        double[] planeRates = _planeSidewaysRates;
        int at = 1;
        foreach (int k in _zPlanes)
        {
            double az = planes[(k * 4) + 2];
            double m0 = prepared[at++] + (az >= 0 ? az * minZ : az * maxZ);
            double q = m0 + Epsilon - planes[(k * 4) + 3];
            double mu = planeRates[k];
            if (mu > 0)
            {
                double limit = q / mu;
                if (limit > threshold)
                {
                    threshold = limit;
                }
            }
            else if (q >= 0)
            {
                return double.PositiveInfinity;
            }
        }

        double[] axes = _edgeAxes;
        double[] edgeRates = _edgeSidewaysRates;
        foreach (int k in _zEdges)
        {
            double az = axes[(k * 5) + 2];
            double rate = edgeRates[k];
            double m0 = prepared[at++] + (az >= 0 ? az * minZ : az * maxZ);
            double q = m0 + Epsilon - axes[(k * 5) + 4];
            double x0 = prepared[at++] + (az >= 0 ? az * maxZ : az * minZ);
            double q2 = axes[(k * 5) + 3] + Epsilon - x0;
            if (rate > 0)
            {
                double limit = q / rate;
                if (limit > threshold)
                {
                    threshold = limit;
                }

                limit = q2 / rate;
                if (limit > threshold)
                {
                    threshold = limit;
                }
            }
            else if (q >= 0 || q2 >= 0)
            {
                return double.PositiveInfinity;
            }
        }

        return threshold;
    }

    /// <summary>The brush turned a quarter turn about +z, some number of times, about a centre line.</summary>
    /// <param name="quarterTurns">Counter-clockwise quarter turns, 0 to 3.</param>
    /// <param name="size">The cell's edge: the turn maps the cell <c>[0, size]²</c> onto itself.</param>
    /// <returns>The turned brush: its planes permuted and negated exactly, its corners and edges likewise.</returns>
    public NavBrush Turned(int quarterTurns, double size) => Turned(quarterTurns, size, size);

    /// <summary>
    /// The brush turned a quarter turn about +z, some number of times, with
    /// a rectangle <c>[0, sizeX] × [0, sizeY]</c> mapped onto its turned self:
    /// each turn takes <c>(x, y)</c> to <c>(Y − y, x)</c>, <c>Y</c> the
    /// rectangle's side along y at that turn, so the sides swap as it turns.
    /// </summary>
    /// <param name="quarterTurns">Counter-clockwise quarter turns, 0 to 3.</param>
    /// <param name="sizeX">The rectangle's side along x before the turns.</param>
    /// <param name="sizeY">Its side along y.</param>
    /// <returns>The turned brush, exactly: planes permuted and negated, corners and edges likewise.</returns>
    public NavBrush Turned(int quarterTurns, double sizeX, double sizeY)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        double[] planes = (double[])_planes.Clone();
        double[] vertices = (double[])_vertices.Clone();
        double[] edges = (double[])_edges.Clone();
        for (int t = 0; t < r; t++)
        {
            double size = (t & 1) == 0 ? sizeY : sizeX;
            for (int i = 0; i < planes.Length; i += 4)
            {
                // n·x ≤ d with x' = (size - y, x): n' = (-ny, nx), d' = d - ny·size
                double nx = planes[i];
                double ny = planes[i + 1];
                planes[i] = -ny;
                planes[i + 1] = nx;
                planes[i + 3] -= ny * size;
            }

            for (int i = 0; i < vertices.Length; i += 3)
            {
                double x = vertices[i];
                vertices[i] = size - vertices[i + 1];
                vertices[i + 1] = x;
            }

            for (int i = 0; i < edges.Length; i += 3)
            {
                double x = edges[i];
                edges[i] = -edges[i + 1];
                edges[i + 1] = x;
            }
        }

        return new NavBrush(planes, vertices, edges, Contents);
    }

    /// <summary>The brush moved by an offset.</summary>
    /// <param name="offset">How far to move it.</param>
    /// <returns>The moved brush: each plane's distance grows by its normal dotted with the offset.</returns>
    public NavBrush Translated(Vec3 offset)
    {
        double[] planes = (double[])_planes.Clone();
        double[] vertices = (double[])_vertices.Clone();
        for (int i = 0; i < planes.Length; i += 4)
        {
            planes[i + 3] += (planes[i] * offset.X) + (planes[i + 1] * offset.Y) + (planes[i + 2] * offset.Z);
        }

        for (int i = 0; i < vertices.Length; i += 3)
        {
            vertices[i] += offset.X;
            vertices[i + 1] += offset.Y;
            vertices[i + 2] += offset.Z;
        }

        return new NavBrush(planes, vertices, (double[])_edges.Clone(), Contents);
    }

    /// <summary>The planes as <c>float32</c> quadruples, the form the <c>.nav3d</c> file stores.</summary>
    /// <returns>Four floats per plane: normal x, y, z and distance.</returns>
    public float[] PlaneFloats()
    {
        float[] floats = new float[_planes.Length];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = (float)_planes[i];
        }

        return floats;
    }

    /// <summary>One candidate axis's separating conditions, folded into the threshold; true when the axis separates at every <c>t</c>.</summary>
    /// <remarks>
    /// Inlined: the growth test calls it two dozen times for a typical
    /// slanted brush, and the call (ten arguments, two by reference) cost
    /// more than the arithmetic. Inlining changes no rounding: the runtime
    /// neither fuses nor reorders the floating-point operations.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Axis(
        double ax, double ay, double az, double lo, double hi, in NavBox box, in NavGrowth g, bool twoSided,
        ref double threshold, ref double contactZ)
    {
        // The grown box's least projection is m0 - mu t, its greatest x0 + nu t.
        double m0 = (ax >= 0 ? ax * box.MinX : ax * box.MaxX)
            + (ay >= 0 ? ay * box.MinY : ay * box.MaxY)
            + (az >= 0 ? az * box.MinZ : az * box.MaxZ);
        double mu = (ax >= 0 ? ax * g.MinX : -ax * g.MaxX)
            + (ay >= 0 ? ay * g.MinY : -ay * g.MaxY)
            + (az >= 0 ? az * g.MinZ : -az * g.MaxZ);

        // The brush below along the axis: hi ≤ m0 - mu t + e.
        double q = m0 + Epsilon - hi;
        if (mu > 0)
        {
            double limit = q / mu;
            if (limit > threshold)
            {
                threshold = limit;
                contactZ = az;
            }
        }
        else if (q >= 0)
        {
            return true;
        }

        if (!twoSided)
        {
            return false;
        }

        double x0 = (ax >= 0 ? ax * box.MaxX : ax * box.MinX)
            + (ay >= 0 ? ay * box.MaxY : ay * box.MinY)
            + (az >= 0 ? az * box.MaxZ : az * box.MinZ);
        double nu = (ax >= 0 ? ax * g.MaxX : -ax * g.MinX)
            + (ay >= 0 ? ay * g.MaxY : -ay * g.MinY)
            + (az >= 0 ? az * g.MaxZ : -az * g.MinZ);

        // The brush above along the axis: lo ≥ x0 + nu t - e.
        double q2 = lo + Epsilon - x0;
        if (nu > 0)
        {
            double limit = q2 / nu;
            if (limit > threshold)
            {
                threshold = limit;
                contactZ = -az;
            }
        }
        else if (q2 >= 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// How fast a growing box's least projection on an axis falls with
    /// <c>t</c>, exactly as <see cref="Axis"/> works it out; for a growth
    /// whose low and high rates match on each axis (sideways), also how fast
    /// its greatest projection rises.
    /// </summary>
    private static double LowRate(double ax, double ay, double az, in NavGrowth g) =>
        (ax >= 0 ? ax * g.MinX : -ax * g.MaxX)
        + (ay >= 0 ? ay * g.MinY : -ay * g.MaxY)
        + (az >= 0 ? az * g.MinZ : -az * g.MaxZ);

    private (double Lo, double Hi) Project(double ax, double ay, double az)
    {
        double lo = double.MaxValue;
        double hi = double.MinValue;
        for (int v = 0; v < _vertices.Length; v += 3)
        {
            double d = (ax * _vertices[v]) + (ay * _vertices[v + 1]) + (az * _vertices[v + 2]);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }

        return (lo, hi);
    }

    private static NavBrush? FromPlanes(double[] planes, int contents)
    {
        int count = planes.Length / 4;
        List<double> vertices = [];
        List<double> edges = [];
        List<double> polygon = [];
        List<double> clipped = [];
        for (int i = 0; i < count; i++)
        {
            BaseWinding(planes, i, polygon);
            for (int j = 0; j < count && polygon.Count >= 9; j++)
            {
                if (j != i)
                {
                    Clip(polygon, planes, j, clipped);
                    (polygon, clipped) = (clipped, polygon);
                }
            }

            if (polygon.Count < 9)
            {
                continue;
            }

            int points = polygon.Count / 3;
            for (int k = 0; k < points; k++)
            {
                AddVertex(vertices, polygon[k * 3], polygon[(k * 3) + 1], polygon[(k * 3) + 2]);
                int next = (k + 1) % points;
                AddEdge(
                    edges,
                    polygon[next * 3] - polygon[k * 3],
                    polygon[(next * 3) + 1] - polygon[(k * 3) + 1],
                    polygon[(next * 3) + 2] - polygon[(k * 3) + 2]);
            }
        }

        // Fewer than four corners bound no volume; a corner as far out as the
        // starting square means the planes leave the brush open on a side.
        if (vertices.Count < 12)
        {
            return null;
        }

        foreach (double v in vertices)
        {
            if (Math.Abs(v) >= Huge / 2)
            {
                return null;
            }
        }

        return new NavBrush(planes, [.. vertices], [.. edges], contents);
    }

    /// <summary>A square far larger than any map, on one plane, wound so its normal is the plane's.</summary>
    private static void BaseWinding(double[] planes, int index, List<double> polygon)
    {
        double nx = planes[index * 4];
        double ny = planes[(index * 4) + 1];
        double nz = planes[(index * 4) + 2];
        double d = planes[(index * 4) + 3];

        // A direction in the plane: the normal crossed with whichever of up
        // or +x it is least parallel to.
        (double ux, double uy, double uz) = Math.Abs(nz) < 0.9 ? (-ny, nx, 0.0) : (0.0, nz, -ny);
        double ul = Math.Sqrt((ux * ux) + (uy * uy) + (uz * uz));
        ux /= ul;
        uy /= ul;
        uz /= ul;

        // v = n × u
        double vx = (ny * uz) - (nz * uy);
        double vy = (nz * ux) - (nx * uz);
        double vz = (nx * uy) - (ny * ux);
        double ox = nx * d;
        double oy = ny * d;
        double oz = nz * d;

        polygon.Clear();
        ReadOnlySpan<(double U, double V)> corners = [(-1, -1), (1, -1), (1, 1), (-1, 1)];
        foreach ((double cu, double cv) in corners)
        {
            polygon.Add(ox + (Huge * ((cu * ux) + (cv * vx))));
            polygon.Add(oy + (Huge * ((cu * uy) + (cv * vy))));
            polygon.Add(oz + (Huge * ((cu * uz) + (cv * vz))));
        }
    }

    /// <summary>Keeps the part of a convex polygon inside one plane (<c>n·x ≤ d</c>, with a sliver of slack).</summary>
    private static void Clip(List<double> polygon, double[] planes, int index, List<double> output)
    {
        double nx = planes[index * 4];
        double ny = planes[(index * 4) + 1];
        double nz = planes[(index * 4) + 2];
        double d = planes[(index * 4) + 3];
        output.Clear();
        int points = polygon.Count / 3;
        for (int k = 0; k < points; k++)
        {
            int next = (k + 1) % points;
            double ax = polygon[k * 3];
            double ay = polygon[(k * 3) + 1];
            double az = polygon[(k * 3) + 2];
            double bx = polygon[next * 3];
            double by = polygon[(next * 3) + 1];
            double bz = polygon[(next * 3) + 2];
            double da = (nx * ax) + (ny * ay) + (nz * az) - d;
            double db = (nx * bx) + (ny * by) + (nz * bz) - d;
            if (da <= VertexWeld)
            {
                output.Add(ax);
                output.Add(ay);
                output.Add(az);
            }

            if ((da < -VertexWeld && db > VertexWeld) || (da > VertexWeld && db < -VertexWeld))
            {
                double t = da / (da - db);
                output.Add(ax + ((bx - ax) * t));
                output.Add(ay + ((by - ay) * t));
                output.Add(az + ((bz - az) * t));
            }
        }
    }

    private static void AddVertex(List<double> vertices, double x, double y, double z)
    {
        for (int i = 0; i < vertices.Count; i += 3)
        {
            if (Math.Abs(vertices[i] - x) <= VertexWeld && Math.Abs(vertices[i + 1] - y) <= VertexWeld
                && Math.Abs(vertices[i + 2] - z) <= VertexWeld)
            {
                return;
            }
        }

        vertices.Add(x);
        vertices.Add(y);
        vertices.Add(z);
    }

    private static void AddEdge(List<double> edges, double x, double y, double z)
    {
        double length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (length < VertexWeld)
        {
            return;
        }

        x /= length;
        y /= length;
        z /= length;
        for (int i = 0; i < edges.Count; i += 3)
        {
            double dot = (edges[i] * x) + (edges[i + 1] * y) + (edges[i + 2] * z);
            if (Math.Abs(dot) >= 1 - 1e-9)
            {
                return;
            }
        }

        edges.Add(x);
        edges.Add(y);
        edges.Add(z);
    }
}

/// <summary>An axis-aligned box in double precision: an agent's box swept over a voxel.</summary>
/// <param name="MinX">Low x.</param>
/// <param name="MinY">Low y.</param>
/// <param name="MinZ">Low z.</param>
/// <param name="MaxX">High x.</param>
/// <param name="MaxY">High y.</param>
/// <param name="MaxZ">High z.</param>
public readonly record struct NavBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

/// <summary>How fast each face of a box moves out as it grows (<see cref="NavBrush.GrowthThreshold"/>); every rate is 0 or more.</summary>
/// <param name="MinX">The low x face moves to <c>MinX - t·rate</c>.</param>
/// <param name="MinY">The low y face.</param>
/// <param name="MinZ">The low z face.</param>
/// <param name="MaxX">The high x face moves to <c>MaxX + t·rate</c>.</param>
/// <param name="MaxY">The high y face.</param>
/// <param name="MaxZ">The high z face.</param>
public readonly record struct NavGrowth(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    /// <summary>Widening sideways by <c>t</c> on each of the four sides: an agent's half-width.</summary>
    public static NavGrowth Sideways => new(1, 1, 0, 1, 1, 0);

    /// <summary>Rising by <c>t</c> at the top: an agent's height.</summary>
    public static NavGrowth Upward => new(0, 0, 0, 0, 0, 1);

    /// <summary>Sinking by <c>t</c> at the bottom: how far a floor face may drop.</summary>
    public static NavGrowth Downward => new(0, 0, 1, 0, 0, 0);
}
