//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.AssetDb.Materials;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The material reader: the fields an agent filters on, patch materials read
/// through their include, and the usage classification.
/// </summary>
public sealed class MaterialReaderTests
{
    private static async Task<(IReadOnlyList<MaterialRecord> Materials, IReadOnlyList<ImportIssue> Issues)> ReadAsync(
        AssetTestGame game) =>
        await MaterialReader.ReadAllAsync(game.Mount(), 2, CancellationToken.None);

    private static async Task<MaterialRecord> OneAsync(string name, string text)
    {
        (IReadOnlyList<MaterialRecord> materials, IReadOnlyList<ImportIssue> issues) =
            await ReadAsync(new AssetTestGame().AddMaterial(name, text));
        Assert.Empty(issues);
        return Assert.Single(materials);
    }

    [Fact]
    public async Task AnOrdinaryMaterialCarriesItsVarsNormalised()
    {
        MaterialRecord m = await OneAsync("Brick/BrickWall001A", """
            "LightmappedGeneric"
            {
                "$basetexture" "Brick\BrickWall001A.vtf"
                "$bumpmap" "materials/brick/brickwall001a_normal"
                "$detail" "detail/noise"
                "$envmap" "env_cubemap"
                "$surfaceprop" "brick"
                "%keywords" "wall, red"
                "$translucent" 1
                "$alphatest" "0"
                "$selfillum" "false"
            }
            """);

        Assert.Equal("brick/brickwall001a", m.Path);
        Assert.Equal("lightmappedgeneric", m.Shader);
        Assert.Equal(MaterialUsage.Brush, m.Usage);
        Assert.Equal("brick", m.Category);
        Assert.Equal("brick/brickwall001a", m.BaseTexture);
        Assert.Equal("brick/brickwall001a_normal", m.BumpMap);
        Assert.Equal("detail/noise", m.Detail);
        Assert.Equal("env_cubemap", m.EnvMap);
        Assert.Equal("brick", m.SurfaceProp);
        Assert.Equal("wall, red", m.Keywords);
        Assert.True(m.Translucent);
        Assert.False(m.AlphaTest);
        Assert.False(m.SelfIllum);
        Assert.Null(m.Include);
        Assert.Equal("brick/brickwall001a", m.PreviewTexture);
        Assert.Contains("LightmappedGeneric", m.Vmt, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(m.Source));
    }

    [Fact]
    public async Task ANormalMapStandsInForABumpMap()
    {
        MaterialRecord m = await OneAsync("a/b", "VertexLitGeneric { $normalmap a/b_n }");

        Assert.Equal("a/b_n", m.BumpMap);
    }

    [Fact]
    public async Task ABlankTextureVarIsNullAndTheToolTextureIsThePreview()
    {
        MaterialRecord m = await OneAsync("liquids/water", """
            Water { $basetexture "" "%tooltexture" "dev/water_normal" }
            """);

        Assert.Null(m.BaseTexture);
        Assert.Equal("dev/water_normal", m.ToolTexture);
        Assert.Equal("dev/water_normal", m.PreviewTexture);
        Assert.Equal(MaterialUsage.Water, m.Usage);
    }

    [Fact]
    public async Task ParametersAreTheTopLevelScalarsSortedLowerCasedLastOneWinning()
    {
        MaterialRecord m = await OneAsync("a/b", """
            LightmappedGeneric
            {
                $SurfaceProp metal
                $basetexture a/b
                $basetexture a/c
                Proxies { AnimatedTexture { animatedtexturevar $basetexture } }
            }
            """);

        Assert.Equal("""{"$basetexture":"a/c","$surfaceprop":"metal"}""", m.Parameters);
    }

    [Fact]
    public async Task APatchIsReadThroughItsInclude()
    {
        (IReadOnlyList<MaterialRecord> materials, IReadOnlyList<ImportIssue> issues) = await ReadAsync(new AssetTestGame()
            .AddMaterial("base/wood", "LightmappedGeneric { $basetexture base/wood $surfaceprop wood }")
            .AddMaterial("maps/thing/wood_patched", """
                patch
                {
                    include "materials/base/wood.vmt"
                    insert { $envmap env_cubemap }
                }
                """));

        Assert.Empty(issues);
        MaterialRecord patched = materials.Single(m => m.Path == "maps/thing/wood_patched");
        Assert.Equal("lightmappedgeneric", patched.Shader);
        Assert.Equal("base/wood", patched.BaseTexture);
        Assert.Equal("env_cubemap", patched.EnvMap);
        Assert.Equal("base/wood", patched.Include);
    }

    [Fact]
    public async Task APatchWhoseIncludeIsMissingIsAnIssueAndTheRestStillRead()
    {
        (IReadOnlyList<MaterialRecord> materials, IReadOnlyList<ImportIssue> issues) = await ReadAsync(new AssetTestGame()
            .AddMaterial("a/broken", """patch { include "materials/nowhere.vmt" }""")
            .AddMaterial("a/fine", "UnlitGeneric { $basetexture a/fine }"));

        Assert.Equal("a/fine", Assert.Single(materials).Path);
        ImportIssue issue = Assert.Single(issues);
        Assert.Equal(("material", "materials/a/broken.vmt"), (issue.Kind, issue.Path));
    }

    [Fact]
    public async Task AMaterialWithNoRootSectionIsAnIssue()
    {
        // The KeyValues reader is as forgiving as the engine's (a truncated
        // block still reads); a file with no section at all is what fails.
        (IReadOnlyList<MaterialRecord> materials, IReadOnlyList<ImportIssue> issues) = await ReadAsync(
            new AssetTestGame().AddMaterial("a/bad", "// nothing but a comment"));

        Assert.Empty(materials);
        Assert.Contains("no root section", Assert.Single(issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordsComeBackSortedByPath()
    {
        (IReadOnlyList<MaterialRecord> materials, _) = await ReadAsync(new AssetTestGame()
            .AddMaterial("z/last", "UnlitGeneric {}")
            .AddMaterial("a/first", "UnlitGeneric {}")
            .AddMaterial("m/middle", "UnlitGeneric {}"));

        Assert.Equal(["a/first", "m/middle", "z/last"], materials.Select(m => m.Path));
    }

    [Fact]
    public async Task AFileTheMountCannotReadAnyMoreIsSkippedQuietly()
    {
        AssetTestGame game = new AssetTestGame().AddMaterial("a/b", "UnlitGeneric {}");
        ContentFileSystem content = game.Mount();

        Assert.Null(await MaterialReader.ReadOneAsync(content, VPath.Create("materials/a/gone.vmt"), CancellationToken.None));
    }

    [Theory]
    [InlineData("tools/toolsnodraw", "lightmappedgeneric", "", MaterialUsage.Tool)]
    [InlineData("skybox/sky_day01_01up", "unlitgeneric", "", MaterialUsage.Sky)]
    [InlineData("nature/water_canals", "water", "", MaterialUsage.Water)]
    [InlineData("nature/water_canals", "sdk_water", "", MaterialUsage.Water)]
    [InlineData("decals/bloodstain", "lightmappedgeneric", "", MaterialUsage.Decal)]
    [InlineData("signs/sign01", "lightmappedgeneric", "$decal 1", MaterialUsage.Decal)]
    [InlineData("concrete/floor", "lightmappedgeneric", "", MaterialUsage.Brush)]
    [InlineData("concrete/floor", "sdk_lightmappedgeneric", "", MaterialUsage.Brush)]
    [InlineData("nature/blend", "worldvertextransition", "", MaterialUsage.Brush)]
    [InlineData("props/crate", "vertexlitgeneric", "", MaterialUsage.Model)]
    [InlineData("models/props/glow", "unlitgeneric", "", MaterialUsage.Model)]
    [InlineData("sprites/light_glow", "sprite", "", MaterialUsage.Other)]
    [InlineData("signs/sign01", "lightmappedgeneric", "$decal 0", MaterialUsage.Brush)]
    public async Task UsageFollowsDirectoryThenShader(string path, string shader, string vars, string expected)
    {
        VmtDocument document = await VmtDocument.ParseAsync($"{shader} {{ {vars} }}");

        Assert.Equal(expected, MaterialReader.Classify(path, shader, document.Root));
    }

    [Theory]
    [InlineData("Materials/Brick/Wall.VMT", "brick/wall")]
    [InlineData("/materials\\brick\\wall.vmt", "brick/wall")]
    [InlineData(" brick/wall ", "brick/wall")]
    public void MaterialNamesAreFoldedAndStripped(string value, string expected) =>
        Assert.Equal(expected, MaterialReader.MaterialName(value));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("materials/.vtf", null)]
    [InlineData("Metal/Floor.vtf", "metal/floor")]
    public void TexturePathsAreNullForPlaceholders(string? value, string? expected) =>
        Assert.Equal(expected, MaterialReader.TexturePath(value));
}
