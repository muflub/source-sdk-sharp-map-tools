//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.AssetDb.Materials;
using SourceSharp.AssetDb.Models;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The model export over a fake game: what lands on disk for a whole model,
/// which models are selected, what a missing satellite costs, skins, reuse
/// and order independence.
/// </summary>
public sealed class ModelExporterTests
{
    private static async Task<(IReadOnlyList<ModelRecord> Models, IReadOnlyList<ImportIssue> Issues, InMemoryFileSystem Output)> ExportAsync(
        AssetTestGame game,
        AssetExportOptions? options = null,
        AssetIndex<KnownModel>? known = null,
        InMemoryFileSystem? output = null)
    {
        ContentFileSystem content = game.Mount();
        (IReadOnlyList<MaterialRecord> materials, _) = await MaterialReader.ReadAllAsync(content, 2, CancellationToken.None);
        output ??= new InMemoryFileSystem();
        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues) = await ModelExporter.ExportAsync(
            content,
            output,
            known ?? AssetIndex<KnownModel>.Empty(),
            materials.ToDictionary(m => m.Path, StringComparer.Ordinal),
            options ?? AssetTestGame.Options(),
            CancellationToken.None);
        return (models, issues, output);
    }

    private static AssetTestGame CrateGame() => new AssetTestGame()
        .AddTexture("models/props/crate", 4, 4, (200, 0, 0, 255))
        .AddMaterial("models/props/crate", "VertexLitGeneric { $basetexture models/props/crate }")
        .AddModel("props/Crate01", AssetTestGame.CubeModel(), vtxExtension: "dx90.vtx");

    [Fact]
    public async Task AWholeModelWritesSixViewsAndASheetBesideItsRecord()
    {
        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues, InMemoryFileSystem output) =
            await ExportAsync(CrateGame());

        Assert.Empty(issues);
        ModelRecord record = Assert.Single(models);
        Assert.Equal("models/props/crate01.mdl", record.Path);
        Assert.Equal("models/props/crate01", record.ImageDirectory);
        foreach (ModelView view in Enum.GetValues<ModelView>())
        {
            Assert.True(await output.ExistsAsync(VPath.Create(record.ImageFor(view))), $"{view} missing");
        }

        Assert.Equal("models/props/crate01/sheet.png", record.SheetImage);
        Assert.True(await output.ExistsAsync(VPath.Create(record.SheetImage)));
    }

    [Fact]
    public async Task TheRecordDescribesTheModel()
    {
        ModelRecord record = Assert.Single((await ExportAsync(CrateGame())).Models);

        Assert.Equal("props/cube.mdl", record.Name);
        Assert.True(record.IsStaticProp);
        Assert.Equal("metal", record.SurfaceProp);
        Assert.Equal(25f, record.Mass);
        Assert.Equal((new Vec3(-8, -8, 0), new Vec3(8, 8, 16)), record.Mesh);
        Assert.Equal((new Vec3(-64, -64, 0), new Vec3(64, 64, 64)), record.Hull);
        Assert.Equal(12, record.Triangles);
        Assert.Equal(8, record.Vertices);
        Assert.Equal(1, record.BodyParts);
        Assert.Equal(1, record.Lods);
        Assert.Equal(1, record.Skins);
        Assert.Equal(1, record.Bones);
        Assert.Equal(2, record.GridStep);
        Assert.False(record.HasCollision);
        Assert.Null(record.PropData);
        Assert.Equal(64, record.Sha256.Length);
        Assert.Equal(new ModelMaterial(0, 0, "crate", "models/props/crate"), Assert.Single(record.Materials));
    }

    [Fact]
    public async Task TheRenderIsSkinnedWithTheMaterialsTexture()
    {
        (IReadOnlyList<ModelRecord> models, _, InMemoryFileSystem output) = await ExportAsync(CrateGame());

        // The same cube without its material renders flat grey; the pixel
        // values themselves are the renderer facts' business.
        byte[] png = output.GetBytes(VPath.Create(models[0].ImageFor(ModelView.Front)))!;

        (IReadOnlyList<ModelRecord> untextured, _, InMemoryFileSystem plain) = await ExportAsync(
            new AssetTestGame().AddModel("props/crate01", AssetTestGame.CubeModel()));
        Assert.NotEqual(png, plain.GetBytes(VPath.Create(untextured[0].ImageFor(ModelView.Front))));
    }

    [Fact]
    public async Task CollisionAndPropDataAreRead()
    {
        AssetTestGame game = new AssetTestGame()
            .AddModel("props/barrel", AssetTestGame.CubeModel(keyValues: """mdlkeyvalue { prop_data { base "Metal.Medium" } }"""))
            .AddRaw("models/props/barrel.phy", [0]);

        ModelRecord record = Assert.Single((await ExportAsync(game)).Models);

        Assert.True(record.HasCollision);
        Assert.Equal("Metal.Medium", record.PropData);
        Assert.Contains("prop_data", record.KeyValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedKeyValuesLeavePropDataUnknown()
    {
        AssetTestGame game = new AssetTestGame()
            .AddModel("props/barrel", AssetTestGame.CubeModel(keyValues: "mdlkeyvalue { prop_data { base "));

        ModelRecord record = Assert.Single((await ExportAsync(game)).Models);

        Assert.Null(record.PropData);
    }

    [Fact]
    public async Task PropsSelectionKeepsStaticPropsAndPropsDirectoriesOnly()
    {
        AssetTestGame game = new AssetTestGame()
            .AddModel("props_c17/lamp", AssetTestGame.CubeModel(flags: 0))
            .AddModel("weapons/w_gun", AssetTestGame.CubeModel(flags: 0))
            .AddModel("odd/staticthing", AssetTestGame.CubeModel(flags: ModelRecord.StaticPropFlag));

        IReadOnlyList<ModelRecord> props = (await ExportAsync(game)).Models;
        IReadOnlyList<ModelRecord> all = (await ExportAsync(game, AssetTestGame.Options(ModelSelection.All))).Models;
        IReadOnlyList<ModelRecord> none = (await ExportAsync(game, AssetTestGame.Options(ModelSelection.None))).Models;

        Assert.Equal(["models/odd/staticthing.mdl", "models/props_c17/lamp.mdl"], props.Select(m => m.Path));
        Assert.Equal(3, all.Count);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData("models/props_c17/lamp.mdl", 0, true)]
    [InlineData("models/props/a.mdl", 0, true)]
    [InlineData("models/weapons/w_gun.mdl", 0, false)]
    [InlineData("models/weapons/w_gun.mdl", 0x10, true)]
    [InlineData("models/player/props_hat.mdl", 0, true)]
    public void IsPropReadsTheFlagAndThePath(string path, int flags, bool expected) =>
        Assert.Equal(expected, ModelExporter.IsProp(path, flags));

    [Theory]
    [InlineData("models/props/crate.mdl", "models/props/crate")]
    [InlineData("props/crate.mdl", "models/props/crate")]
    [InlineData("models/a/b", "models/a/b")]
    public void TheNaturalImageDirectoryMirrorsThePath(string path, string expected) =>
        Assert.Equal(expected, ModelExporter.NaturalImageDirectory(path));

    [Fact]
    public async Task AMissingVvdIsAnIssue()
    {
        AssetTestGame game = new AssetTestGame().AddModel("props/crate", AssetTestGame.CubeModel(), withVvd: false);

        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(game);

        Assert.Empty(models);
        Assert.Contains(".vvd", Assert.Single(issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingVtxIsAnIssue()
    {
        AssetTestGame game = new AssetTestGame().AddModel("props/crate", AssetTestGame.CubeModel(), withVtx: false);

        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(game);

        Assert.Empty(models);
        Assert.Contains(".vtx", Assert.Single(issues).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dx80.vtx")]
    [InlineData("vtx")]
    [InlineData("sw.vtx")]
    public async Task AnyVtxFlavourIsEnough(string extension)
    {
        AssetTestGame game = new AssetTestGame().AddModel("props/crate", AssetTestGame.CubeModel(), vtxExtension: extension);

        Assert.Single((await ExportAsync(game)).Models);
    }

    [Fact]
    public async Task AnUnreadableModelIsAnIssue()
    {
        AssetTestGame game = new AssetTestGame().AddRaw("models/props/bad.mdl", new byte[512]);

        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(game);

        Assert.Empty(models);
        Assert.Contains("not a readable model", Assert.Single(issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelWithNothingToDrawIsAnIssue()
    {
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(AssetTestGame.Cube, []);
        AssetTestGame game = new AssetTestGame().AddModel("props/empty", (mdl, vvd, vtx));

        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(game);

        Assert.Empty(models);
        Assert.Contains("no level-of-detail-zero triangles", Assert.Single(issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderModelVersionsTheEngineDrawsAreExported()
    {
        AssetTestGame game = new AssetTestGame().AddModel("props/old", AssetTestGame.CubeModel(version: 44));

        (IReadOnlyList<ModelRecord> models, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(game);

        Assert.Empty(issues);
        Assert.Single(models);
    }

    [Fact]
    public async Task EverySkinFamilysSlotsAreRecorded()
    {
        AssetTestGame game = new AssetTestGame()
            .AddMaterial("models/props/red", "VertexLitGeneric { $basetexture x }")
            .AddMaterial("models/props/blue", "VertexLitGeneric { $basetexture y }")
            .AddModel("props/skinned", AssetTestGame.CubeModel(
                skinFamilies: 2, skinRefs: 2, textureNames: ["red", "blue"]));

        ModelRecord record = Assert.Single((await ExportAsync(game)).Models);

        Assert.Equal(2, record.Skins);
        Assert.Equal(
            [
                new ModelMaterial(0, 0, "red", "models/props/red"),
                new ModelMaterial(0, 1, "blue", "models/props/blue"),
                new ModelMaterial(1, 0, "blue", "models/props/blue"),
                new ModelMaterial(1, 1, "red", "models/props/red"),
            ],
            record.Materials);
    }

    [Fact]
    public void MaterialsResolveThroughTheSearchPathsInOrderThenDirectly()
    {
        Dictionary<string, MaterialRecord> materials = new(StringComparer.Ordinal)
        {
            ["models/second/body"] = Material("models/second/body"),
            ["direct/body"] = Material("direct/body"),
        };

        Assert.Equal("models/second/body", ModelExporter.ResolveMaterial("Body", ["models\\first\\", "models/second"], materials));
        Assert.Equal("direct/body", ModelExporter.ResolveMaterial("direct/body", ["models/first/"], materials));
        Assert.Null(ModelExporter.ResolveMaterial("missing", ["models/first/"], materials));
    }

    [Fact]
    public async Task AKnownModelWithItsSheetOnDiskIsNotRenderedAgain()
    {
        AssetTestGame game = CrateGame();
        ModelRecord first = Assert.Single((await ExportAsync(game)).Models);
        InMemoryFileSystem output = new InMemoryFileSystem().AddFile(first.SheetImage, [7]);
        AssetIndex<KnownModel> known = ModelExporter.Index([new KnownModel(first.Path, first.Sha256, first.ImageDirectory)]);

        ModelRecord second = Assert.Single((await ExportAsync(game, known: known, output: output)).Models);

        Assert.Equal([7], output.GetBytes(VPath.Create(first.SheetImage)));
        Assert.False(await output.ExistsAsync(VPath.Create(first.ImageFor(ModelView.Front))));
        Assert.Equal(first.Sha256, second.Sha256);

        // Force renders regardless.
        await ExportAsync(game, AssetTestGame.Options() with { Force = true }, known, output);
        Assert.NotEqual([7], output.GetBytes(VPath.Create(first.SheetImage)));
    }

    [Fact]
    public async Task DifferentContentAtAKnownPathRendersIntoASuffixedDirectory()
    {
        AssetIndex<KnownModel> known = ModelExporter.Index(
            [new KnownModel("models/props/crate01.mdl", "other", "models/props/crate01")]);

        ModelRecord record = Assert.Single((await ExportAsync(CrateGame(), known: known)).Models);

        Assert.Equal($"models/props/crate01~{record.Sha256[..8]}", record.ImageDirectory);
    }

    [Fact]
    public async Task TheExportIsDeterministicAndSortedByPath()
    {
        AssetTestGame game = CrateGame()
            .AddModel("props/a_first", AssetTestGame.CubeModel())
            .AddModel("props/z_last", AssetTestGame.CubeModel());

        (IReadOnlyList<ModelRecord> a, _, InMemoryFileSystem ao) = await ExportAsync(game);
        (IReadOnlyList<ModelRecord> b, _, InMemoryFileSystem bo) = await ExportAsync(game);

        Assert.Equal(["models/props/a_first.mdl", "models/props/crate01.mdl", "models/props/z_last.mdl"], a.Select(m => m.Path));
        Assert.Equal(a.Select(m => (m.Path, m.Sha256)), b.Select(m => (m.Path, m.Sha256)));
        Assert.Equal(ao.GetBytes(VPath.Create(a[1].SheetImage)), bo.GetBytes(VPath.Create(b[1].SheetImage)));
    }

    [Fact]
    public async Task CancellationStopsTheExport()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelExporter.ExportAsync(
            CrateGame().Mount(), new InMemoryFileSystem(), AssetIndex<KnownModel>.Empty(),
            new Dictionary<string, MaterialRecord>(), AssetTestGame.Options(), cancelled.Token));
    }

    private static MaterialRecord Material(string path) => new()
    {
        Path = path,
        Shader = "vertexlitgeneric",
        Usage = MaterialUsage.Model,
        Category = "models",
        Parameters = "{}",
        Vmt = string.Empty,
        Source = string.Empty,
    };
}
