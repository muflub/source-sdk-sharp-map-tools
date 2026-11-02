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

namespace SourceSharp.MapTools.Nav;

/// <summary>What <see cref="NavFloorCheck"/> checks with: the agent, how far down it looks, and how many threads.</summary>
public sealed record NavFloorCheckOptions
{
    /// <summary>The agent whose places to stand are checked, and whose box is traced down from each.</summary>
    public Nav3dWalker Walker { get; init; } = Nav3dWalker.Player;

    /// <summary>How far below a floor the map's collision must have ground; NaN for the file's step height.</summary>
    public double Depth { get; init; } = double.NaN;

    /// <summary>How many leaves are checked at once. The report is the same at any degree.</summary>
    public int MaxDegree { get; init; } = 1;
}

/// <summary>A place to stand with no ground under it: its leaf, and the centre of its bottom voxel at its floor's height.</summary>
/// <param name="Leaf">The leaf.</param>
/// <param name="Position">Where the agent would stand: the voxel's horizontal centre, at the floor.</param>
public sealed record NavFloorFinding(int Leaf, Vec3 Position);

/// <summary>What <see cref="NavFloorCheck.Check"/> found.</summary>
/// <param name="Standable">How many leaves are places the agent stands.</param>
/// <param name="Depth">How far down it looked, in units.</param>
/// <param name="Missing">The places to stand with no ground within <paramref name="Depth"/> under the agent's box, in leaf order.</param>
/// <param name="Warnings">What the check could not take into account: a static prop whose model the content lacks.</param>
public sealed record NavFloorReport(int Standable, double Depth, IReadOnlyList<NavFloorFinding> Missing, IReadOnlyList<string> Warnings);

/// <summary>
/// Checks a built navigation against its compiled map: every place an agent
/// stands (<see cref="Nav3dWalk.IsStand"/>) must have ground under it in the
/// map's own collision, so no route leads over nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The collision</b> is what the game moves a player against, taken
/// exactly, not the navigation's over-estimates: the world's brushes of the
/// agent's clip class, each displacement's triangles (a one-unit shell under
/// its surface), and each solid static prop's convex pieces from its
/// model's <c>.phy</c>, turned and placed, or its hull box turned when the
/// model has no pieces. A prop whose model the content lacks is left out
/// and named in a warning; the check can then report a floor that prop
/// really holds up.
/// </para>
/// <para>
/// <b>The test.</b> The agent stands at the horizontal centre of the leaf's
/// bottom voxel with its feet on the leaf's floor (the navigation's own
/// answer, <see cref="Nav3dReader.StandableAtCentre"/>). Ground is under it
/// when some solid overlaps the box of the agent's footprint, from half a
/// unit above its feet down <see cref="NavFloorCheckOptions.Depth"/> (the
/// step height by default): moved down that far, the box would land. The
/// footprint is drawn in by 1/32 unit on each side, so a wall that only
/// touches the box's side is not ground. A navigation whose floors sit on
/// real surfaces finds ground at once; one whose solid was larger than the
/// map's (a box around a sloped or turned piece) can put a floor where the
/// agent would fall farther than a step, and each such place is reported.
/// </para>
/// <para>
/// The report is in leaf order and the same at any thread count. The check
/// holds only memory it allocates.
/// </para>
/// </remarks>
public static class NavFloorCheck
{
    /// <summary>How far a box's footprint is drawn in, so a solid that only touches its side is not under it.</summary>
    public const double SideClearance = 1.0 / 32;

    /// <summary>How far above the feet the box starts, so a surface exactly at the floor is met.</summary>
    public const double AboveFeet = 0.5;

    private const double BucketSize = 64;

    /// <summary>Checks a navigation's floors against its map.</summary>
    /// <param name="nav">The navigation, built from <paramref name="bsp"/>.</param>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="modelBounds">A model's hull by path, or null when unknown.</param>
    /// <param name="modelPieces">A model's <c>.phy</c> pieces by path (<see cref="NavModelBounds.LoadPiecesAsync"/>), or null when unknown.</param>
    /// <param name="options">The agent, the depth and the threads; null for the player, its step, one thread.</param>
    /// <param name="cancellationToken">Cancels the check between leaves.</param>
    /// <returns>The report.</returns>
    public static NavFloorReport Check(
        Nav3dReader nav,
        BspData bsp,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds,
        Func<string, IReadOnlyList<NavPiece>?>? modelPieces,
        NavFloorCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentNullException.ThrowIfNull(bsp);
        options ??= new NavFloorCheckOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDegree, 1);
        Nav3dWalker walker = options.Walker;
        double depth = double.IsNaN(options.Depth) ? nav.StepHeight : options.Depth;
        ArgumentOutOfRangeException.ThrowIfNegative(depth);

        (IReadOnlyList<NavBrush> solids, IReadOnlyList<string> warnings) = Collision(bsp, modelBounds, modelPieces, walker.ClipClass);
        Dictionary<(int, int), List<int>> buckets = [];
        for (int i = 0; i < solids.Count; i++)
        {
            NavBrush s = solids[i];
            for (int by = Bucket(s.MinY); by <= Bucket(s.MaxY); by++)
            {
                for (int bx = Bucket(s.MinX); bx <= Bucket(s.MaxX); bx++)
                {
                    if (!buckets.TryGetValue((bx, by), out List<int>? list))
                    {
                        buckets[(bx, by)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }

        int leaves = nav.LeafCount;
        bool[] stand = new bool[leaves];
        bool[] missing = new bool[leaves];
        double r = (walker.Width * 0.5) - SideClearance;
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.MaxDegree, CancellationToken = cancellationToken };
        System.Threading.Tasks.Parallel.For(0, leaves, parallel, l =>
        {
            if (!Nav3dWalk.IsStand(nav, walker, l))
            {
                return;
            }

            stand[l] = true;
            (Vec3 mins, Vec3 maxs) = nav.LeafBounds(l);
            double cx = (mins.X + (double)maxs.X) * 0.5;
            double cy = (mins.Y + (double)maxs.Y) * 0.5;
            double floor = nav.Leaf(l).FloorZ(walker.ClipClass);
            NavBox box = new(cx - r, cy - r, floor - depth, cx + r, cy + r, floor + AboveFeet);
            missing[l] = !Grounded(box, solids, buckets);
        });

        List<NavFloorFinding> found = [];
        for (int l = 0; l < leaves; l++)
        {
            if (missing[l])
            {
                (Vec3 mins, Vec3 maxs) = nav.LeafBounds(l);
                found.Add(new NavFloorFinding(l, new Vec3((mins.X + maxs.X) * 0.5f, (mins.Y + maxs.Y) * 0.5f, nav.Leaf(l).FloorZ(walker.ClipClass))));
            }
        }

        return new NavFloorReport(stand.Count(s => s), depth, found, warnings);
    }

    /// <summary>
    /// A map's collision for one clip class, exactly: the world's brushes of
    /// the class, the displacements' surfaces, and the solid static props'
    /// convex pieces (or turned hull boxes).
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="modelBounds">A model's hull by path, or null when unknown.</param>
    /// <param name="modelPieces">A model's pieces by path, or null when unknown.</param>
    /// <param name="clipClass">Whose collision.</param>
    /// <returns>The solids, and one warning per model the content lacks.</returns>
    internal static (IReadOnlyList<NavBrush> Solids, IReadOnlyList<string> Warnings) Collision(
        BspData bsp,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds,
        Func<string, IReadOnlyList<NavPiece>?>? modelPieces,
        Nav3dClipClass clipClass)
    {
        int mask = Nav3dFormat.SolidMask(clipClass);
        List<NavBrush> solids = [.. NavGeometry.FromBsp(bsp).Brushes.Where(b => (b.Contents & mask) != 0)];
        solids.AddRange(NavGeometry.Displacements(bsp, 1).Where(b => (b.Contents & mask) != 0));

        List<string> warnings = [];
        if (NavGeometry.StaticPropLumpOf(bsp) is { } lump)
        {
            SortedDictionary<string, int> lacking = new(StringComparer.Ordinal);
            foreach (StaticProp prop in lump.Props)
            {
                if (prop.Solid == 0 || prop.PropType >= lump.ModelNames.Count)
                {
                    continue;
                }

                string model = lump.ModelNames[prop.PropType];
                IReadOnlyList<NavPiece>? pieces = modelPieces?.Invoke(model) is { Count: > 0 } p ? p
                    : modelBounds?.Invoke(model) is { } hull ? [NavPiece.Box(hull.Mins, hull.Maxs)]
                    : null;
                if (pieces is null)
                {
                    lacking[model] = lacking.GetValueOrDefault(model) + 1;
                    continue;
                }

                foreach (NavPiece piece in pieces)
                {
                    if (NavGeometry.ConvexBrush(NavGeometry.TurnedPoints(prop.Origin, prop.Angles, piece), piece.Triangles, NavGeometry.StaticPropContents)
                        is { } brush)
                    {
                        solids.Add(brush);
                    }
                }
            }

            warnings.AddRange(lacking.Select(m => string.Create(CultureInfo.InvariantCulture,
                $"static prop model \"{m.Key}\" has no collision the content gives, so its {m.Value} solid prop{(m.Value == 1 ? string.Empty : "s")} {(m.Value == 1 ? "is" : "are")} left out of the check.")));
        }

        return (solids, warnings);
    }

    private static bool Grounded(in NavBox box, IReadOnlyList<NavBrush> solids, Dictionary<(int, int), List<int>> buckets)
    {
        for (int by = Bucket(box.MinY); by <= Bucket(box.MaxY); by++)
        {
            for (int bx = Bucket(box.MinX); bx <= Bucket(box.MaxX); bx++)
            {
                if (buckets.TryGetValue((bx, by), out List<int>? list))
                {
                    foreach (int i in list)
                    {
                        if (solids[i].Overlaps(box))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static int Bucket(double v) => (int)Math.Floor(v / BucketSize);
}
