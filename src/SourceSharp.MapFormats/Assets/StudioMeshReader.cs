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
    /// <param name="restPose">
    /// Whether to skin vertices into the skeleton's rest pose (see
    /// <see cref="RestPoseSkin"/>); false returns them as the VVD stores them.
    /// </param>
    /// <returns>One entry per drawn triangle, in file order.</returns>
    public static IReadOnlyList<StudioTriangle> Lod0Triangles(
        MdlFile mdl,
        VvdFile vvd,
        VtxFile vtx,
        StudioBodyChoice bodies = StudioBodyChoice.FirstDrawn,
        bool restPose = false)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ArgumentNullException.ThrowIfNull(vvd);
        ArgumentNullException.ThrowIfNull(vtx);

        StudioVertex[] vertices = vvd.VerticesForLod(0);
        if (restPose)
        {
            vertices = Skin(vertices, RestPoseSkin(mdl));
        }

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
    /// The matrices that carry a bind-pose vertex into the skeleton's rest
    /// pose, one per bone: the bone's rest transform in model space (its
    /// position and quaternion, composed down from the root) times its
    /// pose-to-bone matrix.
    /// </summary>
    /// <param name="mdl">The model.</param>
    /// <returns>Row-major 3x4 matrices, twelve floats per bone.</returns>
    /// <remarks>
    /// For a prop compiled in place the product is the identity and nothing
    /// moves. For a character it is what stands the mesh up: the vertices of
    /// a player model are stored in the bind pose's own frame, which can lie
    /// on its side, and the engine always draws through the skeleton. Drawing
    /// the rest pose rather than an animation frame keeps the reader free of
    /// sequence decoding while still showing the model upright and whole.
    /// </remarks>
    public static float[] RestPoseSkin(MdlFile mdl)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ReadOnlySpan<StudioBone> bones = mdl.Bones();
        float[] toModel = new float[bones.Length * 12];
        float[] skin = new float[bones.Length * 12];
        Span<float> local = stackalloc float[12];
        for (int b = 0; b < bones.Length; b++)
        {
            StudioBone bone = bones[b];
            QuaternionMatrix(bone.Quat[0], bone.Quat[1], bone.Quat[2], bone.Quat[3], bone.Pos, local);
            Span<float> target = toModel.AsSpan(b * 12, 12);
            if (bone.Parent >= 0 && bone.Parent < b)
            {
                Concat(toModel.AsSpan(bone.Parent * 12, 12), local, target);
            }
            else
            {
                local.CopyTo(target);
            }

            Span<float> poseToBone = stackalloc float[12];
            for (int i = 0; i < 12; i++)
            {
                poseToBone[i] = bone.PoseToBone[i];
            }

            Concat(target, poseToBone, skin.AsSpan(b * 12, 12));
        }

        return skin;
    }

    private static StudioVertex[] Skin(StudioVertex[] vertices, float[] skin)
    {
        int bones = skin.Length / 12;
        StudioVertex[] result = new StudioVertex[vertices.Length];
        for (int v = 0; v < vertices.Length; v++)
        {
            StudioVertex vertex = vertices[v];
            float px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0, total = 0;
            int count = Math.Clamp((int)vertex.BoneWeights.NumBones, 0, StudioIdents.MaxBonesPerVert);
            for (int i = 0; i < count; i++)
            {
                int bone = vertex.BoneWeights.Bone[i];
                float w = vertex.BoneWeights.Weight[i];
                if (bone >= bones || w == 0)
                {
                    continue;
                }

                ReadOnlySpan<float> m = skin.AsSpan(bone * 12, 12);
                Vec3 p = vertex.Position, n = vertex.Normal;
                px += w * ((m[0] * p.X) + (m[1] * p.Y) + (m[2] * p.Z) + m[3]);
                py += w * ((m[4] * p.X) + (m[5] * p.Y) + (m[6] * p.Z) + m[7]);
                pz += w * ((m[8] * p.X) + (m[9] * p.Y) + (m[10] * p.Z) + m[11]);
                nx += w * ((m[0] * n.X) + (m[1] * n.Y) + (m[2] * n.Z));
                ny += w * ((m[4] * n.X) + (m[5] * n.Y) + (m[6] * n.Z));
                nz += w * ((m[8] * n.X) + (m[9] * n.Y) + (m[10] * n.Z));
                total += w;
            }

            if (total > 0)
            {
                float inv = 1f / total;
                vertex.Position = new Vec3(px * inv, py * inv, pz * inv);
                vertex.Normal = new Vec3(nx * inv, ny * inv, nz * inv);
            }

            result[v] = vertex;
        }

        return result;
    }

    /// <summary>A unit quaternion (x, y, z, w) and a translation as a row-major 3x4 matrix.</summary>
    internal static void QuaternionMatrix(float x, float y, float z, float w, Vec3 t, Span<float> m)
    {
        m[0] = 1f - (2f * y * y) - (2f * z * z);
        m[4] = (2f * x * y) + (2f * w * z);
        m[8] = (2f * x * z) - (2f * w * y);
        m[1] = (2f * x * y) - (2f * w * z);
        m[5] = 1f - (2f * x * x) - (2f * z * z);
        m[9] = (2f * y * z) + (2f * w * x);
        m[2] = (2f * x * z) + (2f * w * y);
        m[6] = (2f * y * z) - (2f * w * x);
        m[10] = 1f - (2f * x * x) - (2f * y * y);
        m[3] = t.X;
        m[7] = t.Y;
        m[11] = t.Z;
    }

    /// <summary>Composes two row-major 3x4 affine matrices: <paramref name="result"/> = a * b.</summary>
    internal static void Concat(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result)
    {
        Span<float> r = stackalloc float[12];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float sum = (a[(row * 4) + 0] * b[col]) + (a[(row * 4) + 1] * b[4 + col]) + (a[(row * 4) + 2] * b[8 + col]);
                r[(row * 4) + col] = col == 3 ? sum + a[(row * 4) + 3] : sum;
            }
        }

        r.CopyTo(result);
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
