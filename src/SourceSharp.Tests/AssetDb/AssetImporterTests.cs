//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.AssetDb.Database;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// A whole import over a fake game: every pass, the database, the README,
/// reuse on the second run and pruning when content changes.
/// </summary>
public sealed class AssetImporterTests
{
    private static AssetTestGame Game((byte R, byte G, byte B, byte A)? wallColour = null) => new AssetTestGame()
        .AddTexture("brick/wall", 8, 8, wallColour)
        .AddMaterial("brick/wall", "LightmappedGeneric { $basetexture brick/wall $surfaceprop brick }")
        .AddTexture("models/props/crate", 4, 4, (200, 0, 0, 255))
        .AddMaterial("models/props/crate", "VertexLitGeneric { $basetexture models/props/crate }")
        .AddModel("props/crate01", AssetTestGame.CubeModel())
        .AddUndecodableTexture("hdr/sky");

    private static Task<ImportSummary> ImportAsync(
        AssetDatabase database,
        IFileSystem output,
        AssetTestGame game,
        string name = "hl2",
        AssetExportOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AssetImporter.ImportAsync(
            database,
            output,
            name,
            "Half-Life 2",
            "/games/hl2",
            game.Mount(),
            options ?? AssetTestGame.Options(),
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 30, 0, TimeSpan.Zero)),
            cancellationToken);

    [Fact]
    public async Task AnImportStoresEveryPassAndWritesTheReadme()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        InMemoryFileSystem output = new();

        ImportSummary summary = await ImportAsync(database, output, Game());

        Assert.Equal(new ImportSummary("hl2", 2, 2, 1, 1, 0), summary);
        GameSummary game = Assert.Single(database.Games());
        Assert.Equal(("Half-Life 2", "2026-11-08T12:30:00Z"), (game.Title, game.ImportedUtc));
        Assert.Equal(
            [["brick/wall", "brush", "textures/brick/wall.png"]],
            database.Query("SELECT material, usage, image FROM material_view WHERE usage = 'brush'").Rows);
        Assert.Equal(
            [["models/props/crate01.mdl", "models/props/crate01/sheet.png"]],
            database.Query("SELECT model, image_sheet FROM model_view").Rows);
        Assert.True(await output.ExistsAsync(VPath.Create("textures/brick/wall.png")));
        Assert.True(await output.ExistsAsync(VPath.Create("models/props/crate01/sheet.png")));
        Assert.Contains("| `hl2` | Half-Life 2 |", System.Text.Encoding.UTF8.GetString(output.GetBytes(VPath.Create(AssetImporter.ReadmeFileName))!), StringComparison.Ordinal);
        Assert.Contains("Games here: `hl2` (Half-Life 2).", System.Text.Encoding.UTF8.GetString(output.GetBytes(VPath.Create(AssetImporter.ClaudeFileName))!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTexturesMaterialsAndModelsAreStillImported()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        InMemoryFileSystem output = new();

        ImportSummary summary = await ImportAsync(
            database, output, Game(), options: AssetTestGame.Options() with { Textures = false });

        Assert.Equal((0, 2, 1), (summary.Textures, summary.Materials, summary.Models));
        Assert.False(await output.ExistsAsync(VPath.Create("textures/brick/wall.png")));
    }

    [Fact]
    public async Task ASecondGameSharingContentReusesItsImages()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        RecordingFileSystem output = new(new InMemoryFileSystem());
        await ImportAsync(database, output, Game());
        int writesAfterFirst = output.Writes.Count;

        ImportSummary tf = await ImportAsync(database, output, Game(), name: "tf");

        // Only the README and CLAUDE.md are written again: every image is found by its hash.
        Assert.Equal(["README.md", "CLAUDE.md"], output.Writes.Skip(writesAfterFirst).Distinct());
        Assert.Equal((2, 1), (tf.Textures, tf.Models));
        Assert.Equal("2", database.Query("SELECT count(*) FROM textures").Rows[0][0]);
        Assert.Equal("4", database.Query("SELECT count(*) FROM texture_view").Rows[0][0]);
    }

    [Fact]
    public async Task ChangedContentPrunesTheImagesOfWhatItReplaced()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        InMemoryFileSystem output = new();
        await ImportAsync(database, output, Game((1, 1, 1, 255)));

        ImportSummary second = await ImportAsync(database, output, Game((2, 2, 2, 255)));

        Assert.Equal(1, second.PrunedImages);
        Assert.False(await output.ExistsAsync(VPath.Create("textures/brick/wall.png")));
        string image = database.Query("SELECT image FROM textures WHERE path = 'brick/wall'").Rows.Single()[0]!;
        Assert.StartsWith("textures/brick/wall~", image, StringComparison.Ordinal);
        Assert.True(await output.ExistsAsync(VPath.Create(image)));
    }

    [Fact]
    public async Task ACancelledImportLeavesTheDatabaseAsItWas()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImportAsync(database, new InMemoryFileSystem(), Game(), cancellationToken: cancelled.Token));

        Assert.Empty(database.Games());
    }

    [Fact]
    public async Task PruneDeletesOnlyFilesThatExist()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        InMemoryFileSystem output = new();
        await ImportAsync(database, output, Game());
        await output.DeleteAsync(VPath.Create("models/props/crate01/front.png"));
        database.RemoveGame("hl2");

        int deleted = await AssetImporter.PruneAsync(database, output, CancellationToken.None);

        // Two textures, six of the model's seven images (front was already gone).
        Assert.Equal(8, deleted);
    }

    [Fact]
    public void TheReadmeListsGamesOrSaysThereAreNone()
    {
        string empty = AgentReadme.Build([]);
        string two = AgentReadme.Build(
        [
            new GameSummary("hl2", "Half-Life 2", "/a", "2026-01-01T00:00:00Z", 1, 2, 3, 4),
            new GameSummary("tf", null, "/b", "2026-01-02T00:00:00Z", 5, 6, 7, 8),
        ]);

        Assert.Contains("None yet", empty, StringComparison.Ordinal);
        Assert.Contains("| `hl2` | Half-Life 2 | 1 | 2 | 3 | 4 | 2026-01-01T00:00:00Z |", two, StringComparison.Ordinal);
        Assert.Contains("| `tf` |  | 5 | 6 | 7 | 8 |", two, StringComparison.Ordinal);
        Assert.Contains("material_view", two, StringComparison.Ordinal);
        Assert.EndsWith("\n", two, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClaudeFileNamesTheGamesAndTheRules()
    {
        string empty = AgentReadme.BuildClaude([]);
        string two = AgentReadme.BuildClaude(
        [
            new GameSummary("hl2", "Half-Life 2", "/a", "2026-01-01T00:00:00Z", 1, 2, 3, 4),
            new GameSummary("tf", null, "/b", "2026-01-02T00:00:00Z", 5, 6, 7, 8),
        ]);

        Assert.Contains("Games here: none yet.", empty, StringComparison.Ordinal);
        Assert.Contains("Games here: `hl2` (Half-Life 2), `tf`.", two, StringComparison.Ordinal);
        Assert.Contains("usage = 'brush'", two, StringComparison.Ordinal);
        Assert.Contains("image_sheet", two, StringComparison.Ordinal);
        Assert.EndsWith("\n", two, StringComparison.Ordinal);
    }

    /// <summary>A clock stopped at one instant.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Records the paths written through it, in order.</summary>
    private sealed class RecordingFileSystem(InMemoryFileSystem inner) : IFileSystem
    {
        public List<string> Writes { get; } = [];

        public ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(path, cancellationToken);

        public ValueTask<System.Buffers.IMemoryOwner<byte>> ReadAllAsync(VPath path, CancellationToken cancellationToken = default) =>
            inner.ReadAllAsync(path, cancellationToken);

        public ValueTask<FileRange> ReadRangeAsync(VPath path, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadRangeAsync(path, offset, length, cancellationToken);

        public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default)
        {
            lock (Writes)
            {
                Writes.Add(path.Value);
            }

            return inner.OpenWriteAsync(path, cancellationToken);
        }

        public ValueTask ReplaceAsync(VPath path, Func<Stream, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default)
        {
            lock (Writes)
            {
                Writes.Add(path.Value);
            }

            return inner.ReplaceAsync(path, write, cancellationToken);
        }

        public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(path, cancellationToken);

        public ValueTask<FileInfoSnapshot?> GetInfoAsync(VPath path, CancellationToken cancellationToken = default) =>
            inner.GetInfoAsync(path, cancellationToken);

        public IAsyncEnumerable<VPath> EnumerateAsync(VPath directory, string searchPattern = "*", bool recursive = false, CancellationToken cancellationToken = default) =>
            inner.EnumerateAsync(directory, searchPattern, recursive, cancellationToken);

        public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(path, cancellationToken);
    }
}
