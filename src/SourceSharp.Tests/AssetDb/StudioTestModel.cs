//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// Builds a consistent synthetic <c>.mdl</c>/<c>.vvd</c>/<c>.vtx</c> triple
/// around one caller-chosen mesh, so the renderer and exporter facts run
/// against geometry whose expected image is knowable without a fixture.
/// </summary>
/// <remarks>
/// One body part, one model, one mesh, one strip group, one triangle-list
/// strip: the smallest hierarchy the VTX walk accepts, with every vertex
/// shared. The MDL carries one texture (or the list given), one search path,
/// an optional skin table and optional keyvalue text, mirroring the layout the
/// studio facts pin.
/// </remarks>
internal static class StudioTestModel
{
    /// <summary>The checksum shared by all three files, as the loader binds them.</summary>
    public const int Checksum = 0x1234ABCD;

    /// <summary>
    /// Builds the triple. The mesh is whatever triangles <paramref name="indices"/>
    /// describes over <paramref name="vertices"/>, taken three at a time.
    /// </summary>
    public static (byte[] Mdl, byte[] Vvd, byte[] Vtx) Build(
        Vec3[] vertices,
        int[] indices,
        string name = "gadget.mdl",
        string textureName = "models/gadget/body",
        string searchPath = "models/gadgets/",
        int skinFamilies = 0,
        int skinRefs = 0,
        (float U, float V)[]? uvs = null,
        int flags = 256,
        string[]? textureNames = null,
        string? keyValues = null,
        int version = StudioIdents.MdlVersion)
    {
        return (
            Mdl(name, textureNames ?? [textureName], searchPath, skinFamilies, skinRefs, flags, keyValues, version),
            Vvd(vertices, uvs),
            Vtx(vertices.Length, indices));
    }

    private static byte[] Mdl(
        string name,
        string[] textureNames,
        string searchPath,
        int skinFamilies,
        int skinRefs,
        int flags,
        string? keyValues,
        int version)
    {
        int headerSize = Unsafe.SizeOf<StudioHeader>();
        int partSize = Unsafe.SizeOf<StudioBodyParts>();
        int modelSize = Unsafe.SizeOf<StudioModel>();
        int meshSize = Unsafe.SizeOf<StudioMesh>();
        int textureSize = Unsafe.SizeOf<StudioTexture>();

        int partAt = headerSize;
        int modelAt = partAt + partSize;
        int meshAt = modelAt + modelSize;
        int textureAt = meshAt + meshSize;
        int cdTableAt = textureAt + (textureSize * textureNames.Length);
        int skinAt = cdTableAt + 4;
        int stringsAt = skinAt + (skinFamilies * skinRefs * sizeof(short));

        List<byte> strings = [];
        int[] textureNameAt = new int[textureNames.Length];
        for (int i = 0; i < textureNames.Length; i++)
        {
            textureNameAt[i] = stringsAt + strings.Count;
            strings.AddRange(Encoding.Latin1.GetBytes(textureNames[i] + "\0"));
        }

        int cdAt = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes(searchPath + "\0"));
        int surfacePropAt = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes("metal\0"));
        int keyValuesAt = stringsAt + strings.Count;
        byte[] keyValueBytes = keyValues is null ? [] : Encoding.Latin1.GetBytes(keyValues + "\0");
        strings.AddRange(keyValueBytes);

        StudioHeader header = new()
        {
            Id = StudioIdents.Mdl,
            Version = version,
            Checksum = Checksum,
            HullMin = new Vec3(-64, -64, 0),
            HullMax = new Vec3(64, 64, 64),
            ViewBbMin = new Vec3(-64, -64, 0),
            ViewBbMax = new Vec3(64, 64, 64),

            // A prop flag bit by default, so the index round-trips something non-zero.
            Flags = flags,

            NumBones = 1,
            NumBodyParts = 1,
            BodyPartIndex = partAt,
            NumTextures = textureNames.Length,
            TextureIndex = textureAt,
            NumCdTextures = 1,
            CdTextureIndex = cdTableAt,
            NumSkinFamilies = skinFamilies,
            NumSkinRef = skinRefs,
            SkinIndex = skinFamilies > 0 ? skinAt : 0,
            SurfacePropIndex = surfacePropAt,
            KeyValueIndex = keyValues is null ? 0 : keyValuesAt,
            KeyValueSize = keyValueBytes.Length,
            Mass = 25f,

            // Everything after the strings block, so the length is honest.
            Length = stringsAt + strings.Count,
        };
        Encoding.Latin1.GetBytes(name).CopyTo((Span<byte>)header.Name);

        StudioBodyParts part = new() { NumModels = 1, ModelIndex = modelAt - partAt };
        StudioModel model = new() { NumMeshes = 1, MeshIndex = meshAt - modelAt };
        StudioMesh mesh = new() { ModelIndex = modelAt - meshAt };

        List<byte> bytes = [];
        Put(bytes, in header);
        Put(bytes, in part);
        Put(bytes, in model);
        Put(bytes, in mesh);
        for (int i = 0; i < textureNames.Length; i++)
        {
            int here = textureAt + (i * textureSize);
            StudioTexture texture = new() { NameIndex = textureNameAt[i] - here };
            Put(bytes, in texture);
        }

        bytes.AddRange(BitConverter.GetBytes(cdAt));
        for (int family = 0; family < skinFamilies; family++)
        {
            for (int reference = 0; reference < skinRefs; reference++)
            {
                bytes.AddRange(BitConverter.GetBytes((short)((family + reference) % skinRefs)));
            }
        }

        bytes.AddRange(strings);
        return [.. bytes];
    }

    private static byte[] Vvd(Vec3[] vertices, (float U, float V)[]? uvs)
    {
        VertexFileHeader header = new()
        {
            Id = StudioIdents.Vvd,
            Version = StudioIdents.VvdVersion,
            Checksum = Checksum,
            NumLods = 1,
            VertexDataStart = Unsafe.SizeOf<VertexFileHeader>(),
        };
        header.NumLodVertexes[0] = vertices.Length;
        header.TangentDataStart = header.VertexDataStart + (vertices.Length * Unsafe.SizeOf<StudioVertex>());

        List<byte> bytes = [];
        Put(bytes, in header);
        for (int i = 0; i < vertices.Length; i++)
        {
            StudioVertex vertex = new() { Position = vertices[i] };
            if (uvs is not null)
            {
                vertex.TexCoord[0] = uvs[i].U;
                vertex.TexCoord[1] = uvs[i].V;
            }

            vertex.BoneWeights.NumBones = 1;
            vertex.BoneWeights.Weight[0] = 1.0f;
            Put(bytes, in vertex);
        }

        return [.. bytes];
    }

    private static byte[] Vtx(int vertexCount, int[] indices)
    {
        int headerSize = Unsafe.SizeOf<VtxFileHeader>();
        int partSize = Unsafe.SizeOf<VtxBodyPartHeader>();
        int modelSize = Unsafe.SizeOf<VtxModelHeader>();
        int lodSize = Unsafe.SizeOf<VtxModelLodHeader>();
        int meshSize = Unsafe.SizeOf<VtxMeshHeader>();
        int groupSize = Unsafe.SizeOf<VtxStripGroupHeader>();
        int stripSize = Unsafe.SizeOf<VtxStripHeader>();
        int vertexSize = Unsafe.SizeOf<VtxVertex>();

        int partAt = headerSize;
        int modelAt = partAt + partSize;
        int lodAt = modelAt + modelSize;
        int meshAt = lodAt + lodSize;
        int groupAt = meshAt + meshSize;
        int stripAt = groupAt + groupSize;
        int groupVertexAt = stripAt + stripSize;
        int indexAt = groupVertexAt + (vertexCount * vertexSize);

        VtxFileHeader header = new()
        {
            Version = StudioIdents.VtxVersion,
            CheckSum = Checksum,
            NumLods = 1,
            MaxBonesPerVert = StudioIdents.MaxBonesPerVert,
            NumBodyParts = 1,
            BodyPartOffset = partAt,
        };

        List<byte> bytes = [];
        Put(bytes, in header);
        Put(bytes, new VtxBodyPartHeader { NumModels = 1, ModelOffset = modelAt - partAt });
        Put(bytes, new VtxModelHeader { NumLods = 1, LodOffset = lodAt - modelAt });
        Put(bytes, new VtxModelLodHeader { NumMeshes = 1, MeshOffset = meshAt - lodAt });
        Put(bytes, new VtxMeshHeader { NumStripGroups = 1, StripGroupHeaderOffset = groupAt - meshAt });
        Put(bytes, new VtxStripGroupHeader
        {
            NumVerts = vertexCount,
            VertOffset = groupVertexAt - groupAt,
            NumIndices = indices.Length,
            IndexOffset = indexAt - groupAt,
            NumStrips = 1,
            StripOffset = stripAt - groupAt,
        });
        Put(bytes, new VtxStripHeader
        {
            NumIndices = indices.Length,
            NumVerts = vertexCount,
            Flags = (byte)VtxStripFlags.IsTriList,
        });

        for (int i = 0; i < vertexCount; i++)
        {
            VtxVertex vertex = new() { NumBones = 1, OrigMeshVertId = (ushort)i };
            Put(bytes, in vertex);
        }

        foreach (int index in indices)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)index));
        }

        return [.. bytes];
    }

    private static void Put<T>(List<byte> into, in T value)
        where T : unmanaged
    {
        byte[] bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        into.AddRange(bytes);
    }
}
