//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb.Materials;

/// <summary>
/// Reads every material a game can see into <see cref="MaterialRecord"/>s.
/// </summary>
/// <remarks>
/// <para>
/// A patch material is resolved through its include before anything is read
/// from it, the way the engine does, so a patch reports the shader and base
/// texture it actually draws with; its own <c>include</c> is kept so the
/// relationship is still visible.
/// </para>
/// <para>
/// No single material can end the run. A file that does not parse, or a
/// patch whose include is missing, becomes an <see cref="ImportIssue"/> and
/// the walk carries on: a game's tree always holds a few, and a database
/// missing them is still the database.
/// </para>
/// </remarks>
public static class MaterialReader
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        // Material values are paths and numbers; escaping '+' or non-ASCII
        // would make the column harder to read and search without making it
        // any safer, since nothing renders it as HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The brush shaders, lower case, without any <c>sdk_</c> prefix.</summary>
    private static readonly FrozenSet<string> BrushShaders = new[]
    {
        "lightmappedgeneric",
        "lightmappedreflective",
        "lightmapped_4wayblend",
        "worldvertextransition",
        "worldtwotextureblend",
        "worldgeneric",
        "unlittwotexture",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The model shaders, lower case, without any <c>sdk_</c> prefix.</summary>
    private static readonly FrozenSet<string> ModelShaders = new[]
    {
        "vertexlitgeneric",
        "eyes",
        "eyerefract",
        "teeth",
        "eyeball",
        "character",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Reads every <c>.vmt</c> under <c>materials/</c>.</summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="maxParallelism">How many files to parse at once.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The records and the issues, each sorted by path.</returns>
    public static async Task<(IReadOnlyList<MaterialRecord> Materials, IReadOnlyList<ImportIssue> Issues)> ReadAllAsync(
        IContentFileSystem content,
        int maxParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<VPath> paths = await ContentWalk.ListAsync(content, "materials", "*.vmt", cancellationToken)
            .ConfigureAwait(false);

        ConcurrentBag<MaterialRecord> materials = [];
        ConcurrentBag<ImportIssue> issues = [];

        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxParallelism),
                CancellationToken = cancellationToken,
            },
            async (path, token) =>
            {
                try
                {
                    MaterialRecord? record = await ReadOneAsync(content, path, token).ConfigureAwait(false);
                    if (record is not null)
                    {
                        materials.Add(record);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    issues.Add(new ImportIssue("material", path.Value, e.Message));
                }
            }).ConfigureAwait(false);

        return (
            [.. materials.OrderBy(m => m.Path, StringComparer.Ordinal)],
            [.. issues.OrderBy(i => i.Path, StringComparer.Ordinal)]);
    }

    /// <summary>Reads one material.</summary>
    /// <param name="content">The mounted game, for the file and any patch include.</param>
    /// <param name="path">The content path of the <c>.vmt</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or null when the mount no longer has the file.</returns>
    /// <exception cref="ChunkFileException">The file does not parse.</exception>
    /// <exception cref="VmtPatchException">A patch's include cannot be loaded.</exception>
    public static async Task<MaterialRecord?> ReadOneAsync(
        IContentFileSystem content,
        VPath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        ContentSource? source = await content.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? bytes = await ContentWalk.ReadAsync(content, path, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        VmtDocument document = await VmtDocument.ParseAsync(bytes, cancellationToken).ConfigureAwait(false);
        string? include = document.IsPatch ? document.IncludePath : null;
        VmtDocument resolved = document.IsPatch
            ? await VmtPatchResolver.ResolveAsync(
                document,
                (name, token) => LoadIncludeAsync(content, name, token),
                VmtPatchDialect.Engine,
                cancellationToken).ConfigureAwait(false)
            : document;

        KeyValuesNode root = resolved.Root;
        string name = MaterialName(path.Value);
        string shader = root.Name.ToLowerInvariant();

        return new MaterialRecord
        {
            Path = name,
            Shader = shader,
            Usage = Classify(name, shader, root),
            Category = CategoryOf(name),
            BaseTexture = TexturePath(root.GetString("$basetexture")),
            BaseTexture2 = TexturePath(root.GetString("$basetexture2")),
            BumpMap = TexturePath(root.GetString("$bumpmap") ?? root.GetString("$normalmap")),
            Detail = TexturePath(root.GetString("$detail")),
            EnvMap = Blank(root.GetString("$envmap")),
            ToolTexture = TexturePath(root.GetString("%tooltexture")),
            SurfaceProp = Blank(root.GetString("$surfaceprop")),
            SurfaceProp2 = Blank(root.GetString("$surfaceprop2")),
            Keywords = Blank(root.GetString("%keywords")),
            Translucent = Flag(root, "$translucent"),
            AlphaTest = Flag(root, "$alphatest"),
            SelfIllum = Flag(root, "$selfillum"),
            Include = include is null ? null : MaterialName(include),
            Parameters = ParametersJson(root),
            Vmt = Encoding.UTF8.GetString(bytes),
            Source = source?.Mount ?? string.Empty,
        };
    }

    /// <summary>
    /// What a material is for, by the first rule that matches: its directory
    /// (<c>tools/</c>, <c>skybox/</c>, <c>decals/</c>), a water shader, the
    /// <c>$decal</c> flag, a brush shader, a model shader or a path under
    /// <c>models/</c>; otherwise <see cref="MaterialUsage.Other"/>.
    /// </summary>
    /// <param name="path">The normalised material name.</param>
    /// <param name="shader">The lower-case shader name.</param>
    /// <param name="root">The resolved material, for its flags.</param>
    /// <returns>One of the <see cref="MaterialUsage"/> values.</returns>
    /// <remarks>
    /// Directory first, because it is what the authors meant: a tool material
    /// is drawn with <c>LightmappedGeneric</c> as often as not, and is still a
    /// tool. <c>sdk_</c>-prefixed shaders are the mod-side copies of the
    /// stock ones and classify as them.
    /// </remarks>
    public static string Classify(string path, string shader, KeyValuesNode root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(root);

        string bare = shader.StartsWith("sdk_", StringComparison.Ordinal) ? shader[4..] : shader;

        if (path.StartsWith("tools/", StringComparison.Ordinal))
        {
            return MaterialUsage.Tool;
        }

        if (path.StartsWith("skybox/", StringComparison.Ordinal))
        {
            return MaterialUsage.Sky;
        }

        if (bare == "water")
        {
            return MaterialUsage.Water;
        }

        if (path.StartsWith("decals/", StringComparison.Ordinal) || Flag(root, "$decal"))
        {
            return MaterialUsage.Decal;
        }

        if (BrushShaders.Contains(bare))
        {
            return MaterialUsage.Brush;
        }

        if (ModelShaders.Contains(bare) || path.StartsWith("models/", StringComparison.Ordinal))
        {
            return MaterialUsage.Model;
        }

        return MaterialUsage.Other;
    }

    /// <summary>
    /// A material name as the database keys it: lower case, forward slashes,
    /// no <c>materials/</c> prefix and no <c>.vmt</c>.
    /// </summary>
    /// <param name="value">A content path or a name as a VMF writes it.</param>
    /// <returns>The key.</returns>
    public static string MaterialName(string value) => Normalise(value, ".vmt");

    /// <summary>
    /// A texture name as the database keys it; null for a blank var, which
    /// materials use as a placeholder.
    /// </summary>
    /// <param name="value">The var's value.</param>
    /// <returns>The key, or null.</returns>
    public static string? TexturePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string key = Normalise(value, ".vtf");
        return key.Length == 0 ? null : key;
    }

    /// <summary>Strips the prefix and extension, folds case and slashes.</summary>
    internal static string Normalise(string value, string extension)
    {
        string key = value.Trim().Replace('\\', '/').ToLowerInvariant();
        while (key.StartsWith('/'))
        {
            key = key[1..];
        }

        if (key.StartsWith("materials/", StringComparison.Ordinal))
        {
            key = key["materials/".Length..];
        }

        if (key.EndsWith(extension, StringComparison.Ordinal))
        {
            key = key[..^extension.Length];
        }

        return key;
    }

    private static string CategoryOf(string name)
    {
        int slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? string.Empty : name[..slash];
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A material flag: present and not zero.</summary>
    private static bool Flag(KeyValuesNode root, string name)
    {
        string? value = root.GetString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return trimmed != "0" && !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The top-level scalars as one JSON object. Nested blocks (proxies,
    /// shader fallbacks) are left out: the raw <c>.vmt</c> carries them, and a
    /// flat object is what a SQL <c>json_extract</c> can query.
    /// </summary>
    private static string ParametersJson(KeyValuesNode root)
    {
        // Last one wins, the way the engine reads a repeated key; ordinal key
        // order so the column is byte-stable whatever order the file uses.
        SortedDictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (KeyValuesNode child in root.Children)
        {
            if (child.Value is not null && !child.IsSection)
            {
                values[child.Name.ToLowerInvariant()] = child.Value;
            }
        }

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in values)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static async Task<VmtDocument?> LoadIncludeAsync(
        IContentFileSystem content,
        string include,
        CancellationToken cancellationToken)
    {
        VPath path = VPath.Create("materials/" + MaterialName(include) + ".vmt");
        byte[]? bytes = await ContentWalk.ReadAsync(content, path, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : await VmtDocument.ParseAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
