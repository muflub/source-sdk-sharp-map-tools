//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapPreview.Scene;

/// <summary>
/// The light a model receives at a point, from the per-leaf ambient cubes vrad
/// bakes: the same source the engine lights props and players with when a
/// static prop has no per-vertex lighting.
/// </summary>
/// <remarks>
/// <para>
/// A point is located in its leaf by walking the BSP nodes; of the leaf's
/// ambient samples, the nearest is used (the engine blends the nearest few;
/// for a preview the difference is a soft gradient, not a different colour).
/// A leaf with no samples, or a map with no ambient lumps, falls back to a
/// constant so a prop is never black merely because the data is absent.
/// </para>
/// <para>
/// The cube is evaluated the engine's way: each axis contributes the face it
/// points toward, weighted by the normal's component squared, so the weights
/// sum to one for a unit normal.
/// </para>
/// </remarks>
public sealed class AmbientLight
{
    /// <summary>The light used where the map has no ambient data: mid grey.</summary>
    public static Vec3 Fallback => new(0.5f, 0.5f, 0.5f);

    private readonly DNode[] _nodes;
    private readonly DPlane[] _planes;
    private readonly DLeaf[] _leaves;
    private readonly DLeafAmbientIndex[] _index;
    private readonly DLeafAmbientLighting[] _samples;

    private AmbientLight(DNode[] nodes, DPlane[] planes, DLeaf[] leaves, DLeafAmbientIndex[] index, DLeafAmbientLighting[] samples)
    {
        _nodes = nodes;
        _planes = planes;
        _leaves = leaves;
        _index = index;
        _samples = samples;
    }

    /// <summary>Reads a map's ambient lighting.</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="hdr">Whether to read the HDR lumps (falling back to LDR when absent).</param>
    /// <returns>The field.</returns>
    public static AmbientLight FromBsp(BspData bsp, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        BspLump indexLump = BspLump.LeafAmbientIndex;
        BspLump sampleLump = BspLump.LeafAmbientLighting;
        if (hdr && bsp[BspLump.LeafAmbientLightingHdr].Data.Length > 0)
        {
            indexLump = BspLump.LeafAmbientIndexHdr;
            sampleLump = BspLump.LeafAmbientLightingHdr;
        }
        else if (!hdr && bsp[BspLump.LeafAmbientLighting].Data.Length == 0)
        {
            indexLump = BspLump.LeafAmbientIndexHdr;
            sampleLump = BspLump.LeafAmbientLightingHdr;
        }

        return new AmbientLight(
            BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray(),
            BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray(),
            BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray(),
            BspStructView.As<DLeafAmbientIndex>(bsp[indexLump]).ToArray(),
            BspStructView.As<DLeafAmbientLighting>(bsp[sampleLump]).ToArray());
    }

    /// <summary>The leaf a point is in, or -1 when the map has no tree.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The leaf index.</returns>
    public int LeafOf(Vec3 point)
    {
        if (_nodes.Length == 0)
        {
            return _leaves.Length > 0 ? 0 : -1;
        }

        int node = 0;
        for (int guard = 0; guard < _nodes.Length + 1 && node >= 0; guard++)
        {
            DNode n = _nodes[node];
            DPlane plane = _planes[n.PlaneNum];
            node = Vec3.Dot(plane.Normal, point) - plane.Dist >= 0 ? n.Children[0] : n.Children[1];
        }

        int leaf = -1 - node;
        return leaf >= 0 && leaf < _leaves.Length ? leaf : -1;
    }

    /// <summary>The area (as areaportals divide the map) a point is in, or -1.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The area index.</returns>
    public int AreaOf(Vec3 point)
    {
        int leaf = LeafOf(point);
        return leaf < 0 ? -1 : _leaves[leaf].GetArea();
    }

    /// <summary>The ambient cube nearest a point, as six linear colours (+x, -x, +y, -y, +z, -z).</summary>
    /// <param name="point">The point.</param>
    /// <returns>The cube, or null when the point's leaf has no samples.</returns>
    public Vec3[]? CubeAt(Vec3 point)
    {
        int leaf = LeafOf(point);
        if (leaf < 0 || leaf >= _index.Length)
        {
            return null;
        }

        DLeafAmbientIndex entry = _index[leaf];
        if (entry.AmbientSampleCount == 0 || entry.FirstAmbientSample + entry.AmbientSampleCount > _samples.Length)
        {
            return null;
        }

        DLeaf l = _leaves[leaf];
        Vec3 min = new(l.Mins[0], l.Mins[1], l.Mins[2]);
        Vec3 size = new(l.Maxs[0] - l.Mins[0], l.Maxs[1] - l.Mins[1], l.Maxs[2] - l.Mins[2]);
        int best = entry.FirstAmbientSample;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < entry.AmbientSampleCount; i++)
        {
            DLeafAmbientLighting sample = _samples[entry.FirstAmbientSample + i];
            Vec3 at = min + new Vec3(size.X * sample.X / 255f, size.Y * sample.Y / 255f, size.Z * sample.Z / 255f);
            float distance = (at - point).LengthSquared();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry.FirstAmbientSample + i;
            }
        }

        Vec3[] cube = new Vec3[6];
        for (int face = 0; face < 6; face++)
        {
            cube[face] = _samples[best].Cube.Color[face].ToLinear() * BspSceneBuilder.LightScale;
        }

        return cube;
    }

    /// <summary>Evaluates a cube for a normal.</summary>
    /// <param name="cube">Six linear colours, +x, -x, +y, -y, +z, -z.</param>
    /// <param name="normal">A unit normal.</param>
    /// <returns>The light arriving on a surface facing that way.</returns>
    public static Vec3 Evaluate(ReadOnlySpan<Vec3> cube, Vec3 normal)
    {
        float xx = normal.X * normal.X;
        float yy = normal.Y * normal.Y;
        float zz = normal.Z * normal.Z;
        return (cube[normal.X >= 0 ? 0 : 1] * xx) + (cube[normal.Y >= 0 ? 2 : 3] * yy) + (cube[normal.Z >= 0 ? 4 : 5] * zz);
    }
}
