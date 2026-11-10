//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Where each cell of a room larger than one cell starts in the room's own
/// tree, and which nodes above them the link leaves out (the rooms design,
/// 17.7 option A and 17.8).
/// </summary>
/// <param name="CellsX">The footprint's cells along the room's +x.</param>
/// <param name="CellsY">The footprint's cells along its +y.</param>
/// <param name="Roots">
/// Per covered cell, <c>x + y × CellsX</c>, the child the link routes that
/// cell to: a node index of the room's tree, or <c>-(leaf + 1)</c> for a
/// leaf (a cell the author filled solid).
/// </param>
/// <param name="BlockNodes">
/// The room's block nodes, ascending: the axial nodes on cell faces that
/// stitch the cells' subtrees together above them. The link omits them.
/// </param>
/// <remarks>
/// <para>
/// <b>Why the room is compiled on cell-sized blocks.</b> The level's top
/// tree routes each grid cell to a child, and the engine gives every node
/// one parent, so one room root reached from several cells would be drawn
/// through only one of them. A larger room is therefore compiled with vbsp's
/// block size set to the cell size (<see cref="Bsp.Driver.Vbsp.CompileRoomAsync"/>):
/// vbsp builds each block's tree from the brushes clipped to the block and
/// stitches the blocks under axial planes on the block faces, so the room's
/// tree is a kd tree over its cells with one self-contained subtree per
/// cell. The link points each covered cell's top-tree leaf at that cell's
/// subtree, exactly as a one-cell room's cell goes to its root, and leaves
/// the stitching nodes out.
/// </para>
/// <para>
/// <b>Found by walking, not recorded by vbsp.</b> <see cref="Derive"/> walks
/// the compiled tree from its head the way vbsp stitched it: while the
/// region a node stands for spans more than one cell, the node must split it
/// on a cell face; a region of one covered cell is that cell's root. A leaf
/// may stand for several cells only when it is solid (vbsp's prune joins
/// cells an author filled solid). Anything else is refused, as the rooms
/// design's 17.3 says, as a bug in the room compile.
/// </para>
/// <para>
/// <b>A surface on an internal cell face is refused.</b> vbsp puts a face on
/// the node whose plane it lies on, and the engine draws a node's faces by
/// walking to that node. A brush side lying exactly on one of the room's
/// internal cell faces, with open space in front of it, would therefore be
/// a face of a stitching node, which the link leaves out, and would never be
/// drawn. The room's design (17.8) lets brushes cross internal cell faces
/// freely, and a crossing brush's cut sides are never faces; only a visible
/// side lying in the plane itself is refused, naming the plane.
/// </para>
/// </remarks>
internal sealed record RoomCellTree(int CellsX, int CellsY, IReadOnlyList<int> Roots, IReadOnlyList<int> BlockNodes)
{
    /// <summary>The child the link routes a covered cell to (<see cref="Roots"/>).</summary>
    /// <param name="x">The room-local cell along +x.</param>
    /// <param name="y">The room-local cell along +y.</param>
    /// <returns>A node index, or <c>-(leaf + 1)</c>.</returns>
    public int RootOf(int x, int y) => Roots[x + (y * CellsX)];

    /// <summary>Whether a node of the room's tree is one the link leaves out.</summary>
    /// <param name="node">The node index.</param>
    /// <returns>True for a block node.</returns>
    public bool IsOmitted(int node) => BlockNodesSearch(node) >= 0;

    /// <summary>
    /// How many omitted nodes come before a node: what the link subtracts
    /// from the node's index to place it among the room's kept nodes.
    /// </summary>
    /// <param name="node">A kept node's index.</param>
    /// <returns>The number of block nodes with a smaller index.</returns>
    public int OmittedBefore(int node)
    {
        int at = BlockNodesSearch(node);
        return at >= 0 ? at : ~at;
    }

    private int BlockNodesSearch(int node)
    {
        int lo = 0, hi = BlockNodes.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int value = BlockNodes[mid];
            if (value == node)
            {
                return mid;
            }

            if (value < node)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    /// <summary>The cell roots and block nodes of a larger room's compile, or null for a room of one cell.</summary>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">Its compile.</param>
    /// <returns>The tree's cells, or null when the room is one cell.</returns>
    /// <exception cref="RoomLintException">The tree does not split at every cell face, or a surface lies on an internal cell face.</exception>
    public static RoomCellTree? Derive(RoomDefinition definition, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        if (!definition.IsLarge)
        {
            return null;
        }

        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        DLeaf[] leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        DModel world = BspStructView.As<DModel>(bsp[BspLump.Models])[0];
        int w = definition.CellsX, d = definition.CellsY;
        float cell = definition.CellSize;
        int[] roots = new int[w * d];
        Array.Fill(roots, int.MinValue);
        List<int> blocks = [];

        // A region in whole cells, inclusive; the outermost is unbounded.
        const int Far = int.MaxValue / 4;
        Walk(world.HeadNode, -Far, Far, -Far, Far);
        blocks.Sort();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i] == int.MinValue)
            {
                throw Unsplit(i % w, i / w);
            }
        }

        return new RoomCellTree(w, d, roots, blocks);

        void Walk(int child, int x0, int x1, int y0, int y1)
        {
            // The covered cells of the region.
            int cx0 = Math.Max(x0, 0), cx1 = Math.Min(x1, w - 1);
            int cy0 = Math.Max(y0, 0), cy1 = Math.Min(y1, d - 1);
            if (cx0 > cx1 || cy0 > cy1)
            {
                return;
            }

            bool single = x0 == x1 && y0 == y1;
            if (child < 0)
            {
                bool solid = (leaves[-(child + 1)].Contents & (int)BrushContents.Solid) != 0;
                if (!single && !solid)
                {
                    throw Unsplit(cx0, cy0);
                }

                Fill(child, cx0, cx1, cy0, cy1);
                return;
            }

            if (single)
            {
                roots[x0 + (y0 * w)] = child;
                return;
            }

            DNode node = nodes[child];
            DPlane plane = planes[node.PlaneNum];
            int axis = plane.Normal.X == 1 && plane.Normal.Y == 0 && plane.Normal.Z == 0 ? 0
                : plane.Normal.X == 0 && plane.Normal.Y == 1 && plane.Normal.Z == 0 ? 1
                : -1;
            float k = plane.Dist / cell;
            int at = (int)k;
            (int lo, int hi) = axis == 0 ? (x0, x1) : (y0, y1);
            if (axis < 0 || k != at || at <= lo || at > hi)
            {
                throw Unsplit(cx0, cy0);
            }

            if (node.NumFaces > 0)
            {
                throw new RoomLintException(
                    string.Create(CultureInfo.InvariantCulture, $"room {definition.Name}: a surface lies on the cell face {(axis == 0 ? 'x' : 'y')} = {plane.Dist:0.###} inside its")
                    + string.Create(CultureInfo.InvariantCulture, $" {w} x {d} footprint; the link routes each cell to its own part of the room and would drop it.")
                    + " Move the surface off the cell face, or let its brush cross it.");
            }

            blocks.Add(child);
            if (axis == 0)
            {
                Walk(node.Children[0], at, x1, y0, y1);
                Walk(node.Children[1], x0, at - 1, y0, y1);
            }
            else
            {
                Walk(node.Children[0], x0, x1, at, y1);
                Walk(node.Children[1], x0, x1, y0, at - 1);
            }
        }

        void Fill(int child, int cx0, int cx1, int cy0, int cy1)
        {
            for (int y = cy0; y <= cy1; y++)
            {
                for (int x = cx0; x <= cx1; x++)
                {
                    roots[x + (y * w)] = child;
                }
            }
        }

        RoomLintException Unsplit(int i, int j) => new(string.Create(CultureInfo.InvariantCulture,
            $"room {definition.Name}: its compile does not split at every cell face of its {w} x {d} footprint (cell ({i}, {j})); this is a bug in the room compile."));
    }
}
