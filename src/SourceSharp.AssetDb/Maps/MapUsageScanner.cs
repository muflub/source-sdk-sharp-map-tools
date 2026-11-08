//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using SourceSharp.AssetDb.Materials;
using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb.Maps;

/// <summary>What one compiled map uses.</summary>
public sealed record MapUsage
{
    /// <summary>Gets the map's name: the file name without <c>.bsp</c>, lower case.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the SHA-256 of the <c>.bsp</c>.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the mount the map was read from.</summary>
    public required string Source { get; init; }

    /// <summary>Gets the worldspawn's <c>skyname</c>, or null.</summary>
    public string? SkyName { get; init; }

    /// <summary>Gets how many faces the map draws.</summary>
    public required int Faces { get; init; }

    /// <summary>Gets how many static props it places.</summary>
    public required int StaticProps { get; init; }

    /// <summary>Gets how many entities it has.</summary>
    public required int Entities { get; init; }

    /// <summary>Gets the materials it uses, by material and kind.</summary>
    public required IReadOnlyList<MapMaterialUse> Materials { get; init; }

    /// <summary>Gets the models it places, by model and kind.</summary>
    public required IReadOnlyList<MapModelUse> Models { get; init; }

    /// <summary>
    /// Gets why the static props could not be read, or null when they were.
    /// The rest of the map's usage is still recorded: one unreadable lump
    /// should not cost the map's materials and entity models.
    /// </summary>
    public string? StaticPropsIssue { get; init; }
}

/// <summary>One material's use in one map.</summary>
/// <param name="Material">The material's name, with the compiler's per-map renames undone.</param>
/// <param name="Kind"><c>face</c> (brush and displacement faces), <c>overlay</c> or <c>decal</c>.</param>
/// <param name="Count">How many faces, overlays or decals.</param>
/// <param name="Area">For faces, their total area in square units; zero otherwise.</param>
public sealed record MapMaterialUse(string Material, string Kind, int Count, double Area);

/// <summary>One model's use in one map.</summary>
/// <param name="Model">The model's path, lower case, as an entity names it.</param>
/// <param name="Kind"><c>static</c> for <c>prop_static</c>, <c>entity</c> for any entity's <c>model</c> key.</param>
/// <param name="Count">How many times it is placed.</param>
public sealed record MapModelUse(string Model, string Kind, int Count);

/// <summary>
/// Reads the compiled maps a game ships and records which materials and
/// models each uses, so the database can say what the game's own designers
/// used, how much, and alongside what.
/// </summary>
/// <remarks>
/// <para>
/// Only the lumps that name content are read, through <see cref="BspLumps"/>
/// so maps shipped with LZMA-compressed lumps read too: faces through texinfo and
/// texdata to the material string table (with each face's area), the static
/// prop dictionary and instances, and the entity lump for model keys,
/// overlays, decals and the sky name. Lighting, visibility and the pak are
/// ignored.
/// </para>
/// <para>
/// The compiler renames some materials per map: a face near a cubemap gets
/// <c>maps/&lt;map&gt;/&lt;material&gt;_x_y_z</c>, water gets a
/// <c>_depth_N</c> or <c>_wvt_patch</c> variant. Those are folded back to
/// the material an author picked, which is what the statistics are about.
/// </para>
/// </remarks>
public static partial class MapUsageScanner
{
    /// <summary>Scans every <c>maps/*.bsp</c> the game can see.</summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="options">Parallelism and progress.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The usage per map and the maps that could not be read, each sorted by name.</returns>
    public static async Task<(IReadOnlyList<MapUsage> Maps, IReadOnlyList<ImportIssue> Issues)> ScanAsync(
        IContentFileSystem content,
        AssetExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        List<VPath> paths = await ContentWalk.ListAsync(content, "maps", "*.bsp", cancellationToken).ConfigureAwait(false);
        ConcurrentBag<MapUsage> maps = [];
        ConcurrentBag<ImportIssue> issues = [];
        int done = 0;

        // Half the workers: a map is read whole and can be a hundred
        // megabytes, so the full width would hold gigabytes at once.
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism / 2),
                CancellationToken = cancellationToken,
            },
            async (path, token) =>
            {
                try
                {
                    byte[]? bytes = await ContentWalk.ReadAsync(content, path, token).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        issues.Add(new ImportIssue("map", path.Value, "listed by the mount but not readable"));
                    }
                    else
                    {
                        ContentSource? source = await content.ResolveAsync(path, token).ConfigureAwait(false);
                        MapUsage usage = Read(MapName(path.Value), bytes);
                        maps.Add(usage with { Source = source?.Mount ?? string.Empty });
                        if (usage.StaticPropsIssue is { } partial)
                        {
                            issues.Add(new ImportIssue("map", path.Value, partial));
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    issues.Add(new ImportIssue("map", path.Value, e.Message));
                }

                options.Progress?.Report(new ImportProgress("maps", Interlocked.Increment(ref done), paths.Count));
            }).ConfigureAwait(false);

        return (
            [.. maps.OrderBy(m => m.Name, StringComparer.Ordinal)],
            [.. issues.OrderBy(i => i.Path, StringComparer.Ordinal)]);
    }

    /// <summary>Reads one map's usage.</summary>
    /// <param name="name">The map's name, for undoing the compiler's per-map renames.</param>
    /// <param name="bytes">The whole <c>.bsp</c>.</param>
    /// <returns>The usage, with an empty <see cref="MapUsage.Source"/>.</returns>
    /// <exception cref="InvalidBspException">The file is not a BSP this port reads.</exception>
    /// <exception cref="InvalidDataException">A compressed lump does not unpack.</exception>
    public static MapUsage Read(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bytes);

        BspLumps bsp = BspLumps.Read(bytes);
        ReadOnlyMemory<byte> faceLump = bsp.Lump(BspLump.Faces);
        if (faceLump.IsEmpty)
        {
            faceLump = bsp.Lump(BspLump.FacesHdr);
        }

        ReadOnlyMemory<byte> texInfoLump = bsp.Lump(BspLump.TexInfo);
        ReadOnlyMemory<byte> texDataLump = bsp.Lump(BspLump.TexData);
        ReadOnlyMemory<byte> stringTableLump = bsp.Lump(BspLump.TexDataStringTable);
        ReadOnlyMemory<byte> stringDataLump = bsp.Lump(BspLump.TexDataStringData);
        ReadOnlyMemory<byte> entityLump = bsp.Lump(BspLump.Entities);

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(faceLump.Span);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(texInfoLump.Span);
        ReadOnlySpan<DTexData> texDatas = BspStructView.As<DTexData>(texDataLump.Span);
        ReadOnlySpan<int> stringTable = BspStructView.As<int>(stringTableLump.Span);
        ReadOnlySpan<byte> stringData = stringDataLump.Span;

        Dictionary<(string, string), (int Count, double Area)> materials = [];
        foreach (DFace face in faces)
        {
            if ((uint)face.TexInfo >= (uint)texInfos.Length)
            {
                continue;
            }

            int texData = texInfos[face.TexInfo].TexData;
            if ((uint)texData >= (uint)texDatas.Length)
            {
                continue;
            }

            int stringId = texDatas[texData].NameStringTableId;
            if ((uint)stringId >= (uint)stringTable.Length)
            {
                continue;
            }

            string material = MaterialName(CString(stringData, stringTable[stringId]), name);
            Add(materials, (material, "face"), face.Area);
        }

        List<BspEntity> entities = EntityLump.Parse(new BspLumpData(entityLump, 0, 0));
        Dictionary<(string, string), int> models = [];
        string? skyName = null;
        foreach (BspEntity entity in entities)
        {
            string className = entity.ClassName;
            if (className == "worldspawn")
            {
                skyName = entity.Get("skyname") is { Length: > 0 } sky ? sky.ToLowerInvariant() : null;
            }
            else if (className == "info_overlay" && entity.Get("material") is { Length: > 0 } overlay)
            {
                Add(materials, (MaterialName(overlay, name), "overlay"), 0);
            }
            else if (className == "infodecal" && entity.Get("texture") is { Length: > 0 } decal)
            {
                Add(materials, (MaterialName(decal, name), "decal"), 0);
            }

            if (entity.Get("model") is { } model && model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                Count(models, (ModelName(model), "entity"));
            }
        }

        int staticProps = 0;
        string? staticPropsIssue = null;
        foreach (GameLumpEntry entry in bsp.GameLumps())
        {
            if (entry.Id != GameLumpId.MakeId(GameLumpId.StaticProps))
            {
                continue;
            }

            if (!TryReadStaticPropModels(entry.Data.Span, out List<string> placed, out string? problem))
            {
                staticPropsIssue = $"static props not counted: {problem}";
                continue;
            }

            foreach (string model in placed)
            {
                Count(models, (ModelName(model), "static"));
            }

            staticProps += placed.Count;
        }

        return new MapUsage
        {
            Name = name,
            Sha256 = ContentWalk.Sha256(bytes),
            Source = string.Empty,
            SkyName = skyName,
            Faces = faces.Length,
            StaticProps = staticProps,
            Entities = entities.Count,
            StaticPropsIssue = staticPropsIssue,
            Materials = [.. materials
                .Select(kv => new MapMaterialUse(kv.Key.Item1, kv.Key.Item2, kv.Value.Count, Math.Round(kv.Value.Area, 1)))
                .OrderBy(m => m.Material, StringComparer.Ordinal).ThenBy(m => m.Kind, StringComparer.Ordinal)],
            Models = [.. models
                .Select(kv => new MapModelUse(kv.Key.Item1, kv.Key.Item2, kv.Value))
                .OrderBy(m => m.Model, StringComparer.Ordinal).ThenBy(m => m.Kind, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// The model each static prop places, read without knowing the lump's
    /// version: the model dictionary, the leaf list, then the props, whose
    /// record size is whatever the remaining bytes divide into.
    /// </summary>
    /// <param name="data">The <c>sprp</c> game lump entry, unpacked.</param>
    /// <param name="models">The model of each prop, in lump order.</param>
    /// <param name="problem">Why the lump could not be read, when it could not.</param>
    /// <returns>True when every prop's model was read.</returns>
    /// <remarks>
    /// Every version of the record starts with the origin and angles (24
    /// bytes) followed by the 16-bit index into the model dictionary; what
    /// differs between versions is what follows. Reading by stride rather
    /// than by version struct is what lets maps from other engine branches
    /// read: Team Fortress 2 ships maps labelled version 7 whose records are
    /// the 72-byte version-10 layout.
    /// </remarks>
    public static bool TryReadStaticPropModels(ReadOnlySpan<byte> data, out List<string> models, out string? problem)
    {
        const int NameLength = 128;
        const int PropTypeOffset = 24;
        models = [];
        problem = null;

        if (!TryCount(data, 0, out int names) || 4 + ((long)names * NameLength) > data.Length)
        {
            problem = "the model dictionary does not fit the lump";
            return false;
        }

        List<string> dictionary = [];
        for (int i = 0; i < names; i++)
        {
            dictionary.Add(CString(data.Slice(4 + (i * NameLength), NameLength), 0));
        }

        int at = 4 + (names * NameLength);
        if (!TryCount(data, at, out int leaves) || at + 4 + ((long)leaves * 2) > data.Length)
        {
            problem = "the leaf list does not fit the lump";
            return false;
        }

        at += 4 + (leaves * 2);
        if (!TryCount(data, at, out int props))
        {
            problem = "the prop count is missing";
            return false;
        }

        at += 4;
        int remaining = data.Length - at;
        if (props == 0)
        {
            return true;
        }

        if (remaining % props != 0 || remaining / props < PropTypeOffset + 2)
        {
            problem = $"{props} props do not divide the {remaining} bytes left into records";
            return false;
        }

        int stride = remaining / props;
        for (int i = 0; i < props; i++)
        {
            int type = data[at + (i * stride) + PropTypeOffset] | (data[at + (i * stride) + PropTypeOffset + 1] << 8);
            if (type >= dictionary.Count)
            {
                problem = $"prop {i} names model {type} of a {dictionary.Count}-entry dictionary";
                models = [];
                return false;
            }

            models.Add(dictionary[type]);
        }

        return true;
    }

    private static bool TryCount(ReadOnlySpan<byte> data, int at, out int count)
    {
        count = 0;
        if (at < 0 || at + 4 > data.Length)
        {
            return false;
        }

        count = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);
        return count >= 0;
    }

    /// <summary>A map's name from its content path: the file name without <c>.bsp</c>, lower case.</summary>
    /// <param name="path">The content path.</param>
    /// <returns>The name.</returns>
    public static string MapName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string file = path.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return file.EndsWith(".bsp", StringComparison.Ordinal) ? file[..^4] : file;
    }

    /// <summary>
    /// A material name as a map stores it, folded back to the material an
    /// author picked: <c>maps/&lt;map&gt;/</c> stripped, then a cubemap
    /// position suffix (<c>_x_y_z</c>), a water <c>_depth_N</c> or a
    /// <c>_wvt_patch</c> suffix.
    /// </summary>
    /// <param name="stored">The name in the map.</param>
    /// <param name="map">The map's name.</param>
    /// <returns>The normalised material name.</returns>
    public static string MaterialName(string stored, string map)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(map);

        string name = MaterialReader.MaterialName(stored);
        string prefix = $"maps/{map}/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return name;
        }

        name = name[prefix.Length..];
        name = WvtPatch().Replace(name, string.Empty);
        name = DepthSuffix().Replace(name, string.Empty);
        return CubemapSuffix().Replace(name, string.Empty);
    }

    /// <summary>A model path as the database keys it: lower case, forward slashes.</summary>
    /// <param name="model">The path as an entity or the prop dictionary spells it.</param>
    /// <returns>The key.</returns>
    public static string ModelName(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    private static void Add(Dictionary<(string, string), (int Count, double Area)> into, (string, string) key, double area)
    {
        (int count, double sum) = into.GetValueOrDefault(key);
        into[key] = (count + 1, sum + area);
    }

    private static void Count(Dictionary<(string, string), int> into, (string, string) key) =>
        into[key] = into.GetValueOrDefault(key) + 1;

    private static string CString(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset >= (uint)data.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> tail = data[offset..];
        int end = tail.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(end < 0 ? tail : tail[..end]);
    }

    [GeneratedRegex(@"(_-?\d+){3}$")]
    private static partial Regex CubemapSuffix();

    [GeneratedRegex(@"_depth_-?\d+$")]
    private static partial Regex DepthSuffix();

    [GeneratedRegex(@"_wvt_patch$")]
    private static partial Regex WvtPatch();
}
