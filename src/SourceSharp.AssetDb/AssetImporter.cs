//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.AssetDb.Database;
using SourceSharp.AssetDb.Maps;
using SourceSharp.AssetDb.Materials;
using SourceSharp.AssetDb.Models;
using SourceSharp.AssetDb.Textures;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb;

/// <summary>What one import did, for the host to report.</summary>
/// <param name="Game">The short name the game was stored under.</param>
/// <param name="Textures">How many textures the game can see.</param>
/// <param name="Materials">How many materials it has.</param>
/// <param name="Models">How many models were exported.</param>
/// <param name="Issues">How many inputs were passed over.</param>
/// <param name="PrunedImages">How many images of assets no game uses any more were deleted.</param>
/// <param name="Previews">How many lit material previews the game has.</param>
/// <param name="Maps">How many of the game's own maps were read for usage.</param>
public sealed record ImportSummary(
    string Game,
    int Textures,
    int Materials,
    int Models,
    int Issues,
    int PrunedImages,
    int Previews = 0,
    int Maps = 0);

/// <summary>
/// Imports one mounted game into an asset database: materials, then textures,
/// then lit previews of bump-mapped materials, then the game's own maps for
/// usage, then models, then one transaction to store them, then a sweep of
/// what no game uses any more, then the README and CLAUDE.md.
/// </summary>
/// <remarks>
/// <para>
/// Materials go first because models need them: a model's slots resolve to
/// materials, and the material says which texture to skin the render with
/// and whether its alpha cuts holes.
/// </para>
/// <para>
/// The database is written only after every pass has finished, so a failed
/// or cancelled import leaves the previous import of the game in place. The
/// images an interrupted import already wrote are not wasted: the next run
/// finds them by hash and keeps them.
/// </para>
/// </remarks>
public static class AssetImporter
{
    /// <summary>The README's file name, beside the database.</summary>
    public const string ReadmeFileName = "README.md";

    /// <summary>
    /// The agent instructions' file name, beside the database: the file a
    /// Claude Code session started in (or under) the directory loads on its
    /// own, so the workflow reaches the agent without anyone pasting it.
    /// </summary>
    public const string ClaudeFileName = "CLAUDE.md";

    /// <summary>Imports a game.</summary>
    /// <param name="database">The open database.</param>
    /// <param name="output">The database's directory, where images and the README go.</param>
    /// <param name="name">The short name to store the game under.</param>
    /// <param name="title">The game's title, when known.</param>
    /// <param name="directory">The directory the gameinfo was read from, recorded for reference.</param>
    /// <param name="content">The mounted game.</param>
    /// <param name="options">What to export and how hard to work.</param>
    /// <param name="clock">Where the import's timestamp comes from.</param>
    /// <param name="cancellationToken">Cancels the import; the database is left as it was.</param>
    /// <returns>What was stored.</returns>
    public static async Task<ImportSummary> ImportAsync(
        AssetDatabase database,
        IFileSystem output,
        string name,
        string? title,
        string directory,
        IContentFileSystem content,
        AssetExportOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        List<ImportIssue> issues = [];

        (IReadOnlyList<MaterialRecord> materials, IReadOnlyList<ImportIssue> materialIssues) =
            await MaterialReader.ReadAllAsync(content, options.MaxParallelism, cancellationToken).ConfigureAwait(false);
        issues.AddRange(materialIssues);
        options.Progress?.Report(new ImportProgress("materials", materials.Count, materials.Count));

        IReadOnlyList<TextureRecord> textures = [];
        IReadOnlyList<PreviewRecord> previews = [];
        if (options.Textures)
        {
            AssetIndex<TextureRecord> knownTextures = TextureExporter.Index(database.LoadTextures());
            (textures, IReadOnlyList<ImportIssue> textureIssues) = await TextureExporter.ExportAsync(
                content, output, knownTextures, TextureExporter.BumpMapKinds(materials), options, cancellationToken)
                .ConfigureAwait(false);
            issues.AddRange(textureIssues);

            (previews, IReadOnlyList<ImportIssue> previewIssues) = await MaterialPreview.RenderAsync(
                content, output, materials, MaterialPreview.Index(database.LoadPreviews()), options, cancellationToken)
                .ConfigureAwait(false);
            issues.AddRange(previewIssues);
        }

        IReadOnlyList<MapUsage> maps = [];
        if (options.Maps)
        {
            (maps, IReadOnlyList<ImportIssue> mapIssues) = await MapUsageScanner.ScanAsync(content, options, cancellationToken)
                .ConfigureAwait(false);
            issues.AddRange(mapIssues);
        }

        Dictionary<string, MaterialRecord> byName = new(StringComparer.Ordinal);
        foreach (MaterialRecord material in materials)
        {
            byName[material.Path] = material;
        }

        AssetIndex<KnownModel> knownModels = ModelExporter.Index(database.LoadModels());
        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> modelIssues) = await ModelExporter.ExportAsync(
            content, output, knownModels, byName, options, cancellationToken).ConfigureAwait(false);
        issues.AddRange(modelIssues);

        cancellationToken.ThrowIfCancellationRequested();
        database.ReplaceGame(
            new GameImport
            {
                Name = name,
                Title = title,
                Directory = directory,
                ImportedUtc = clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Textures = textures,
                Materials = materials,
                Models = models,
                Issues = issues,
                Previews = previews,
                Maps = maps,
            },
            cancellationToken);

        int pruned = await PruneAsync(database, output, cancellationToken).ConfigureAwait(false);
        await WriteReadmeAsync(database, output, cancellationToken).ConfigureAwait(false);
        return new ImportSummary(name, textures.Count, materials.Count, models.Count, issues.Count, pruned, previews.Count, maps.Count);
    }

    /// <summary>
    /// Deletes the rows no game links to and the images they owned.
    /// </summary>
    /// <param name="database">The open database.</param>
    /// <param name="output">The database's directory.</param>
    /// <param name="cancellationToken">Cancels the file deletes; the rows are already gone.</param>
    /// <returns>How many image files were deleted.</returns>
    public static async Task<int> PruneAsync(
        AssetDatabase database,
        IFileSystem output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(output);

        int deleted = 0;
        foreach (string image in database.PruneOrphans())
        {
            VPath path = VPath.Create(image);
            if (await output.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                await output.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>Rewrites the README and CLAUDE.md beside the database from its current games.</summary>
    /// <param name="database">The open database.</param>
    /// <param name="output">The database's directory.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static async Task WriteReadmeAsync(
        AssetDatabase database,
        IFileSystem output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<GameSummary> games = database.Games();
        await WriteTextAsync(output, ReadmeFileName, AgentReadme.Build(games), cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(output, ClaudeFileName, AgentReadme.BuildClaude(games), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        IFileSystem output,
        string name,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await output.ReplaceAsync(
            VPath.Create(name),
            async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }
}
