//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.AssetDb.Materials;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// Lit material previews: the lighting response of normal and self-shadowing
/// bump maps, which materials get one, and the render pass with reuse.
/// </summary>
public sealed class MaterialPreviewTests
{
    private static readonly Rgba Base = new(200, 100, 50);

    /// <summary>A flat normal map pixel: straight out of the surface.</summary>
    private static readonly Rgba FlatNormal = new(128, 128, 255);

    /// <summary>A flat self-shadowing bump pixel: each basis direction sees the surface's cosine, 1/sqrt(3).</summary>
    private static readonly Rgba FlatSsBump = new(147, 147, 147);

    private static Rgba LitPixel(Rgba bump, bool ssBump) =>
        MaterialPreview.Light(RgbaImage.Filled(2, 2, Base), RgbaImage.Filled(2, 2, bump), ssBump).Get(0, 0);

    /// <summary>Lights a two-pixel-wide base through a bump whose left and right pixels differ.</summary>
    private static (Rgba Left, Rgba Right) LitPair(Rgba left, Rgba right, bool ssBump)
    {
        RgbaImage lit = MaterialPreview.Light(
            RgbaImage.Filled(2, 1, Base),
            new RgbaImage(2, 1, [left.R, left.G, left.B, 255, right.R, right.G, right.B, 255]),
            ssBump);
        return (lit.Get(0, 0), lit.Get(1, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUniformBumpMapLeavesTheBaseTextureAsItIs(bool ssBump)
    {
        Rgba lit = LitPixel(ssBump ? FlatSsBump : FlatNormal, ssBump);

        Assert.Equal(Base, lit);
    }

    [Fact]
    public void AnSsBumpAtAnyRestingLevelKeepsTheBaseBrightness()
    {
        // A self-shadowing map authored around a low resting level must not
        // darken the material: only differences between pixels show.
        Assert.Equal(Base, LitPixel(new Rgba(90, 90, 90), ssBump: true));
    }

    [Fact]
    public void ASlopeFacingTheLightIsBrighterThanOneFacingAway()
    {
        // The light comes from the image's upper left, so a normal leaning up
        // and left (low red, low green) catches more of it.
        (Rgba toward, Rgba away) = LitPair(new Rgba(60, 60, 220), new Rgba(196, 196, 220), ssBump: false);

        Assert.True(toward.R > Base.R, $"facing the light should brighten, got {toward}");
        Assert.True(away.R < Base.R, $"facing away should darken, got {away}");
        Assert.True(away.R >= (byte)(Base.R * 0.3f) - 1, "the ambient term keeps a shadowed slope visible");
    }

    [Fact]
    public void AnSsBumpPixelLitOnlyFromTheFirstBasisIsDarkerThanAFlatOne()
    {
        // The first basis direction leans right, away from an upper-left light.
        (Rgba shadowed, Rgba flat) = LitPair(new Rgba(255, 0, 0), FlatSsBump, ssBump: true);

        Assert.True(shadowed.R < flat.R);
    }

    [Fact]
    public void TheBumpMapIsSampledAtTheBaseTexturesResolutionAndAlphaIsKept()
    {
        RgbaImage colour = RgbaImage.Filled(4, 4, new Rgba(100, 100, 100, 77));
        RgbaImage bump = new(2, 1, [60, 60, 220, 255, 196, 196, 220, 255]);

        RgbaImage lit = MaterialPreview.Light(colour, bump, ssBump: false);

        Assert.Equal((4, 4), (lit.Width, lit.Height));
        Assert.True(lit.Get(0, 3).R > lit.Get(3, 0).R, "the left half samples the bright pixel, the right the dark");
        Assert.Equal(77, lit.Get(2, 2).A);
    }

    [Fact]
    public void OnlyBrushMaterialsWithABaseAndABumpGetAPreview()
    {
        MaterialRecord brush = AssetDatabaseTests.Material("a/b", baseTexture: "a/b") with { BumpMap = "a/b_n" };

        Assert.True(MaterialPreview.Wants(brush));
        Assert.False(MaterialPreview.Wants(brush with { BumpMap = null }));
        Assert.False(MaterialPreview.Wants(brush with { BaseTexture = null }));
        Assert.False(MaterialPreview.Wants(brush with { Usage = MaterialUsage.Model }));
    }

    private static AssetTestGame BumpedGame(bool withBump = true)
    {
        AssetTestGame game = new AssetTestGame()
            .AddTexture("brick/wall", 8, 8, (200, 100, 50, 255))
            .AddMaterial("brick/wall", "LightmappedGeneric { $basetexture brick/wall $bumpmap brick/wall_normal }")
            .AddMaterial("brick/plain", "LightmappedGeneric { $basetexture brick/wall }");
        if (withBump)
        {
            game.AddTexture("brick/wall_normal", 8, 8, (60, 60, 220, 255), flags: 0x80);
        }

        return game;
    }

    private static async Task<(IReadOnlyList<PreviewRecord> Previews, IReadOnlyList<ImportIssue> Issues)> RenderAsync(
        AssetTestGame game,
        InMemoryFileSystem output,
        AssetIndex<PreviewRecord>? known = null,
        AssetExportOptions? options = null)
    {
        ContentFileSystem content = game.Mount();
        (IReadOnlyList<MaterialRecord> materials, _) = await MaterialReader.ReadAllAsync(content, 2, CancellationToken.None);
        return await MaterialPreview.RenderAsync(
            content, output, materials, known ?? AssetIndex<PreviewRecord>.Empty(), options ?? AssetTestGame.Options(), CancellationToken.None);
    }

    [Fact]
    public async Task TheRenderPassWritesAPreviewForEachBumpedBrushMaterial()
    {
        InMemoryFileSystem output = new();

        (IReadOnlyList<PreviewRecord> previews, IReadOnlyList<ImportIssue> issues) = await RenderAsync(BumpedGame(), output);

        Assert.Empty(issues);
        PreviewRecord preview = Assert.Single(previews);
        Assert.Equal(("brick/wall", "previews/brick/wall.png", 8, 8), (preview.Material, preview.Image, preview.Width, preview.Height));
        Assert.Equal(64, preview.Sha256.Length);
        Assert.True(await output.ExistsAsync(VPath.Create(preview.Image)));
    }

    [Fact]
    public async Task AMissingBumpMapIsAnIssueNotAFailure()
    {
        (IReadOnlyList<PreviewRecord> previews, IReadOnlyList<ImportIssue> issues) =
            await RenderAsync(BumpedGame(withBump: false), new InMemoryFileSystem());

        Assert.Empty(previews);
        ImportIssue issue = Assert.Single(issues);
        Assert.Equal(("preview", "brick/wall"), (issue.Kind, issue.Path));
        Assert.Contains("brick/wall_normal", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKnownPreviewOnDiskIsReusedAndForceRendersItAgain()
    {
        (IReadOnlyList<PreviewRecord> first, _) = await RenderAsync(BumpedGame(), new InMemoryFileSystem());
        InMemoryFileSystem output = new InMemoryFileSystem().AddFile(first[0].Image, [5]);
        AssetIndex<PreviewRecord> known = MaterialPreview.Index(first);

        (IReadOnlyList<PreviewRecord> reused, _) = await RenderAsync(BumpedGame(), output, known);
        Assert.Equal(first, reused);
        Assert.Equal([5], output.GetBytes(VPath.Create(first[0].Image)));

        await RenderAsync(BumpedGame(), output, known, AssetTestGame.Options() with { Force = true });
        Assert.NotEqual([5], output.GetBytes(VPath.Create(first[0].Image)));
    }

    [Fact]
    public async Task ADifferentBumpGivesADifferentHashAndASuffixedImage()
    {
        (IReadOnlyList<PreviewRecord> first, _) = await RenderAsync(BumpedGame(), new InMemoryFileSystem());
        AssetTestGame changed = BumpedGame(withBump: false).AddTexture("brick/wall_normal", 8, 8, (196, 196, 220, 255), flags: 0x80);

        (IReadOnlyList<PreviewRecord> second, _) = await RenderAsync(changed, new InMemoryFileSystem(), MaterialPreview.Index(first));

        Assert.NotEqual(first[0].Sha256, second[0].Sha256);
        Assert.Equal($"previews/brick/wall~{second[0].Sha256[..8]}.png", second[0].Image);
    }
}
