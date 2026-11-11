//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>
/// The written world tree the prop emitters walk: <c>dnodes</c>,
/// <c>dplanes</c> and <c>dleafs</c> as <c>WriteBSP</c> left them.
/// </summary>
/// <remarks>
/// Both emitters run in <c>EndBSPFile</c> after the tree is written, and read
/// it, not the in-memory <c>node_t</c> tree. Under stage isolation the lumps
/// are stock's own; in the managed driver they are whatever 3e wrote.
/// </remarks>
/// <param name="Nodes">LUMP_NODES.</param>
/// <param name="Planes">LUMP_PLANES.</param>
/// <param name="LeafContents">Each leaf's <c>contents</c>, from LUMP_LEAFS.</param>
public sealed record BspTreeView(DNode[] Nodes, DPlane[] Planes, int[] LeafContents)
{
    /// <summary>LUMP_LEAFS whole, when the tree came from version-1 leaves; the cooked leaf walk reads it.</summary>
    public DLeaf[]? Leafs { get; init; }

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    public const int ContentsSolid = 0x1;

    /// <summary>Reads the three lumps of a BSP.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The view.</returns>
    public static BspTreeView FromBsp(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        int[] contents;
        DLeaf[]? full = null;
        if (bsp[BspLump.Leafs].Version == 0)
        {
            DLeafVersion0[] leafs = BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]).ToArray();
            contents = [.. leafs.Select(l => l.Contents)];
        }
        else
        {
            full = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
            contents = [.. full.Select(l => l.Contents)];
        }

        return new BspTreeView(
            BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray(),
            BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray(),
            contents)
        {
            Leafs = full,
        };
    }

    /// <summary>
    /// <c>ComputeDetailLeaf</c>: the leaf a
    /// point falls in, a point exactly on a plane going FRONT.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The leaf index.</returns>
    public int LeafOf(Vec3 point)
    {
        int node = 0;
        while (node >= 0)
        {
            DNode n = Nodes[node];
            DPlane plane = Planes[n.PlaneNum];

            // DotProduct(pt, normal): the point first.
            node = Vec3.Dot(point, plane.Normal) < plane.Dist ? n.Children[1] : n.Children[0];
        }

        return -node - 1;
    }

    /// <summary>
    /// <see cref="LeafOf"/> for every point of a vertical line at once: the
    /// leaf of each point (x, y, z) for z in <paramref name="heights"/>, the
    /// same leaf <see cref="LeafOf"/> gives that point.
    /// </summary>
    /// <param name="x">The line's x.</param>
    /// <param name="y">The line's y.</param>
    /// <param name="heights">The points' heights, rising (each at least the one before).</param>
    /// <param name="leaves">Where each point's leaf goes, one per height.</param>
    /// <remarks>
    /// <para>
    /// The points are walked down the tree together, and each node splits
    /// them where its plane does. <see cref="LeafOf"/> sends a point back
    /// when <c>Dot(point, normal) &lt; dist</c>, computed as
    /// <c>(x·nx + y·ny) + z·nz</c>: along the line only the last product
    /// changes, and a rounded product of a fixed number and a rounded sum
    /// with a fixed number only rise (or only fall) as z does. So the test
    /// is false then true, or true then false, up the line, never back: the
    /// points a node sends back are the low ones or the high ones, and a
    /// binary search with the same test finds where they end. Every point
    /// lands where its own walk takes it, after a handful of tests per node
    /// instead of one per point.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The spans differ in length, or the heights do not rise.</exception>
    public void LeavesAlong(float x, float y, ReadOnlySpan<float> heights, Span<int> leaves)
    {
        if (heights.Length != leaves.Length)
        {
            throw new ArgumentException("one leaf per height.", nameof(leaves));
        }

        for (int i = 1; i < heights.Length; i++)
        {
            if (!(heights[i] >= heights[i - 1]))
            {
                throw new ArgumentException("the heights rise.", nameof(heights));
            }
        }

        Along(0, x, y, heights, leaves, 0, heights.Length);
    }

    private void Along(int node, float x, float y, ReadOnlySpan<float> heights, Span<int> leaves, int lo, int hi)
    {
        while (lo < hi)
        {
            if (node < 0)
            {
                leaves[lo..hi].Fill(-node - 1);
                return;
            }

            DNode n = Nodes[node];
            DPlane plane = Planes[n.PlaneNum];
            Vec3 normal = plane.Normal;
            float dist = plane.Dist;

            // The first point whose side differs from the lowest point's: the
            // test changes at most once along the line.
            bool lowBack = Back(x, y, heights[lo], normal, dist);
            int first = lo + 1;
            int last = hi;
            if (lowBack == Back(x, y, heights[hi - 1], normal, dist))
            {
                first = hi;
            }
            else
            {
                while (first < last)
                {
                    int mid = first + ((last - first) / 2);
                    if (Back(x, y, heights[mid], normal, dist) == lowBack)
                    {
                        first = mid + 1;
                    }
                    else
                    {
                        last = mid;
                    }
                }
            }

            int lowChild = lowBack ? n.Children[1] : n.Children[0];
            if (first < hi)
            {
                Along(lowBack ? n.Children[0] : n.Children[1], x, y, heights, leaves, first, hi);
            }

            node = lowChild;
            hi = first;
        }
    }

    /// <summary><see cref="LeafOf"/>'s test at one node: whether the point goes back.</summary>
    private static bool Back(float x, float y, float z, Vec3 normal, float dist) => Vec3.Dot(new Vec3(x, y, z), normal) < dist;
}
