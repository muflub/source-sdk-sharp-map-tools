//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Assets;

/// <summary>
/// One triangle of a studio model's level-of-detail-zero mesh, in model space:
/// corners, per-corner normals and texture coordinates, and the skin slot its
/// mesh draws with.
/// </summary>
/// <param name="A">The first corner.</param>
/// <param name="B">The second corner.</param>
/// <param name="C">The third corner.</param>
/// <param name="NormalA">The first corner's vertex normal, as the VVD stores it.</param>
/// <param name="NormalB">The second corner's vertex normal.</param>
/// <param name="NormalC">The third corner's vertex normal.</param>
/// <param name="UvA">The first corner's texture coordinate.</param>
/// <param name="UvB">The second corner's texture coordinate.</param>
/// <param name="UvC">The third corner's texture coordinate.</param>
/// <param name="Slot">
/// The mesh's material slot: the column of the skin table, which the active
/// skin family maps to a texture.
/// </param>
public readonly record struct StudioTriangle(
    Vec3 A,
    Vec3 B,
    Vec3 C,
    Vec3 NormalA,
    Vec3 NormalB,
    Vec3 NormalC,
    (float U, float V) UvA,
    (float U, float V) UvB,
    (float U, float V) UvC,
    int Slot);

/// <summary>Which model of each body part <see cref="StudioMeshReader"/> reads.</summary>
/// <remarks>
/// A body part's models are alternatives (a bodygroup): the engine draws one of
/// them, chosen by the entity's body value, and with no value set that is the
/// first. Reading all of them would overlay every variant at once.
/// </remarks>
public enum StudioBodyChoice
{
    /// <summary>
    /// The first model of each part that has any triangles. Suits a catalogue
    /// thumbnail: when a part's first option is the usual empty "off" choice,
    /// the next one is what the part looks like when shown.
    /// </summary>
    FirstDrawn,

    /// <summary>
    /// Model zero of each part, empty or not: what the engine draws for an
    /// entity with no body value set, which is what a map preview must show.
    /// </summary>
    Default,
}

/// <summary>
/// Reads the triangles a studio model draws at level of detail zero, resolving
/// the VTX strip hierarchy through the MDL's models and meshes into the VVD's
/// vertices.
/// </summary>
/// <remarks>
/// <para>
/// Shared by everything that draws a model (the asset database's thumbnails
/// and the map preview) so the one walk every studio reader gets wrong once is
/// written once.
/// </para>
/// <para>
/// The two-step: a strip index names a GROUP vertex, whose
/// <c>OrigMeshVertId</c> names a vertex of the MESH, which the model's byte
/// offset into the VVD and the mesh's own run offset base into the file's
/// vertex block. Skipping the VVD's fixup table picks the right COUNT of
/// vertices and the wrong ones, so callers pass a <see cref="VvdFile"/> whose
/// level-zero vertices have their fixups applied.
/// </para>
/// <para>
/// A strip flagged as a triangle strip contributes one triangle per index past
/// the first two, every second one wound the other way so the surface normal
/// is continuous along the strip; any other strip is a triangle list read three
/// indices at a time. A triangle whose indices name vertices the file does not
/// hold is dropped, not guessed at. The VTX and MDL hierarchies are walked in
/// lockstep and stop at whichever runs out first, the same way the reference
/// loaders pair them.
/// </para>
/// </remarks>
public static class StudioMeshReader
{
    /// <summary>Collects the level-of-detail-zero triangles a model draws.</summary>
    /// <param name="mdl">The model.</param>
    /// <param name="vvd">The shared vertex block.</param>
    /// <param name="vtx">The strip groups (any hardware flavour: dx90, dx80, sw).</param>
    /// <param name="bodies">Which model of each body part to read.</param>
    /// <returns>One entry per drawn triangle, in file order.</returns>
    public static IReadOnlyList<StudioTriangle> Lod0Triangles(
        MdlFile mdl,
        VvdFile vvd,
        VtxFile vtx,
        StudioBodyChoice bodies = StudioBodyChoice.FirstDrawn)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ArgumentNullException.ThrowIfNull(vvd);
        ArgumentNullException.ThrowIfNull(vtx);

        StudioVertex[] vertices = vvd.VerticesForLod(0);
        List<StudioTriangle> triangles = [];

        IReadOnlyList<(int Offset, VtxBodyPartHeader Header)> vtxParts = vtx.BodyParts();
        int partLimit = Math.Min(vtxParts.Count, mdl.BodyParts().Length);

        for (int partId = 0; partId < partLimit; partId++)
        {
            (int partOffset, VtxBodyPartHeader vtxPart) = vtxParts[partId];
            IReadOnlyList<(int Offset, VtxModelHeader Header)> vtxModels = vtx.Models(partOffset, vtxPart);
            IReadOnlyList<StudioModel> mdlModels = [.. mdl.Models(partId)];
            int modelLimit = Math.Min(vtxModels.Count, mdlModels.Count);
            if (bodies == StudioBodyChoice.Default)
            {
                modelLimit = Math.Min(modelLimit, 1);
            }

            for (int modelId = 0; modelId < modelLimit; modelId++)
            {
                int before = triangles.Count;
                (int modelOffset, VtxModelHeader vtxModel) = vtxModels[modelId];
                IReadOnlyList<(int Offset, VtxModelLodHeader Header)> lods = vtx.Lods(modelOffset, vtxModel);
                if (lods.Count == 0)
                {
                    continue;
                }

                (int lodOffset, VtxModelLodHeader vtxLod) = lods[0];
                IReadOnlyList<(int Offset, VtxMeshHeader Header)> vtxMeshes = vtx.Meshes(lodOffset, vtxLod);
                StudioModel mdlModel = mdlModels[modelId];
                ReadOnlySpan<StudioMesh> mdlMeshes = mdl.Meshes(partId, modelId);
                int meshLimit = Math.Min(vtxMeshes.Count, mdlMeshes.Length);

                for (int meshId = 0; meshId < meshLimit; meshId++)
                {
                    (int meshOffset, VtxMeshHeader vtxMesh) = vtxMeshes[meshId];
                    CollectMeshTriangles(vtx, vtxMesh, meshOffset, mdlModel, mdlMeshes[meshId], vertices, triangles);
                }

                if (triangles.Count > before)
                {
                    break;
                }
            }
        }

        return triangles;
    }

    /// <summary>
    /// One mesh's triangles: group vertices based into the VVD's block, then
    /// every strip unpacked.
    /// </summary>
    /// <remarks>
    /// The model's <c>VertexIndex</c> is a BYTE offset into the VVD's block
    /// (divide by the vertex size); the mesh's <c>VertexOffset</c> is an INDEX
    /// into the model's run. Confusing the two scales one of them by 48.
    /// </remarks>
    private static void CollectMeshTriangles(
        VtxFile vtx,
        in VtxMeshHeader mesh,
        int meshOffset,
        in StudioModel owner,
        in StudioMesh ownerMesh,
        StudioVertex[] vertices,
        List<StudioTriangle> into)
    {
        int vertexBase = (owner.VertexIndex / Unsafe.SizeOf<StudioVertex>()) + ownerMesh.VertexOffset;
        int slot = ownerMesh.Material;

        foreach ((int groupOffset, VtxStripGroupHeader group) in vtx.StripGroups(meshOffset, mesh))
        {
            ReadOnlySpan<VtxVertex> groupVertices = vtx.Vertices(groupOffset, group);
            ReadOnlySpan<ushort> groupIndices = vtx.Indices(groupOffset, group);

            foreach (VtxStripHeader strip in vtx.Strips(groupOffset, group))
            {
                bool isStrip = ((VtxStripFlags)strip.Flags & VtxStripFlags.IsTriStrip) != 0;
                int triangles = isStrip
                    ? strip.NumIndices - 2
                    : strip.NumIndices - (strip.NumIndices % 3);

                for (int i = 0; i < triangles; i++)
                {
                    int a = isStrip ? i : i * 3;
                    int b = a + 1;
                    int c = a + 2;
                    if (isStrip && (i & 1) != 0)
                    {
                        (b, c) = (c, b); // keep the winding continuous along the strip
                    }

                    AddTriangle(
                        vertexBase,
                        slot,
                        groupVertices,
                        groupIndices,
                        strip.IndexOffset + a,
                        strip.IndexOffset + b,
                        strip.IndexOffset + c,
                        vertices,
                        into);
                }
            }
        }
    }

    /// <summary>Resolves three strip indices to vertices and appends the triangle.</summary>
    private static void AddTriangle(
        int vertexBase,
        int slot,
        ReadOnlySpan<VtxVertex> groupVertices,
        ReadOnlySpan<ushort> groupIndices,
        int a,
        int b,
        int c,
        StudioVertex[] vertices,
        List<StudioTriangle> into)
    {
        if ((uint)a >= (uint)groupIndices.Length || (uint)b >= (uint)groupIndices.Length ||
            (uint)c >= (uint)groupIndices.Length)
        {
            return;
        }

        int ga = groupIndices[a], gb = groupIndices[b], gc = groupIndices[c];
        if (ga >= groupVertices.Length || gb >= groupVertices.Length || gc >= groupVertices.Length)
        {
            return;
        }

        int ia = vertexBase + groupVertices[ga].OrigMeshVertId;
        int ib = vertexBase + groupVertices[gb].OrigMeshVertId;
        int ic = vertexBase + groupVertices[gc].OrigMeshVertId;

        if ((uint)ia >= (uint)vertices.Length
            || (uint)ib >= (uint)vertices.Length
            || (uint)ic >= (uint)vertices.Length)
        {
            return;
        }

        into.Add(new StudioTriangle(
            vertices[ia].Position,
            vertices[ib].Position,
            vertices[ic].Position,
            vertices[ia].Normal,
            vertices[ib].Normal,
            vertices[ic].Normal,
            (vertices[ia].TexCoord[0], vertices[ia].TexCoord[1]),
            (vertices[ib].TexCoord[0], vertices[ib].TexCoord[1]),
            (vertices[ic].TexCoord[0], vertices[ic].TexCoord[1]),
            slot));
    }
}
