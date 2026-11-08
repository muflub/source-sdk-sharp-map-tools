//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Microsoft.Data.Sqlite;

using SourceSharp.AssetDb;
using SourceSharp.AssetDb.Database;
using SourceSharp.AssetDb.Maps;
using SourceSharp.AssetDb.Materials;
using SourceSharp.AssetDb.Models;
using SourceSharp.AssetDb.Textures;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The SQLite store: what a game's import writes, how games share textures
/// and models, re-import, removal, pruning and the views agents read.
/// </summary>
public sealed class AssetDatabaseTests
{
    internal static TextureRecord Texture(string path, string sha = "aa", string? image = null) => new()
    {
        Path = path,
        Sha256 = sha,
        Width = 64,
        Height = 32,
        Format = "Dxt1",
        Mips = 7,
        Frames = 1,
        Flags = "ClampS",
        Reflectivity = (0.25f, 0.5f, 0.75f),
        Kind = TextureKind.Color,
        AverageColour = "#102030",
        HasAlpha = false,
        Image = image ?? $"textures/{path}.png",
        ImageWidth = 64,
        ImageHeight = 32,
        Source = "hl2_textures.vpk",
    };

    internal static MaterialRecord Material(string path, string? baseTexture = null, string? toolTexture = null) => new()
    {
        Path = path,
        Shader = "lightmappedgeneric",
        Usage = MaterialUsage.Brush,
        Category = path.Split('/')[0],
        BaseTexture = baseTexture,
        ToolTexture = toolTexture,
        SurfaceProp = "brick",
        Parameters = "{}",
        Vmt = "LightmappedGeneric {}",
        Source = "hl2_misc.vpk",
    };

    internal static ModelRecord Model(string path, string sha = "bb") => new()
    {
        Path = path,
        Sha256 = sha,
        Name = "crate.mdl",
        Flags = ModelRecord.StaticPropFlag,
        SurfaceProp = "wood",
        Mass = 30,
        Hull = (Vec3.Zero, Vec3.Zero),
        View = (new Vec3(-1, -1, -1), new Vec3(1, 1, 1)),
        Mesh = (new Vec3(-20, -10, 0), new Vec3(20, 10, 40)),
        Bones = 1,
        Sequences = 1,
        BodyParts = 1,
        Lods = 1,
        Triangles = 12,
        Vertices = 8,
        Skins = 1,
        HasCollision = true,
        PropData = "Wooden.Medium",
        KeyValues = "mdlkeyvalue {}",
        GridStep = 8,
        ImageDirectory = ModelExporter.NaturalImageDirectory(path),
        Materials = [new ModelMaterial(0, 0, "crate", "models/props/crate")],
        Source = "hl2_misc.vpk",
    };

    private static GameImport Import(
        string name,
        IReadOnlyList<TextureRecord>? textures = null,
        IReadOnlyList<MaterialRecord>? materials = null,
        IReadOnlyList<ModelRecord>? models = null,
        IReadOnlyList<ImportIssue>? issues = null) => new()
        {
            Name = name,
            Title = name.ToUpperInvariant(),
            Directory = $"/games/{name}",
            ImportedUtc = "2026-11-08T00:00:00Z",
            Textures = textures ?? [],
            Materials = materials ?? [],
            Models = models ?? [],
            Issues = issues ?? [],
        };

    private static string?[][] Rows(AssetDatabase database, string sql) =>
        [.. database.Query(sql).Rows];

    [Fact]
    public void AFreshDatabaseHasTheSchemaAndNoGames()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");

        Assert.Empty(database.Games());
        Assert.Equal(AssetDatabase.SchemaVersion.ToString(), Rows(database, "PRAGMA user_version")[0][0]);
        Assert.Empty(Rows(database, "SELECT * FROM material_view"));
        Assert.Empty(Rows(database, "SELECT * FROM model_view"));
        Assert.Empty(Rows(database, "SELECT * FROM texture_view"));
    }

    [Fact]
    public void ReopeningAFileKeepsItsContents()
    {
        using TempTree temp = new();
        string file = Path.Combine(temp.Root, "assets.db");
        using (AssetDatabase database = AssetDatabase.Open(file))
        {
            database.ReplaceGame(Import("hl2", [Texture("a/b")]), CancellationToken.None);
        }

        using AssetDatabase reopened = AssetDatabase.Open(file);
        Assert.Equal("hl2", Assert.Single(reopened.Games()).Name);
    }

    [Fact]
    public void AFileFromAnotherSchemaVersionIsRefused()
    {
        using TempTree temp = new();
        string file = Path.Combine(temp.Root, "assets.db");
        using (SqliteConnection connection = new($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => AssetDatabase.Open(file));
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadOnlyDatabaseReadsButRefusesWrites()
    {
        using TempTree temp = new();
        string file = Path.Combine(temp.Root, "assets.db");
        using (AssetDatabase database = AssetDatabase.Open(file))
        {
            database.ReplaceGame(Import("hl2", [Texture("a/b")]), CancellationToken.None);
        }

        using AssetDatabase readOnly = AssetDatabase.OpenReadOnly(file);

        Assert.Equal("hl2", Assert.Single(readOnly.Games()).Name);
        Assert.Throws<SqliteException>(() => readOnly.Query("DELETE FROM games"));
        Assert.Equal("hl2", Assert.Single(readOnly.Games()).Name);
    }

    [Fact]
    public void ReadOnlyRefusesAnotherSchemaVersionAndAMissingFile()
    {
        using TempTree temp = new();
        string file = Path.Combine(temp.Root, "assets.db");
        using (SqliteConnection connection = new($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 7";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => AssetDatabase.OpenReadOnly(file));
        Assert.Throws<SqliteException>(() => AssetDatabase.OpenReadOnly(Path.Combine(temp.Root, "missing.db")));
    }

    [Fact]
    public void AGamesImportIsQueryableThroughTheViews()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");

        database.ReplaceGame(
            Import(
                "hl2",
                [Texture("brick/wall"), Texture("dev/water")],
                [Material("brick/wall", baseTexture: "brick/wall"), Material("liquids/water", toolTexture: "dev/water")],
                [Model("models/props/crate.mdl")],
                [new ImportIssue("texture", "materials/x.vtf", "float")]),
            CancellationToken.None);

        GameSummary game = Assert.Single(database.Games());
        Assert.Equal(new GameSummary("hl2", "HL2", "/games/hl2", "2026-11-08T00:00:00Z", 2, 2, 1, 1, 0), game);
        Assert.Equal(
            [["brick/wall", "brush", "textures/brick/wall.png"], ["liquids/water", "brush", "textures/dev/water.png"]],
            Rows(database, "SELECT material, usage, image FROM material_view ORDER BY material"));
        Assert.Equal(
            [["models/props/crate.mdl", "1", "40", "20", "40", "models/props/crate/sheet.png", "models/props/crate/top.png"]],
            Rows(database, "SELECT model, static_prop, size_x, size_y, size_z, image_sheet, image_top FROM model_view"));
        Assert.Equal(
            [["crate", "models/props/crate"]],
            Rows(database, "SELECT texture, material FROM model_materials"));
        Assert.Equal(2, Rows(database, "SELECT * FROM texture_view WHERE game = 'hl2'").Length);
    }

    [Fact]
    public void AMaterialWhosePreviewTextureTheGameLacksHasNoImage()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");

        database.ReplaceGame(Import("hl2", [], [Material("a/b", baseTexture: "nowhere")]), CancellationToken.None);

        Assert.Equal([["a/b", null]], Rows(database, "SELECT material, image FROM material_view"));
    }

    [Fact]
    public void GamesShareIdenticalTexturesAndModels()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");

        database.ReplaceGame(Import("hl2", [Texture("a/b")], models: [Model("models/m.mdl")]), CancellationToken.None);
        database.ReplaceGame(Import("tf", [Texture("a/b")], models: [Model("models/m.mdl")]), CancellationToken.None);

        Assert.Equal("1", Rows(database, "SELECT count(*) FROM textures")[0][0]);
        Assert.Equal("1", Rows(database, "SELECT count(*) FROM models")[0][0]);
        Assert.Equal("2", Rows(database, "SELECT count(*) FROM texture_view")[0][0]);
        Assert.Equal("2", Rows(database, "SELECT count(*) FROM model_view")[0][0]);
    }

    [Fact]
    public void ReimportingAGameReplacesItsRowsRatherThanAddingToThem()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        database.ReplaceGame(
            Import("hl2", [Texture("a/b")], [Material("a/b", "a/b")], [Model("models/m.mdl")], [new ImportIssue("model", "x", "y")]),
            CancellationToken.None);

        database.ReplaceGame(Import("hl2", [Texture("a/b"), Texture("c/d")], [Material("c/d")]), CancellationToken.None);

        GameSummary game = Assert.Single(database.Games());
        Assert.Equal((2, 1, 0, 0), (game.Textures, game.Materials, game.Models, game.Issues));
        Assert.Equal([["c/d"]], Rows(database, "SELECT material FROM material_view"));
    }

    [Fact]
    public void ChangedContentAtAPathIsANewRowAndTheOldOneIsPrunedWithItsImages()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        database.ReplaceGame(Import("hl2", [Texture("a/b", "old")], models: [Model("models/m.mdl", "old")]), CancellationToken.None);
        database.ReplaceGame(
            Import("hl2", [Texture("a/b", "new", "textures/a/b~new.png")], models: [Model("models/m.mdl", "new") with { ImageDirectory = "models/m~new" }]),
            CancellationToken.None);

        IReadOnlyList<string> orphans = database.PruneOrphans();

        Assert.Contains("textures/a/b.png", orphans);
        Assert.Contains("models/m/sheet.png", orphans);
        Assert.Contains("models/m/iso.png", orphans);
        Assert.Equal(8, orphans.Count);
        Assert.Equal([["new"]], Rows(database, "SELECT sha256 FROM textures"));
        Assert.Equal([["new"]], Rows(database, "SELECT sha256 FROM models"));
        Assert.Empty(database.PruneOrphans());
    }

    [Fact]
    public void ATextureAnotherGameStillUsesIsNotPruned()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        database.ReplaceGame(Import("hl2", [Texture("a/b")]), CancellationToken.None);
        database.ReplaceGame(Import("tf", [Texture("a/b")]), CancellationToken.None);

        Assert.True(database.RemoveGame("hl2"));

        Assert.Empty(database.PruneOrphans());
        Assert.Equal("tf", Assert.Single(database.Games()).Name);
    }

    [Fact]
    public void RemovingAGameDropsItsRowsAndAnUnknownGameIsFalse()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        database.ReplaceGame(Import("hl2", [Texture("a/b")], [Material("a/b")], [Model("models/m.mdl")]), CancellationToken.None);

        Assert.False(database.RemoveGame("nope"));
        Assert.True(database.RemoveGame("hl2"));

        Assert.Empty(database.Games());
        Assert.Equal("0", Rows(database, "SELECT count(*) FROM materials")[0][0]);
        Assert.Equal(8, database.PruneOrphans().Count); // one texture, a model's six views and sheet
    }

    [Fact]
    public void LoadedRecordsRoundTripWhatWasStored()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        TextureRecord texture = Texture("a/b");
        ModelRecord model = Model("models/m.mdl");
        database.ReplaceGame(Import("hl2", [texture], models: [model]), CancellationToken.None);

        Assert.Equal(texture with { Source = string.Empty }, Assert.Single(database.LoadTextures()));
        Assert.Equal(new KnownModel(model.Path, model.Sha256, model.ImageDirectory), Assert.Single(database.LoadModels()));
    }

    [Fact]
    public void ACancelledImportLeavesThePreviousOneInPlace()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        database.ReplaceGame(Import("hl2", [Texture("a/b")]), CancellationToken.None);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            database.ReplaceGame(Import("hl2", [Texture("c/d"), Texture("e/f")]), cancelled.Token));

        Assert.Equal([["a/b"]], Rows(database, "SELECT texture FROM texture_view"));
    }

    [Fact]
    public void AnImagelessTextureRoundTripsWithItsNulls()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        TextureRecord normal = Texture("a/n") with
        {
            Kind = TextureKind.Normal, Image = null, AverageColour = null, ImageWidth = 0, ImageHeight = 0,
        };

        database.ReplaceGame(Import("hl2", [normal]), CancellationToken.None);

        Assert.Equal(normal with { Source = string.Empty }, Assert.Single(database.LoadTextures()));
        Assert.Equal([["normal", null]], Rows(database, "SELECT kind, image FROM texture_view"));
        Assert.Empty(database.PruneOrphans());
    }

    [Fact]
    public void PreviewsLinkToTheirMaterialsShareByHashAndPruneWhenUnused()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        MaterialRecord wall = Material("brick/wall", "brick/wall") with { BumpMap = "brick/wall_n" };
        PreviewRecord preview = new("brick/wall", "p1", "previews/brick/wall.png", 64, 64);

        database.ReplaceGame(Import("hl2", [Texture("brick/wall")], [wall]) with { Previews = [preview] }, CancellationToken.None);
        database.ReplaceGame(Import("tf", [Texture("brick/wall")], [wall]) with { Previews = [preview] }, CancellationToken.None);

        Assert.Equal(
            [["hl2", "textures/brick/wall.png", "previews/brick/wall.png"], ["tf", "textures/brick/wall.png", "previews/brick/wall.png"]],
            Rows(database, "SELECT game, image, preview FROM material_view ORDER BY game"));
        Assert.Equal([preview], database.LoadPreviews());
        Assert.Empty(database.PruneOrphans());

        database.RemoveGame("hl2");
        database.RemoveGame("tf");
        Assert.Contains("previews/brick/wall.png", database.PruneOrphans());
        Assert.Empty(database.LoadPreviews());
    }

    [Fact]
    public void MapUsageFeedsTheViewsAndIsReplacedOnReimport()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        MapUsage well = new()
        {
            Name = "cp_well", Sha256 = "w", Source = "tf", SkyName = "sky_well_01", Faces = 10, StaticProps = 3, Entities = 5,
            Materials = [new MapMaterialUse("brick/wall", "face", 4, 512.5), new MapMaterialUse("decals/x", "decal", 1, 0)],
            Models = [new MapModelUse("models/m.mdl", "static", 3), new MapModelUse("models/m.mdl", "entity", 1)],
        };
        MapUsage fort = well with
        {
            Name = "ctf_2fort", Materials = [new MapMaterialUse("brick/wall", "face", 1, 100)], Models = [],
        };
        GameImport import = Import("tf", [Texture("brick/wall")], [Material("brick/wall", "brick/wall")], [Model("models/m.mdl")])
            with { Maps = [well, fort] };

        database.ReplaceGame(import, CancellationToken.None);

        Assert.Equal(2, Assert.Single(database.Games()).Maps);
        Assert.Equal([["brick/wall", "2", "613"]], Rows(database, "SELECT material, maps_used, area_used FROM material_view"));
        Assert.Equal([["models/m.mdl", "1", "4"]], Rows(database, "SELECT model, maps_used, placements FROM model_view"));
        Assert.Equal(
            [["cp_well", "brick/wall", "face", "4"], ["cp_well", "decals/x", "decal", "1"], ["ctf_2fort", "brick/wall", "face", "1"]],
            Rows(database, "SELECT map, material, kind, count FROM material_usage ORDER BY map, material"));
        Assert.Equal([["sky_well_01"]], Rows(database, "SELECT DISTINCT sky_name FROM maps"));

        database.ReplaceGame(import with { Maps = [fort] }, CancellationToken.None);
        Assert.Equal([["brick/wall", "1", "100"]], Rows(database, "SELECT material, maps_used, area_used FROM material_view"));
        Assert.Equal("0", Rows(database, "SELECT count(*) FROM model_usage")[0][0]);
    }

    [Fact]
    public void QueriesBindParametersAndKeepNulls()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");

        (IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows) = database.Query(
            "SELECT $a AS a, NULL AS b, 1.5 AS c", ("$a", "x"));

        Assert.Equal(["a", "b", "c"], columns);
        Assert.Equal([["x", null, "1.5"]], rows);
    }

    [Fact]
    public void MaterialParametersAreQueryableAsJson()
    {
        using AssetDatabase database = AssetDatabase.Open(":memory:");
        database.ReplaceGame(
            Import("hl2", materials: [Material("a/b") with { Parameters = """{"$basetexture":"a/b"}""" }]),
            CancellationToken.None);

        Assert.Equal(
            [["a/b"]],
            Rows(database, """SELECT json_extract(parameters, '$."$basetexture"') FROM materials"""));
    }
}

/// <summary>The reuse index: lookups by identity and the image-location rule.</summary>
public sealed class AssetIndexTests
{
    private static AssetIndex<TextureRecord> Index() =>
        new([AssetDatabaseTests.Texture("a/b", "1234567890")], t => t.Path, t => t.Sha256, t => t.Image!);

    [Fact]
    public void FindIsByPathAndHashTogether()
    {
        AssetIndex<TextureRecord> index = Index();

        Assert.NotNull(index.Find("a/b", "1234567890"));
        Assert.Null(index.Find("a/b", "other"));
        Assert.Null(index.Find("a/c", "1234567890"));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void AFreeLocationIsUsedAsItIsAndATakenOneGetsTheHashPrefix()
    {
        AssetIndex<TextureRecord> index = new(
            [AssetDatabaseTests.Texture("a/b", image: "textures/a/b")], t => t.Path, t => t.Sha256, t => t.Image!);

        Assert.Equal("textures/c/d", index.ImageLocation("textures/c/d", "ffffffffff"));
        Assert.Equal("textures/a/b~abcdef01", index.ImageLocation("textures/a/b", "abcdef0123"));
        Assert.Equal("textures/a/b~ab", index.ImageLocation("textures/a/b", "ab"));
    }

    [Fact]
    public void TheEmptyIndexKnowsNothing()
    {
        AssetIndex<TextureRecord> empty = AssetIndex<TextureRecord>.Empty();

        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Find(string.Empty, string.Empty));
    }
}
