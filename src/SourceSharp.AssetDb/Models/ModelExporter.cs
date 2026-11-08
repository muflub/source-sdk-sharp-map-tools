//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Imaging;
using SourceSharp.AssetDb.Materials;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb.Models;

/// <summary>
/// Builds the model half of the database: for every selected <c>.mdl</c>, a
/// <see cref="ModelRecord"/> and a directory of rendered views under
/// <c>models/</c>.
/// </summary>
/// <remarks>
/// <para>
/// A model needs its three files. A missing <c>.vvd</c> or <c>.vtx</c>, or a
/// file that does not parse, becomes an <see cref="ImportIssue"/> and the walk
/// continues: game trees hold console-only and stripped models, and one
/// absent satellite must not cost the rest of the database.
/// </para>
/// <para>
/// The metadata is always read afresh, since it is cheap and the materials a
/// model resolves to depend on the game importing it. Only the render is
/// skipped for a model the database already holds with the same hash and
/// whose views are still on disk.
/// </para>
/// </remarks>
public static class ModelExporter
{
    /// <summary>The directory, under the database root, that model views go in.</summary>
    public const string ImageDirectoryName = "models";

    /// <summary>The file name of the sheet of all six views.</summary>
    public const string SheetFileName = "sheet.png";

    /// <summary>
    /// The VTX files tried, best first. <c>dx90</c> is what the PC engine
    /// draws; older and stripped installs carry only the <c>dx80</c> or
    /// unsuffixed file, which hold the same triangles in a different strip
    /// order, which is all a render needs.
    /// </summary>
    private static readonly string[] VtxExtensions = ["dx90.vtx", "dx80.vtx", "vtx", "sw.vtx"];

    /// <summary>Whether a model is a prop, for <see cref="ModelSelection.Props"/>.</summary>
    /// <param name="path">The model's lower-case content path.</param>
    /// <param name="flags">Its header flags.</param>
    /// <returns>True for a static prop or anything under a <c>props*</c> directory.</returns>
    public static bool IsProp(string path, int flags)
    {
        ArgumentNullException.ThrowIfNull(path);
        if ((flags & ModelRecord.StaticPropFlag) != 0)
        {
            return true;
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.StartsWith("props", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The views' directory for a model path, before any collision suffix.</summary>
    /// <param name="path">The model's lower-case content path.</param>
    /// <returns><c>models/&lt;path without models/ and .mdl&gt;</c>.</returns>
    public static string NaturalImageDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string stem = path.StartsWith("models/", StringComparison.Ordinal) ? path["models/".Length..] : path;
        if (stem.EndsWith(".mdl", StringComparison.Ordinal))
        {
            stem = stem[..^4];
        }

        return $"{ImageDirectoryName}/{stem}";
    }

    /// <summary>Indexes the models a database holds for <see cref="ExportAsync"/>, keyed by view directory.</summary>
    /// <param name="models">The stored models.</param>
    /// <returns>The index.</returns>
    public static AssetIndex<KnownModel> Index(IEnumerable<KnownModel> models) =>
        new(models, m => m.Path, m => m.Sha256, m => m.ImageDirectory);

    /// <summary>Runs the export.</summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="output">The database root, where the views are written.</param>
    /// <param name="known">The models the database already holds.</param>
    /// <param name="materials">This game's materials by name, for skins and alpha testing.</param>
    /// <param name="options">Selection, sizes, parallelism, progress.</param>
    /// <param name="cancellationToken">Cancels the export; views already written stay.</param>
    /// <returns>A record per model exported, and the issues, each sorted by path.</returns>
    public static async Task<(IReadOnlyList<ModelRecord> Models, IReadOnlyList<ImportIssue> Issues)> ExportAsync(
        IContentFileSystem content,
        IFileSystem output,
        AssetIndex<KnownModel> known,
        IReadOnlyDictionary<string, MaterialRecord> materials,
        AssetExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Models == ModelSelection.None)
        {
            return ([], []);
        }

        List<VPath> paths = await ContentWalk.ListAsync(content, "models", "*.mdl", cancellationToken)
            .ConfigureAwait(false);

        ConcurrentBag<ModelRecord> records = [];
        ConcurrentBag<ImportIssue> issues = [];
        TextureCache textures = new(content, options.ModelTextureSize);
        int done = 0;

        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = cancellationToken,
            },
            async (path, token) =>
            {
                try
                {
                    ModelRecord? record = await ExportOneAsync(
                        content, output, known, materials, textures, options, path, issues, token).ConfigureAwait(false);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    issues.Add(new ImportIssue("model", path.Value, e.Message));
                }

                options.Progress?.Report(new ImportProgress("models", Interlocked.Increment(ref done), paths.Count));
            }).ConfigureAwait(false);

        return (
            [.. records.OrderBy(r => r.Path, StringComparer.Ordinal)],
            [.. issues.OrderBy(i => i.Path, StringComparer.Ordinal)]);
    }

    private static async Task<ModelRecord?> ExportOneAsync(
        IContentFileSystem content,
        IFileSystem output,
        AssetIndex<KnownModel> known,
        IReadOnlyDictionary<string, MaterialRecord> materials,
        TextureCache textures,
        AssetExportOptions options,
        VPath source,
        ConcurrentBag<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        string path = source.Value.Replace('\\', '/').ToLowerInvariant();
        byte[]? mdlBytes = await ContentWalk.ReadAsync(content, source, cancellationToken).ConfigureAwait(false);
        if (mdlBytes is null)
        {
            issues.Add(new ImportIssue("model", source.Value, "listed by the mount but not readable"));
            return null;
        }

        MdlFile mdl;
        try
        {
            // Engine-compatible: Half-Life 2 ships version 44 models, which
            // the engine draws and the compilers' exact rule would refuse.
            mdl = MdlFile.Parse(mdlBytes, MdlVersionPolicy.EngineCompatible);
        }
        catch (InvalidStudioException e)
        {
            issues.Add(new ImportIssue("model", source.Value, $"not a readable model: {e.Message}"));
            return null;
        }

        if (options.Models == ModelSelection.Props && !IsProp(path, mdl.Header.Flags))
        {
            return null;
        }

        VPath vvdPath = source.WithExtension("vvd");
        byte[]? vvdBytes = await ContentWalk.ReadAsync(content, vvdPath, cancellationToken).ConfigureAwait(false);
        if (vvdBytes is null)
        {
            issues.Add(new ImportIssue("model", source.Value, $"no {vvdPath.Value} beside the .mdl"));
            return null;
        }

        byte[]? vtxBytes = null;
        foreach (string extension in VtxExtensions)
        {
            vtxBytes = await ContentWalk.ReadAsync(content, source.WithExtension(extension), cancellationToken)
                .ConfigureAwait(false);
            if (vtxBytes is not null)
            {
                break;
            }
        }

        if (vtxBytes is null)
        {
            issues.Add(new ImportIssue("model", source.Value, "no .vtx of any kind beside the .mdl"));
            return null;
        }

        VvdFile vvd;
        VtxFile vtx;
        try
        {
            vvd = VvdFile.Parse(vvdBytes);
            vtx = VtxFile.Parse(vtxBytes);
        }
        catch (InvalidStudioException e)
        {
            issues.Add(new ImportIssue("model", source.Value, $"not a readable model: {e.Message}"));
            return null;
        }

        IReadOnlyList<ModelTriangle> triangles = ModelRenderer.CollectTriangles(mdl, vvd, vtx);
        if (triangles.Count == 0)
        {
            issues.Add(new ImportIssue("model", source.Value, "no level-of-detail-zero triangles to draw"));
            return null;
        }

        string sha = HashFiles(mdlBytes, vvdBytes, vtxBytes);
        IReadOnlyList<ModelMaterial> skins = SkinMaterials(mdl, materials);
        KnownModel? existing = known.Find(path, sha);
        string directory = existing?.ImageDirectory ?? known.ImageLocation(NaturalImageDirectory(path), sha);
        (Vec3 Min, Vec3 Max) mesh = ModelRenderer.Bounds(triangles);

        ModelRecord record = new()
        {
            Path = path,
            Sha256 = sha,
            Name = mdl.Name,
            Flags = mdl.Header.Flags,
            SurfaceProp = mdl.SurfaceProp,
            Mass = mdl.Header.Mass,
            Hull = mdl.HullBounds,
            View = mdl.ViewBounds,
            Mesh = mesh,
            Bones = mdl.Header.NumBones,
            Sequences = mdl.Header.NumLocalSeq,
            BodyParts = mdl.Header.NumBodyParts,
            Lods = vtx.Header.NumLods,
            Triangles = triangles.Count,
            Vertices = vvd.VertexCount,
            Skins = Math.Max(1, mdl.Header.NumSkinFamilies),
            HasCollision = await content.ResolveAsync(source.WithExtension("phy"), cancellationToken)
                .ConfigureAwait(false) is not null,
            PropData = await PropDataBaseAsync(mdl, cancellationToken).ConfigureAwait(false),
            KeyValues = mdl.KeyValueText(),
            GridStep = ModelRenderer.GridStep(mesh),
            ImageDirectory = directory,
            Materials = skins,
            Source = (await content.ResolveAsync(source, cancellationToken).ConfigureAwait(false))?.Mount ?? string.Empty,
        };

        if (existing is not null && !options.Force &&
            await output.ExistsAsync(VPath.Create(record.SheetImage), cancellationToken).ConfigureAwait(false))
        {
            return record;
        }

        IReadOnlyList<RenderMaterial?> slots = await SlotMaterialsAsync(mdl, skins, materials, textures, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<ModelView, RgbaImage> views = ModelRenderer.RenderAll(triangles, slots, options.ViewSize);

        foreach ((ModelView view, RgbaImage image) in views)
        {
            await WritePngAsync(output, record.ImageFor(view), image, cancellationToken).ConfigureAwait(false);
        }

        // The sheet last: its presence is what marks the set complete for the
        // reuse check above, so a cancelled render is redone, not trusted.
        await WritePngAsync(output, record.SheetImage, ModelRenderer.Sheet(views), cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Every skin family's slots, each with the material it resolves to. A
    /// model without a skin table has one family whose slot <c>i</c> shows
    /// texture <c>i</c>.
    /// </summary>
    /// <param name="mdl">The model.</param>
    /// <param name="materials">The game's materials by name.</param>
    /// <returns>The rows, family by family, slot by slot.</returns>
    public static IReadOnlyList<ModelMaterial> SkinMaterials(
        MdlFile mdl,
        IReadOnlyDictionary<string, MaterialRecord> materials)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ArgumentNullException.ThrowIfNull(materials);

        int textureCount = mdl.Textures().Length;
        ReadOnlySpan<short> table = mdl.SkinTable();
        int refs = mdl.Header.NumSkinRef;
        int families = table.Length > 0 && refs > 0 ? table.Length / refs : 1;
        int slots = table.Length > 0 && refs > 0 ? refs : textureCount;
        IReadOnlyList<string> searchPaths = mdl.MaterialSearchPaths();

        List<ModelMaterial> rows = [];
        for (int family = 0; family < families; family++)
        {
            for (int slot = 0; slot < slots; slot++)
            {
                int texture = table.Length > 0 && refs > 0 ? table[(family * refs) + slot] : slot;
                if ((uint)texture >= (uint)textureCount)
                {
                    continue;
                }

                string name = mdl.TextureName(texture);
                rows.Add(new ModelMaterial(family, slot, name, ResolveMaterial(name, searchPaths, materials)));
            }
        }

        return rows;
    }

    /// <summary>
    /// The material a model's texture name resolves to: the first search path,
    /// in file order, under which the game has a material by that name.
    /// </summary>
    /// <param name="textureName">The name the model lists.</param>
    /// <param name="searchPaths">The model's material search paths.</param>
    /// <param name="materials">The game's materials by name.</param>
    /// <returns>The material's name, or null when no search path has it.</returns>
    public static string? ResolveMaterial(
        string textureName,
        IReadOnlyList<string> searchPaths,
        IReadOnlyDictionary<string, MaterialRecord> materials)
    {
        ArgumentNullException.ThrowIfNull(textureName);
        ArgumentNullException.ThrowIfNull(searchPaths);
        ArgumentNullException.ThrowIfNull(materials);

        foreach (string searchPath in searchPaths)
        {
            string candidate = MaterialReader.MaterialName(searchPath.TrimEnd('/', '\\') + "/" + textureName);
            if (materials.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        // A texture name with no search paths, or an absolute one, names the
        // material directly.
        string direct = MaterialReader.MaterialName(textureName);
        return materials.ContainsKey(direct) ? direct : null;
    }

    /// <summary>
    /// The <c>base</c> of the model's <c>prop_data</c> block, which says what
    /// it is made of and how it breaks; null when the model has none.
    /// </summary>
    private static async Task<string?> PropDataBaseAsync(MdlFile mdl, CancellationToken cancellationToken)
    {
        string text = mdl.KeyValueText();
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            KeyValuesDocument document = await KeyValuesDocument.ParseAsync(text, null, cancellationToken)
                .ConfigureAwait(false);
            foreach (KeyValuesNode root in document.Roots)
            {
                KeyValuesNode? propData = root.Name.Equals("prop_data", StringComparison.OrdinalIgnoreCase)
                    ? root
                    : root.Find("prop_data");
                if (propData?.GetString("base") is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }
        catch (ChunkFileException)
        {
            // A model's keyvalues are author-written text; a malformed block
            // leaves PropData unknown and KeyValues still carries the text.
        }

        return null;
    }

    /// <summary>The default skin's material per slot, with its texture decoded.</summary>
    private static async Task<IReadOnlyList<RenderMaterial?>> SlotMaterialsAsync(
        MdlFile mdl,
        IReadOnlyList<ModelMaterial> skins,
        IReadOnlyDictionary<string, MaterialRecord> materials,
        TextureCache textures,
        CancellationToken cancellationToken)
    {
        int slots = Math.Max(mdl.Header.NumSkinRef, mdl.Textures().Length);
        RenderMaterial?[] result = new RenderMaterial?[slots];
        foreach (ModelMaterial row in skins)
        {
            if (row.Skin != 0 || (uint)row.Slot >= (uint)slots || row.Material is null ||
                !materials.TryGetValue(row.Material, out MaterialRecord? material))
            {
                continue;
            }

            RgbaImage? texture = material.BaseTexture is null
                ? null
                : await textures.GetAsync(material.BaseTexture, cancellationToken).ConfigureAwait(false);
            result[row.Slot] = new RenderMaterial(texture, material.AlphaTest || material.Translucent);
        }

        return result;
    }

    private static string HashFiles(byte[] mdl, byte[] vvd, byte[] vtx)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(mdl);
        hash.AppendData(vvd);
        hash.AppendData(vtx);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task WritePngAsync(IFileSystem output, string path, RgbaImage image, CancellationToken cancellationToken)
    {
        byte[] png = image.ToPng();
        await output.ReplaceAsync(
            VPath.Create(path),
            async (stream, token) => await stream.WriteAsync(png, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The decoded textures model renders are skinned with, shared across the
    /// run's workers so a material used by a hundred props decodes once.
    /// </summary>
    /// <remarks>
    /// Bounded, because a long-lived host must not grow without limit: past
    /// <see cref="Capacity"/> entries the cache is emptied and refills. The
    /// textures are small (the <see cref="AssetExportOptions.ModelTextureSize"/>
    /// mip), so the bound is a few hundred megabytes at most, and the cache
    /// lives only as long as one export.
    /// </remarks>
    private sealed class TextureCache(IContentFileSystem content, int maxEdge)
    {
        private const int Capacity = 2048;
        private readonly ConcurrentDictionary<string, Lazy<Task<RgbaImage?>>> _entries = new(StringComparer.Ordinal);

        public Task<RgbaImage?> GetAsync(string texture, CancellationToken cancellationToken)
        {
            if (_entries.Count > Capacity)
            {
                _entries.Clear();
            }

            // The load runs without the caller's token: a shared entry must
            // not be poisoned by the first caller's cancellation.
            return _entries.GetOrAdd(texture, key => new Lazy<Task<RgbaImage?>>(() => LoadAsync(key))).Value
                .WaitAsync(cancellationToken);
        }

        private async Task<RgbaImage?> LoadAsync(string texture)
        {
            try
            {
                byte[]? bytes = await ContentWalk.ReadAsync(
                    content, VPath.Create($"materials/{texture}.vtf"), CancellationToken.None).ConfigureAwait(false);
                if (bytes is null)
                {
                    return null;
                }

                VtfFile vtf = VtfFile.Parse(bytes);
                if (!ImageDecoder.CanDecode(vtf.Format))
                {
                    return null;
                }

                return Textures.TextureExporter.DecodeLargestPresent(vtf, MipChoice.For(vtf, maxEdge), out _);
            }
            catch (Exception e) when (e is InvalidVtfException or NotSupportedException or InvalidDataException
                                          or ArgumentException)
            {
                // An unreadable skin draws flat grey; the texture pass records
                // the file's own issue.
                return null;
            }
        }
    }
}
