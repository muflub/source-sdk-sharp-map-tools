//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// Builds the clearance grid of a box of voxels from its geometry: for every
/// voxel and each clip class, the staircase of obstacles that decides every
/// agent size (<see cref="Nav3dClearance"/>), with the voxel's water and
/// ladder flags, its cost, and the floor under it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One grid for every agent.</b> Version 1 voxelised the space once per
/// agent. Here each voxel keeps, per clip class, the corners of the brushes
/// that could block some agent in it, pruned to those no other corner
/// dominates; any agent's fit is then a few comparisons, exact for every
/// width and height (the exactness argument is on <see cref="Nav3dClearance"/>
/// and in <c>docs/nav3d-format.md</c>).
/// </para>
/// <para>
/// <b>How a corner is found.</b> For an axis-aligned brush, the width
/// threshold is the Chebyshev gap between the voxel's footprint and the
/// brush's (the largest of the four side gaps) and the top threshold is the
/// brush's bottom; both are differences of the grid's and the brush's own
/// coordinates, so they are exact. For a brush that is not axis-aligned and
/// has no sloped underside, the width threshold is where the separating-axis
/// test first fails as the box widens (<see cref="NavBrush.GrowthThreshold"/>),
/// the box reaching past the brush's top. A brush with a sloped underside is
/// listed by index and tested directly. The stored floats are rounded
/// toward minus infinity, so a rounded threshold can only block more, never
/// less: an agent the file says fits, fits.
/// </para>
/// <para>
/// <b>Floors.</b> A voxel that is free for a class while the voxel under it
/// is not stands on a floor for that class. The floor's height is the top of
/// the solid in the voxel under it (how far the voxel's floor face could
/// sink before it met solid), and it is walkable when the surface met first
/// faces up at least as steeply as the level's floor threshold.
/// </para>
/// <para>
/// <b>Deterministic.</b> Brushes are visited in the order given and voxels
/// column by column, low to high, on one thread; records are numbered in
/// the order first met. The answer depends only on the geometry, so it is
/// the same at any thread count of the compile around it.
/// </para>
/// </remarks>
public static class NavClearanceBuilder
{
    private const double E = NavBrush.Epsilon;

    /// <summary>Builds the grid of a box of voxels.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="region">The voxels.</param>
    /// <param name="settings">The floor threshold and cost weights.</param>
    /// <param name="cancellationToken">Cancels the build between columns.</param>
    /// <returns>The grid.</returns>
    public static NavGrid Build(NavGeometry geometry, NavRegion region, NavSettings settings, CancellationToken cancellationToken = default) =>
        Build(geometry, region, settings, new NavRecordTable(), cancellationToken);

    /// <summary>Builds the grid of a box of voxels, adding its records to a table another grid may share.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="region">The voxels.</param>
    /// <param name="settings">The floor threshold and cost weights.</param>
    /// <param name="records">The record table to number records in.</param>
    /// <param name="cancellationToken">Cancels the build between columns.</param>
    /// <returns>The grid.</returns>
    public static NavGrid Build(
        NavGeometry geometry, NavRegion region, NavSettings settings, NavRecordTable records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(region.SizeX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(region.SizeY, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(region.SizeZ, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(region.SizeZ, Nav3dFormat.MaxColumnVoxels);
        if (!(region.VoxelSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(region), "the voxel has a positive edge.");
        }

        Prepared prepared = new(geometry);
        Scratch scratch = new();
        NavVoxelKey[] keys = new NavVoxelKey[region.SizeX * region.SizeY * region.SizeZ];
        for (int y = 0; y < region.SizeY; y++)
        {
            for (int x = 0; x < region.SizeX; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Column(prepared, region, settings, records, scratch, x, y, keys);
            }
        }

        return new NavGrid(region, records, keys);
    }

    /// <summary>
    /// One voxel column: each voxel's records, flags, cost and floor, low to
    /// high, so a voxel's floor can read the records of the voxel under it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Column(
        Prepared p, NavRegion region, NavSettings settings, NavRecordTable records, Scratch scratch, int x, int y, NavVoxelKey[] keys)
    {
        double s = region.VoxelSize;
        double x0 = region.OriginX + (x * s);
        double x1 = region.OriginX + ((x + 1) * s);
        double y0 = region.OriginY + (y * s);
        double y1 = region.OriginY + ((y + 1) * s);

        // The axis-aligned solids' corners depend on the column alone: their
        // width threshold is a horizontal gap and their top a fixed height.
        // Sorted once by width (then top), every voxel of the column builds
        // its staircase in one pass.
        scratch.Axial.Clear();
        foreach (int b in p.AxialSolid)
        {
            NavBrush brush = p.Brushes[b];
            double r = Math.Max(Math.Max(x0 - brush.MaxX, brush.MinX - x1), Math.Max(y0 - brush.MaxY, brush.MinY - y1));
            scratch.Axial.Add(new Candidate(r, brush.MinZ, brush.MaxZ, p.SolidMask[b]));
        }

        CollectionsMarshal.AsSpan(scratch.Axial).Sort(default(CandidateOrder));

        // The sloped solids, each with a floor under its width threshold
        // that depends on the column alone, in the order of those floors: a
        // voxel computes a sloped corner only once the staircase has reached
        // that floor, and only if the corner could still lower it.
        scratch.Sloped.Clear();
        foreach (int b in p.SlopedSolid)
        {
            scratch.Sloped.Add(new SlopedFloor(SlopedWidthFloor(p.Brushes[b], x0, y0, x1, y1), b));
        }

        CollectionsMarshal.AsSpan(scratch.Sloped).Sort(default(SlopedFloorOrder));
        int below0 = -1;
        int below1 = -1;
        for (int z = 0; z < region.SizeZ; z++)
        {
            NavBox voxel = region.Voxel(x, y, z);
            int record0 = Record(p, region, records, scratch, voxel, 0);
            int record1 = Record(p, region, records, scratch, voxel, 1);
            Nav3dLeafFlags flags = Nav3dLeafFlags.None;
            bool water = false;
            bool ladder = false;
            if (record0 != NavRecordTable.BlockedIndex || record1 != NavRecordTable.BlockedIndex)
            {
                water = p.Water.Any(b => p.Brushes[b].Overlaps(voxel));
                ladder = p.Ladder.Any(b => p.Brushes[b].Overlaps(voxel))
                    || p.LadderVolumes.Any(v => v.Maxs.X > voxel.MinX + E && v.Mins.X < voxel.MaxX - E
                        && v.Maxs.Y > voxel.MinY + E && v.Mins.Y < voxel.MaxY - E
                        && v.Maxs.Z > voxel.MinZ + E && v.Mins.Z < voxel.MaxZ - E);
            }

            flags |= water ? Nav3dLeafFlags.Water : 0;
            flags |= ladder ? Nav3dLeafFlags.Ladder : 0;
            float floor0 = 0;
            float floor1 = 0;
            if (record0 != NavRecordTable.BlockedIndex && (z == 0 ? BelowBlocked(p, voxel, s, 0) : below0 == NavRecordTable.BlockedIndex))
            {
                (floor0, bool walkable) = Floor(p, voxel, s, 0, settings.FloorNormalZ);
                flags |= Nav3dLeafFlags.GroundedPlayer | (walkable ? Nav3dLeafFlags.WalkablePlayer : 0);
            }

            if (record1 != NavRecordTable.BlockedIndex && (z == 0 ? BelowBlocked(p, voxel, s, 1) : below1 == NavRecordTable.BlockedIndex))
            {
                (floor1, bool walkable) = Floor(p, voxel, s, 1, settings.FloorNormalZ);
                flags |= Nav3dLeafFlags.GroundedNpc | (walkable ? Nav3dLeafFlags.WalkableNpc : 0);
            }

            keys[region.Index(x, y, z)] = record0 == NavRecordTable.BlockedIndex && record1 == NavRecordTable.BlockedIndex
                ? NavVoxelKey.Solid
                : new NavVoxelKey(record0, record1, flags, settings.Cost(water, ladder), floor0, floor1);
            below0 = record0;
            below1 = record1;
        }
    }

    /// <summary>One voxel's record for one clip class (0 player, 1 NPC), numbered in the table.</summary>
    private static int Record(Prepared p, NavRegion region, NavRecordTable records, Scratch scratch, in NavBox voxel, int clipClass)
    {
        int bit = 1 << clipClass;
        double z0 = voxel.MinZ;
        double z1 = voxel.MaxZ;
        List<Nav3dCorner> stair = scratch.Stair;
        stair.Clear();
        Staircase(p, scratch, voxel, bit, stair);
        if (IsBlocked(stair))
        {
            return NavRecordTable.BlockedIndex;
        }

        // Overhanging brushes: blocked outright when one overlaps the voxel
        // itself, listed when its bounding box's corner is not already
        // covered by the staircase (a brush inside its box blocks no more
        // than the box).
        List<int> brushes = scratch.Brushes;
        brushes.Clear();
        for (int i = 0; i < p.Overhang.Length; i++)
        {
            int b = p.Overhang[i];
            NavBrush brush = p.Brushes[b];
            if ((p.SolidMask[b] & bit) == 0 || brush.MaxZ <= z0 + E)
            {
                continue;
            }

            if (brush.Overlaps(voxel))
            {
                return NavRecordTable.BlockedIndex;
            }

            Nav3dCorner box = Normalise(BoxWidth(brush, voxel), brush.MinZ, z1);
            if (!Dominated(stair, box))
            {
                brushes.Add(i);
            }
        }

        // Dynamic obstacles: each one's own staircase, less what the static
        // staircase already covers.
        List<Nav3dDynamicCorner> dynamics = scratch.Dynamics;
        dynamics.Clear();
        List<Nav3dCorner> own = scratch.Own;
        for (int o = 0; o < p.Obstacles.Length; o++)
        {
            own.Clear();
            scratch.Merged.Clear();
            foreach ((NavBrush brush, byte mask) in p.Obstacles[o])
            {
                if ((mask & bit) == 0 || brush.MaxZ <= z0 + E)
                {
                    continue;
                }

                Candidate? candidate = brush.IsAxial || brush.IsOverhang
                    ? new Candidate(BoxWidth(brush, voxel), brush.MinZ, brush.MaxZ, mask)
                    : SlopedCorner(brush, voxel);
                if (candidate is { } c)
                {
                    scratch.Merged.Add(c);
                }
            }

            CollectionsMarshal.AsSpan(scratch.Merged).Sort(default(CandidateOrder));
            foreach (Candidate c in scratch.Merged)
            {
                Offer(own, c.Width, c.Bottom, z1);
            }

            foreach (Nav3dCorner corner in own)
            {
                if (!Dominated(stair, corner))
                {
                    dynamics.Add(new Nav3dDynamicCorner(o, corner));
                }
            }
        }

        return records.Add(Encode(scratch, stair, dynamics, brushes));
    }

    /// <summary>
    /// A voxel's static staircase for one class: the column's axial
    /// candidates and the voxel's sloped ones, offered in width order (then
    /// bottom), exactly as if every candidate were listed and sorted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The staircase only ever takes a corner lower than its last one, so a
    /// candidate whose top is not below the last top when its turn comes is
    /// dropped, and once the last top is minus infinity (a brush blocking
    /// every height) nothing after it counts. Candidates that share a width
    /// and a bottom give the same corner, so their order among themselves
    /// does not matter either.
    /// </para>
    /// <para>
    /// That lets a sloped brush's corner, the costly part (a growth test
    /// over all its planes and edges), be computed only when it can matter.
    /// Its width is at least its floor (<see cref="SlopedWidthFloor"/>), so
    /// it comes after every candidate narrower than the floor; it is
    /// brought in only once the next candidate to offer is no narrower than
    /// the floor, and skipped there when its top (which needs no growth
    /// test) is not below the staircase's last top, since that top can only
    /// fall further before the brush's turn. The walk ends at the first
    /// corner that blocks every height.
    /// </para>
    /// </remarks>
    private static void Staircase(Prepared p, Scratch scratch, in NavBox voxel, int bit, List<Nav3dCorner> stair)
    {
        double z0 = voxel.MinZ;
        double z1 = voxel.MaxZ;
        List<Candidate> axial = scratch.Axial;
        List<SlopedFloor> sloped = scratch.Sloped;
        List<Candidate> pending = scratch.Pending;
        pending.Clear();
        int head = 0;
        int a = 0;
        int s = 0;
        CandidateOrder order = default;
        while (true)
        {
            while (a < axial.Count && ((axial[a].Mask & bit) == 0 || !(axial[a].Top > z0 + E)))
            {
                a++;
            }

            // Every sloped brush that could come at or before the next
            // candidate joins the pending ones first.
            double next = a < axial.Count ? axial[a].Width : double.PositiveInfinity;
            if (head < pending.Count && pending[head].Width < next)
            {
                next = pending[head].Width;
            }

            while (s < sloped.Count && !(sloped[s].Floor > next))
            {
                int b = sloped[s++].Brush;
                NavBrush brush = p.Brushes[b];
                if ((p.SolidMask[b] & bit) == 0 || !(brush.MaxZ > z0 + E)
                    || (stair.Count > 0 && !(CornerTop(brush.MinZ, z1) < stair[^1].Top)))
                {
                    continue;
                }

                if (SlopedCorner(brush, voxel) is { } candidate)
                {
                    int at = pending.Count;
                    pending.Add(default);
                    Candidate c = candidate with { Mask = p.SolidMask[b] };
                    while (at > head && order.Compare(pending[at - 1], c) > 0)
                    {
                        pending[at] = pending[at - 1];
                        at--;
                    }

                    pending[at] = c;
                }
            }

            Candidate offer;
            if (head < pending.Count && (a >= axial.Count || order.Compare(pending[head], axial[a]) < 0))
            {
                offer = pending[head++];
            }
            else if (a < axial.Count)
            {
                offer = axial[a++];
            }
            else
            {
                return;
            }

            Offer(stair, offer.Width, offer.Bottom, z1);
            if (float.IsNegativeInfinity(stair[^1].Top))
            {
                return;
            }
        }
    }

    /// <summary>
    /// A floor under a sloped brush's width threshold at a column (<see cref="SlopedCorner"/>):
    /// the threshold its bounding box alone gives, less the same epsilon.
    /// </summary>
    /// <remarks>
    /// The growth threshold is the largest of the limits every separating
    /// axis sets, and the box's own four side axes are among them. Each of
    /// those limits is computed here with the same operations, on the same
    /// values, as <see cref="NavBrush.GrowthThreshold"/> computes it (a box
    /// axis's projection of the box is the box's coordinate itself, and its
    /// growth rate is 1), so the threshold is at least their largest, and
    /// subtracting the epsilon keeps the order: the corner's width is never
    /// below this floor.
    /// </remarks>
    /// <param name="brush">The sloped brush.</param>
    /// <param name="x0">The column's low x.</param>
    /// <param name="y0">The column's low y.</param>
    /// <param name="x1">The column's high x.</param>
    /// <param name="y1">The column's high y.</param>
    /// <returns>The floor.</returns>
    internal static double SlopedWidthFloor(NavBrush brush, double x0, double y0, double x1, double y1) =>
        Math.Max(Math.Max(x0 + E - brush.MaxX, brush.MinX + E - x1), Math.Max(y0 + E - brush.MaxY, brush.MinY + E - y1)) - E;

    /// <summary>Adds a corner to a staircase built in width order: kept only when lower than every corner before it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Offer(List<Nav3dCorner> stair, double width, double bottom, double voxelTop)
    {
        Nav3dCorner corner = Normalise(width, bottom, voxelTop);
        if (stair.Count > 0 && !(corner.Top < stair[^1].Top))
        {
            return;
        }

        if (stair.Count > 0 && stair[^1].Width == corner.Width)
        {
            stair[^1] = corner;
            return;
        }

        stair.Add(corner);
    }

    /// <summary>
    /// A corner in its canonical form: a width threshold every width passes
    /// is minus infinity, a top every agent in the voxel reaches is minus
    /// infinity, and anything else is rounded to the float at or below it.
    /// </summary>
    internal static Nav3dCorner Normalise(double width, double bottom, double voxelTop) => new(
        width + E < 0 ? float.NegativeInfinity : Down(width),
        CornerTop(bottom, voxelTop));

    /// <summary>A corner's top in its canonical form: minus infinity when every agent in the voxel reaches the bottom, else the bottom rounded down.</summary>
    private static float CornerTop(double bottom, double voxelTop) =>
        voxelTop > bottom + E ? float.NegativeInfinity : Down(bottom);

    /// <summary>The largest float at or below a double: a threshold rounded so it can only block more.</summary>
    internal static float Down(double value)
    {
        float f = (float)value;
        return f > value ? MathF.BitDecrement(f) : f;
    }

    /// <summary>Whether a staircase covers a corner: some corner of it is no wider and no higher.</summary>
    internal static bool Dominated(List<Nav3dCorner> stair, Nav3dCorner corner)
    {
        // Widths rise and tops fall, so the lowest top among the corners no
        // wider than this one is the last of them.
        for (int i = stair.Count - 1; i >= 0; i--)
        {
            if (stair[i].Width <= corner.Width)
            {
                return stair[i].Top <= corner.Top;
            }
        }

        return false;
    }

    private static bool IsBlocked(List<Nav3dCorner> stair) =>
        stair.Count > 0 && float.IsNegativeInfinity(stair[0].Width) && float.IsNegativeInfinity(stair[0].Top);

    /// <summary>The Chebyshev gap between a voxel's footprint and a brush's bounding box: a box brush's width threshold.</summary>
    private static double BoxWidth(NavBrush brush, in NavBox voxel) =>
        Math.Max(Math.Max(voxel.MinX - brush.MaxX, brush.MinX - voxel.MaxX), Math.Max(voxel.MinY - brush.MaxY, brush.MinY - voxel.MaxY));

    /// <summary>
    /// A sloped brush's corner at a voxel: the width at which a box reaching
    /// past the brush's top first overlaps it. Null when no width does.
    /// </summary>
    internal static Candidate? SlopedCorner(NavBrush brush, in NavBox voxel)
    {
        NavBox tall = voxel with { MaxZ = Math.Max(voxel.MaxZ, brush.MaxZ) + 1 };
        double threshold = brush.GrowthThreshold(tall, NavGrowth.Sideways, out _);
        return double.IsPositiveInfinity(threshold)
            ? null
            : new Candidate(double.IsNegativeInfinity(threshold) ? double.NegativeInfinity : threshold - E, brush.MinZ, brush.MaxZ, 0);
    }

    /// <summary>Whether the voxel under a region's bottom voxel is solid for a class, tested directly.</summary>
    private static bool BelowBlocked(Prepared p, in NavBox voxel, double s, int clipClass)
    {
        NavBox below = voxel with { MinZ = voxel.MinZ - s, MaxZ = voxel.MinZ };
        for (int b = 0; b < p.Brushes.Length; b++)
        {
            if ((p.SolidMask[b] & (1 << clipClass)) != 0 && p.Brushes[b].Overlaps(below))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The floor under a voxel for a class: the highest top of the solids in
    /// the voxel under it, and whether the surface there is walkable.
    /// </summary>
    private static (float Z, bool Walkable) Floor(Prepared p, in NavBox voxel, double s, int clipClass, float floorNormalZ)
    {
        double best = double.NegativeInfinity;
        bool walkable = false;
        NavBox face = voxel with { MaxZ = voxel.MinZ };
        for (int b = 0; b < p.Brushes.Length; b++)
        {
            if ((p.SolidMask[b] & (1 << clipClass)) == 0)
            {
                continue;
            }

            NavBrush brush = p.Brushes[b];
            double height;
            bool up;
            if (brush.IsAxial)
            {
                bool under = brush.MaxX > voxel.MinX + E && brush.MinX < voxel.MaxX - E
                    && brush.MaxY > voxel.MinY + E && brush.MinY < voxel.MaxY - E
                    && brush.MaxZ > voxel.MinZ - s + E && brush.MinZ < voxel.MinZ - E;
                if (!under)
                {
                    continue;
                }

                height = brush.MaxZ;
                up = true;
            }
            else
            {
                double sink = brush.GrowthThreshold(face, NavGrowth.Downward, out double contactZ);
                if (!(sink < s))
                {
                    continue;
                }

                height = voxel.MinZ - sink + E;
                up = contactZ >= floorNormalZ;
            }

            if (height > best + 1e-9)
            {
                best = height;
                walkable = up;
            }
            else if (height >= best - 1e-9)
            {
                walkable |= up;
            }
        }

        return double.IsNegativeInfinity(best) ? (0, false) : ((float)best, walkable);
    }

    /// <summary>A record's bytes, written into the build's buffer (so looking it up allocates nothing).</summary>
    private static ReadOnlySpan<byte> Encode(Scratch scratch, List<Nav3dCorner> stair, List<Nav3dDynamicCorner> dynamics, List<int> brushes)
    {
        int length = Nav3dClearance.HeaderBytes + (stair.Count * Nav3dClearance.CornerBytes)
            + (dynamics.Count * Nav3dClearance.DynamicBytes) + (brushes.Count * Nav3dClearance.BrushBytes);
        if (scratch.Buffer.Length < length)
        {
            scratch.Buffer = new byte[Math.Max(length, scratch.Buffer.Length * 2)];
        }

        Span<byte> b = scratch.Buffer.AsSpan(0, length);
        b.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)stair.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], (ushort)dynamics.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], (ushort)brushes.Count);
        int at = Nav3dClearance.HeaderBytes;
        foreach (Nav3dCorner c in stair)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b[at..], c.Width);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 4)..], c.Top);
            at += Nav3dClearance.CornerBytes;
        }

        foreach (Nav3dDynamicCorner d in dynamics)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b[at..], (uint)d.Obstacle);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 4)..], d.Corner.Width);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 8)..], d.Corner.Top);
            at += Nav3dClearance.DynamicBytes;
        }

        foreach (int brush in brushes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b[at..], (uint)brush);
            at += Nav3dClearance.BrushBytes;
        }

        return b;
    }

    /// <summary>A brush's corner at a voxel before normalising: width threshold, bottom, top, and which classes it is solid for.</summary>
    internal readonly record struct Candidate(double Width, double Bottom, double Top, byte Mask);

    /// <summary>Candidates in width order, then bottom: a struct, so a sort calls it directly rather than through a delegate.</summary>
    private readonly struct CandidateOrder : IComparer<Candidate>
    {
        public int Compare(Candidate a, Candidate b) =>
            a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Bottom.CompareTo(b.Bottom);
    }

    /// <summary>A sloped solid's index and the floor under its width threshold at the column.</summary>
    private readonly record struct SlopedFloor(double Floor, int Brush);

    /// <summary>Sloped solids by floor, then index, so the walk is the same every run.</summary>
    private readonly struct SlopedFloorOrder : IComparer<SlopedFloor>
    {
        public int Compare(SlopedFloor a, SlopedFloor b) =>
            a.Floor != b.Floor ? a.Floor.CompareTo(b.Floor) : a.Brush.CompareTo(b.Brush);
    }

    /// <summary>The build's reusable lists: one set per build, so a column allocates nothing per voxel.</summary>
    private sealed class Scratch
    {
        public List<Candidate> Axial { get; } = [];

        public List<Candidate> Merged { get; } = [];

        public List<SlopedFloor> Sloped { get; } = [];

        public List<Candidate> Pending { get; } = [];

        public List<Nav3dCorner> Stair { get; } = [];

        public List<Nav3dCorner> Own { get; } = [];

        public List<Nav3dDynamicCorner> Dynamics { get; } = [];

        public List<int> Brushes { get; } = [];

        public byte[] Buffer { get; set; } = new byte[256];
    }

    /// <summary>The geometry sorted by what each brush does: solid for which classes, and axial, sloped or overhanging; water; ladder.</summary>
    private sealed class Prepared
    {
        public Prepared(NavGeometry geometry)
        {
            Brushes = [.. geometry.Brushes];
            SolidMask = new byte[Brushes.Length];
            List<int> axial = [];
            List<int> sloped = [];
            List<int> overhang = [];
            List<int> water = [];
            List<int> ladder = [];
            for (int b = 0; b < Brushes.Length; b++)
            {
                int contents = Brushes[b].Contents;
                SolidMask[b] = (byte)(((contents & Nav3dFormat.PlayerSolidMask) != 0 ? 1 : 0) | ((contents & Nav3dFormat.NpcSolidMask) != 0 ? 2 : 0));
                if ((contents & Nav3dFormat.WaterContents) != 0)
                {
                    water.Add(b);
                }

                if ((contents & Nav3dFormat.LadderContents) != 0)
                {
                    ladder.Add(b);
                }

                if (SolidMask[b] == 0)
                {
                    continue;
                }

                (Brushes[b].IsAxial ? axial : Brushes[b].IsOverhang ? overhang : sloped).Add(b);
            }

            AxialSolid = [.. axial];
            SlopedSolid = [.. sloped];
            Overhang = [.. overhang];
            Water = [.. water];
            Ladder = [.. ladder];
            LadderVolumes = [.. geometry.Ladders];
            Obstacles = [.. geometry.Obstacles.Select(o => o.Brushes
                .Select(b => (b, (byte)(((b.Contents & Nav3dFormat.PlayerSolidMask) != 0 ? 1 : 0) | ((b.Contents & Nav3dFormat.NpcSolidMask) != 0 ? 2 : 0))))
                .ToArray())];
        }

        public NavBrush[] Brushes { get; }

        public byte[] SolidMask { get; }

        public int[] AxialSolid { get; }

        public int[] SlopedSolid { get; }

        /// <summary>The overhanging solids, in geometry order: a record's brush index is a position in this list.</summary>
        public int[] Overhang { get; }

        public int[] Water { get; }

        public int[] Ladder { get; }

        public Rooms.Box[] LadderVolumes { get; }

        public (NavBrush Brush, byte Mask)[][] Obstacles { get; }
    }

    /// <summary>The overhanging solid brushes of a geometry, in the order a record's brush indices count them.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns>The brushes.</returns>
    public static IReadOnlyList<NavBrush> OverhangBrushes(NavGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return [.. geometry.Brushes.Where(b => b.IsOverhang && !b.IsAxial
            && (b.Contents & (Nav3dFormat.PlayerSolidMask | Nav3dFormat.NpcSolidMask)) != 0)];
    }
}
