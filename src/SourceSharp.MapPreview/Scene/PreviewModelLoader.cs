//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Immutable;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapPreview.Scene;

/// <summary>Why a model did not load.</summary>
/// <param name="Model">The model path.</param>
/// <param name="Reason">What was wrong, in words.</param>
public readonly record struct ModelLoadIssue(string Model, string Reason);

/// <summary>
/// Loads the studio models a map places, for <see cref="BspSceneBuilder.Build"/>:
/// the IO half of building a scene.
/// </summary>
/// <remarks>
/// <para>
/// Each model needs its <c>.mdl</c>, <c>.vvd</c> and one <c>.vtx</c>; the VTX
/// flavours are tried <c>dx90</c> first (what the PC engine draws), then
/// <c>dx80</c>, plain and <c>sw</c>. Models are parsed engine-compatibly, so the
/// version 44 models Half-Life 2 ships load. Triangles are read with
/// <see cref="StudioBodyChoice.Default"/>: model zero of each body part, which
/// is what the engine shows for an entity with no body value set.
/// </para>
/// <para>
/// A model that fails is left out of the result and reported, never thrown:
/// the scene draws it as a stand-in box and the report lists it, which is more
/// use to an agent than a preview that refuses to render.
/// </para>
/// </remarks>
public static class PreviewModelLoader
{
    private static readonly ImmutableArray<string> VtxExtensions = ["dx90.vtx", "dx80.vtx", "vtx", "sw.vtx"];

    /// <summary>Loads models.</summary>
    /// <param name="names">Model paths, as <see cref="BspSceneBuilder.ModelNames"/> lists them.</param>
    /// <param name="content">The mounted game (with the map's pak in front).</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The loaded models keyed by <see cref="PreviewModel.Key"/>, and an issue per model that did not load.</returns>
    public static async Task<(IReadOnlyDictionary<string, PreviewModel> Models, IReadOnlyList<ModelLoadIssue> Issues)> LoadAsync(
        IEnumerable<string> names,
        IContentFileSystem content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(content);

        Dictionary<string, PreviewModel> models = [];
        List<ModelLoadIssue> issues = [];
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = PreviewModel.Key(name);
            if (models.ContainsKey(key))
            {
                continue;
            }

            (PreviewModel? model, string? problem) = await LoadOneAsync(key, content, cancellationToken).ConfigureAwait(false);
            if (model is not null)
            {
                models[key] = model;
            }
            else
            {
                issues.Add(new ModelLoadIssue(key, problem ?? "unknown"));
            }
        }

        return (models, issues);
    }

    private static async Task<(PreviewModel? Model, string? Problem)> LoadOneAsync(
        string key, IContentFileSystem content, CancellationToken cancellationToken)
    {
        if (!VPath.TryCreate(key, out VPath mdlPath))
        {
            return (null, "not a valid content path");
        }

        byte[]? mdlBytes = await ReadAsync(content, mdlPath, cancellationToken).ConfigureAwait(false);
        if (mdlBytes is null)
        {
            return (null, "the .mdl is not in the game content");
        }

        byte[]? vvdBytes = await ReadAsync(content, mdlPath.WithExtension("vvd"), cancellationToken).ConfigureAwait(false);
        if (vvdBytes is null)
        {
            return (null, "no .vvd beside the .mdl");
        }

        byte[]? vtxBytes = null;
        foreach (string extension in VtxExtensions)
        {
            vtxBytes = await ReadAsync(content, mdlPath.WithExtension(extension), cancellationToken).ConfigureAwait(false);
            if (vtxBytes is not null)
            {
                break;
            }
        }

        if (vtxBytes is null)
        {
            return (null, "no .vtx of any kind beside the .mdl");
        }

        MdlFile mdl;
        VvdFile vvd;
        VtxFile vtx;
        try
        {
            mdl = MdlFile.Parse(mdlBytes, MdlVersionPolicy.EngineCompatible);
            vvd = VvdFile.Parse(vvdBytes);
            vtx = VtxFile.Parse(vtxBytes);
        }
        catch (InvalidStudioException e)
        {
            return (null, $"not a readable model: {e.Message}");
        }

        IReadOnlyList<StudioTriangle> triangles = StudioMeshReader.Lod0Triangles(mdl, vvd, vtx, StudioBodyChoice.Default, restPose: true);
        triangles = StandUp(triangles, mdl);
        IReadOnlyList<IReadOnlyList<string>> skins = await SkinMaterialsAsync(mdl, content, cancellationToken).ConfigureAwait(false);
        return (new PreviewModel(key, triangles, skins, mdl.Header.HullMin, mdl.Header.HullMax), null);
    }

    /// <summary>
    /// Stands up a character whose mesh is stored Y-up.
    /// </summary>
    /// <remarks>
    /// Character models (TF2's players among them) keep their vertices in a
    /// Y-up frame and rely on every animation's root bone to turn them Z-up;
    /// the preview draws the mesh without decoding animations, so such a model
    /// would lie on its back. A model that is not a static prop, whose mesh is
    /// tall along Y and flat along Z while its hull is tall along Z, is turned
    /// +90 degrees about X, which is the turn those root bones make. Props are
    /// never touched.
    /// </remarks>
    internal static IReadOnlyList<StudioTriangle> StandUp(IReadOnlyList<StudioTriangle> triangles, MdlFile mdl)
    {
        const int StaticPropFlag = 0x10;
        if (triangles.Count == 0 || (mdl.Header.Flags & StaticPropFlag) != 0)
        {
            return triangles;
        }

        float minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (StudioTriangle t in triangles)
        {
            foreach (Vec3 p in (ReadOnlySpan<Vec3>)[t.A, t.B, t.C])
            {
                minY = MathF.Min(minY, p.Y);
                maxY = MathF.Max(maxY, p.Y);
                minZ = MathF.Min(minZ, p.Z);
                maxZ = MathF.Max(maxZ, p.Z);
            }
        }

        float meshY = maxY - minY, meshZ = maxZ - minZ;
        float hullZ = mdl.Header.HullMax.Z - mdl.Header.HullMin.Z;
        bool lying = meshY > 2f * meshZ && hullZ > 0 && MathF.Abs(meshY - hullZ) < 0.25f * hullZ;
        if (!lying)
        {
            return triangles;
        }

        static Vec3 Turn(Vec3 v) => new(v.X, -v.Z, v.Y);
        return [.. triangles.Select(t => t with
        {
            A = Turn(t.A),
            B = Turn(t.B),
            C = Turn(t.C),
            NormalA = Turn(t.NormalA),
            NormalB = Turn(t.NormalB),
            NormalC = Turn(t.NormalC),
        })];
    }

    /// <summary>
    /// The material each skin family gives each slot: the skin table maps a
    /// slot to a texture index, and the texture name resolves against the
    /// model's search paths in order, the first that the game has winning.
    /// A name no search path resolves keeps its first candidate, so the
    /// material library reports it as missing under a useful name.
    /// </summary>
    private static async Task<IReadOnlyList<IReadOnlyList<string>>> SkinMaterialsAsync(
        MdlFile mdl, IContentFileSystem content, CancellationToken cancellationToken)
    {
        int textureCount = mdl.Textures().Length;
        short[] table = mdl.SkinTable().ToArray();
        int refs = mdl.Header.NumSkinRef;
        int families = table.Length > 0 && refs > 0 ? table.Length / refs : 1;
        int slots = table.Length > 0 && refs > 0 ? refs : textureCount;
        IReadOnlyList<string> searchPaths = mdl.MaterialSearchPaths();
        Dictionary<int, string> resolved = [];

        List<IReadOnlyList<string>> result = [];
        for (int family = 0; family < Math.Max(1, families); family++)
        {
            string[] row = new string[slots];
            for (int slot = 0; slot < slots; slot++)
            {
                int texture = table.Length > 0 && refs > 0 ? table[(family * refs) + slot] : slot;
                if ((uint)texture >= (uint)textureCount)
                {
                    row[slot] = BspSceneBuilder.MissingSlotMaterial;
                    continue;
                }

                if (!resolved.TryGetValue(texture, out string? name))
                {
                    name = await ResolveAsync(mdl.TextureName(texture), searchPaths, content, cancellationToken).ConfigureAwait(false);
                    resolved[texture] = name;
                }

                row[slot] = name;
            }

            result.Add(row);
        }

        return result;
    }

    private static async Task<string> ResolveAsync(
        string textureName, IReadOnlyList<string> searchPaths, IContentFileSystem content, CancellationToken cancellationToken)
    {
        string? first = null;
        foreach (string searchPath in searchPaths)
        {
            string candidate = MaterialFactsReader.Normalize(searchPath.TrimEnd('/', '\\') + "/" + textureName);
            first ??= candidate;
            if (await content.ResolveAsync(MaterialFactsReader.ContentPath(candidate), cancellationToken).ConfigureAwait(false) is not null)
            {
                return candidate;
            }
        }

        string direct = MaterialFactsReader.Normalize(textureName);
        if (await content.ResolveAsync(MaterialFactsReader.ContentPath(direct), cancellationToken).ConfigureAwait(false) is not null)
        {
            return direct;
        }

        return first ?? direct;
    }

    private static async Task<byte[]?> ReadAsync(IContentFileSystem content, VPath path, CancellationToken cancellationToken)
    {
        using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return owner?.Memory.ToArray();
    }
}
