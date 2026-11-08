//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.AssetDb.Textures;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The texture export over a fake game: PNGs where the contract puts them,
/// records that say what the VTF said, reuse by hash, and issues that record
/// instead of throw.
/// </summary>
public sealed class TextureExporterTests
{
    private static async Task<(IReadOnlyList<TextureRecord> Textures, IReadOnlyList<ImportIssue> Issues, InMemoryFileSystem Output)> ExportAsync(
        AssetTestGame game,
        AssetIndex<TextureRecord>? known = null,
        AssetExportOptions? options = null,
        InMemoryFileSystem? output = null)
    {
        output ??= new InMemoryFileSystem();
        (IReadOnlyList<TextureRecord> textures, IReadOnlyList<ImportIssue> issues) = await TextureExporter.ExportAsync(
            game.Mount(),
            output,
            known ?? AssetIndex<TextureRecord>.Empty(),
            options ?? AssetTestGame.Options(),
            CancellationToken.None);
        return (textures, issues, output);
    }

    private static AssetIndex<TextureRecord> Index(params TextureRecord[] records) =>
        TextureExporter.Index(records);

    [Fact]
    public async Task WritesAPngPerTextureUnderTexturesWithItsHeaderFacts()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("Metal/Wall", 32, 16, flags: 0x4);

        (IReadOnlyList<TextureRecord> textures, IReadOnlyList<ImportIssue> issues, InMemoryFileSystem output) =
            await ExportAsync(game);

        Assert.Empty(issues);
        TextureRecord record = Assert.Single(textures);
        Assert.Equal("metal/wall", record.Path);
        Assert.Equal("textures/metal/wall.png", record.Image);
        Assert.Equal(32, record.Width);
        Assert.Equal(16, record.Height);
        Assert.Equal(nameof(ImageFormat.Bgra8888), record.Format);
        Assert.Equal("ClampS", record.Flags);
        Assert.Equal(64, record.Sha256.Length);
        Assert.True(await output.ExistsAsync(VPath.Create("textures/metal/wall.png")));
    }

    [Fact]
    public async Task ThePngIsTheDecodedMipThroughTheEncoderAndTheStatsDescribeIt()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("plain/flat", 8, 8, (12, 240, 3, 128));

        (IReadOnlyList<TextureRecord> textures, _, InMemoryFileSystem output) = await ExportAsync(game);

        byte[] expected = RgbaImage.Filled(8, 8, new Rgba(12, 240, 3, 128)).ToPng();
        Assert.Equal(expected, output.GetBytes(VPath.Create("textures/plain/flat.png")));
        TextureRecord record = Assert.Single(textures);
        Assert.Equal("#0cf003", record.AverageColour);
        Assert.True(record.HasAlpha);
    }

    [Fact]
    public async Task TheImageIsTheLargestStoredMipWithinTheSizeLimit()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("big/one", 64, 32);

        (IReadOnlyList<TextureRecord> textures, _, _) = await ExportAsync(
            game, options: AssetTestGame.Options() with { MaxTextureSize = 16 });

        TextureRecord record = Assert.Single(textures);
        Assert.Equal((64, 32), (record.Width, record.Height));
        Assert.Equal((16, 8), (record.ImageWidth, record.ImageHeight));
    }

    [Fact]
    public async Task ASizeLimitOfZeroWritesTheTopMip()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("big/one", 64, 32);

        (IReadOnlyList<TextureRecord> textures, _, _) = await ExportAsync(
            game, options: AssetTestGame.Options() with { MaxTextureSize = 0 });

        Assert.Equal((64, 32), (textures[0].ImageWidth, textures[0].ImageHeight));
    }

    [Fact]
    public async Task AnUndecodableFormatIsAnIssueAndWritesNothing()
    {
        AssetTestGame game = new AssetTestGame().AddUndecodableTexture("hdr/sky");

        (IReadOnlyList<TextureRecord> textures, IReadOnlyList<ImportIssue> issues, InMemoryFileSystem output) =
            await ExportAsync(game);

        Assert.Empty(textures);
        ImportIssue issue = Assert.Single(issues);
        Assert.Equal("texture", issue.Kind);
        Assert.Contains("Rgba16161616F", issue.Reason, StringComparison.Ordinal);
        Assert.False(await output.ExistsAsync(VPath.Create("textures/hdr/sky.png")));
    }

    [Fact]
    public async Task ACorruptFileIsAnIssueAndTheRunContinues()
    {
        AssetTestGame game = new AssetTestGame()
            .AddRaw("materials/bad/garbage.vtf", [1, 2, 3, 4])
            .AddTexture("good/one", 4, 4);

        (IReadOnlyList<TextureRecord> textures, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(game);

        Assert.Equal("good/one", Assert.Single(textures).Path);
        Assert.Equal("materials/bad/garbage.vtf", Assert.Single(issues).Path);
    }

    [Fact]
    public async Task AKnownTextureWithItsImageOnDiskIsReusedNotRewritten()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("a/one", 8, 8);
        (IReadOnlyList<TextureRecord> first, _, _) = await ExportAsync(game);

        // The image exists, but with different bytes: a reuse leaves it alone.
        InMemoryFileSystem output = new InMemoryFileSystem().AddFile("textures/a/one.png", [9]);
        (IReadOnlyList<TextureRecord> second, _, _) = await ExportAsync(
            game, Index(first[0] with { Source = "elsewhere" }), output: output);

        Assert.Equal([9], output.GetBytes(VPath.Create("textures/a/one.png")));
        Assert.Equal(first[0] with { Source = second[0].Source }, second[0]);
    }

    [Fact]
    public async Task AKnownTextureWhoseImageIsGoneIsDecodedAgain()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("a/one", 8, 8);
        (IReadOnlyList<TextureRecord> first, _, _) = await ExportAsync(game);

        (_, _, InMemoryFileSystem output) = await ExportAsync(game, Index(first[0]));

        Assert.True(await output.ExistsAsync(VPath.Create("textures/a/one.png")));
    }

    [Fact]
    public async Task ForceDecodesAKnownTextureAgain()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("a/one", 8, 8);
        (IReadOnlyList<TextureRecord> first, _, _) = await ExportAsync(game);
        InMemoryFileSystem output = new InMemoryFileSystem().AddFile("textures/a/one.png", [9]);

        await ExportAsync(game, Index(first[0]), AssetTestGame.Options() with { Force = true }, output);

        Assert.NotEqual([9], output.GetBytes(VPath.Create("textures/a/one.png")));
    }

    [Fact]
    public async Task DifferentContentAtAKnownPathGetsAHashSuffixedImage()
    {
        (IReadOnlyList<TextureRecord> other, _, _) = await ExportAsync(
            new AssetTestGame().AddTexture("a/one", 8, 8, (1, 1, 1, 255)));
        AssetTestGame game = new AssetTestGame().AddTexture("a/one", 8, 8, (2, 2, 2, 255));

        (IReadOnlyList<TextureRecord> textures, _, InMemoryFileSystem output) = await ExportAsync(game, Index(other[0]));

        TextureRecord record = Assert.Single(textures);
        Assert.Equal($"textures/a/one~{record.Sha256[..8]}.png", record.Image);
        Assert.True(await output.ExistsAsync(VPath.Create(record.Image)));
    }

    [Fact]
    public async Task TwoRunsProduceIdenticalRecordsAndBytes()
    {
        AssetTestGame game = new AssetTestGame()
            .AddTexture("b/two", 16, 16, (5, 6, 7, 255))
            .AddTexture("a/one", 8, 4)
            .AddUndecodableTexture("c/three");

        (IReadOnlyList<TextureRecord> a, IReadOnlyList<ImportIssue> ai, InMemoryFileSystem ao) = await ExportAsync(game);
        (IReadOnlyList<TextureRecord> b, IReadOnlyList<ImportIssue> bi, InMemoryFileSystem bo) = await ExportAsync(game);

        Assert.Equal(a, b);
        Assert.Equal(ai, bi);
        Assert.Equal(["a/one", "b/two"], a.Select(t => t.Path));
        Assert.Equal(ao.GetBytes(VPath.Create("textures/b/two.png")), bo.GetBytes(VPath.Create("textures/b/two.png")));
    }

    [Fact]
    public async Task AnEmptyGameExportsNothing()
    {
        (IReadOnlyList<TextureRecord> textures, IReadOnlyList<ImportIssue> issues, _) = await ExportAsync(new AssetTestGame());

        Assert.Empty(textures);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task CancellationStopsTheExport()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("a/one", 8, 8);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TextureExporter.ExportAsync(
            game.Mount(), new InMemoryFileSystem(), AssetIndex<TextureRecord>.Empty(), AssetTestGame.Options(), cancelled.Token));
    }

    [Fact]
    public async Task ProgressCountsEveryTexture()
    {
        AssetTestGame game = new AssetTestGame().AddTexture("a/one", 4, 4).AddTexture("a/two", 4, 4);
        List<ImportProgress> reports = [];
        Progress progress = new(reports);

        await ExportAsync(game, options: AssetTestGame.Options() with { Progress = progress });

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(("textures", 2), (r.Stage, r.Total)));
        Assert.Contains(reports, r => r.Done == 2);
    }

    [Fact]
    public void FlagNamesSpellKnownBitsAndHexTheRest()
    {
        Assert.Equal(string.Empty, TextureExporter.FlagNames(0));
        Assert.Equal("ClampS,ClampT", TextureExporter.FlagNames(0x4 | 0x8));
        Assert.Equal("0x80000000", TextureExporter.FlagNames(0x80000000));
    }

    [Theory]
    [InlineData("materials/Metal/Wall.vtf", "metal/wall")]
    [InlineData("materials\\a\\b.VTF", "a/b")]
    [InlineData("a/b", "a/b")]
    public void TextureNamesAreFoldedAndStripped(string contentPath, string expected) =>
        Assert.Equal(expected, TextureExporter.TextureName(contentPath));

    [Fact]
    public void ATruncatedTopMipFallsBackToTheLargestMipTheFileHolds()
    {
        // The writer stores the chain smallest first, so cutting the top
        // mip's bytes off the end leaves a file whose header still claims
        // 8x8 but whose 4x4 and smaller are intact.
        byte[] whole = VtfWriter.Write(8, 8, ImageFormat.Bgra8888, 0, (_, _) => (50, 60, 70, 255));
        byte[] truncated = whole[..^(8 * 8 * 4)];
        VtfFile texture = VtfFile.Parse(truncated);

        RgbaImage? image = TextureExporter.DecodeLargestPresent(texture, 0, out string? failure);

        Assert.NotNull(image);
        Assert.Equal((4, 4), (image.Width, image.Height));
        Assert.NotNull(failure);
    }

    [Fact]
    public void WhenNoMipDecodesThereIsNoImageAndAReason()
    {
        byte[] whole = VtfWriter.Write(2, 2, ImageFormat.Bgra8888, 0x100, (_, _) => (1, 2, 3, 255));
        VtfFile texture = VtfFile.Parse(whole[..^(2 * 2 * 4)]);

        Assert.Null(TextureExporter.DecodeLargestPresent(texture, 0, out string? failure));
        Assert.NotNull(failure);
    }

    /// <summary>A synchronous progress sink: <see cref="Progress{T}"/> posts to the thread pool.</summary>
    private sealed class Progress(List<ImportProgress> into) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value)
        {
            lock (into)
            {
                into.Add(value);
            }
        }
    }
}
