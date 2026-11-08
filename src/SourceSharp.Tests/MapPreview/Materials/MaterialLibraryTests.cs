//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Materials;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.Tests.MapFormats.Assets;
using Xunit;

namespace SourceSharp.Tests.MapPreview.Materials;

/// <summary>Facts for <see cref="PreviewMaterialLibrary"/> and <see cref="PreviewMaterial"/>.</summary>
public class MaterialLibraryTests
{
    /// <summary>A 4x4 RGBA texture with a 2x2 mip: the small mip red, the large one blue.</summary>
    private static byte[] Texture()
    {
        byte[] vtf = VtfTests.Synthetic(ImageFormat.Rgba8888, 4, 4, 2, false);
        int large = 4 * 4 * 4, small = 2 * 2 * 4;
        int start = vtf.Length - large - small;
        for (int i = 0; i < small; i += 4)
        {
            vtf[start + i] = 255;
            vtf[start + i + 3] = 255;
        }

        for (int i = 0; i < large; i += 4)
        {
            vtf[start + small + i + 2] = 255;
            vtf[start + small + i + 3] = 255;
        }

        return vtf;
    }

    private static async Task<ContentFileSystem> ContentAsync() => await PreviewTestMaps.ContentAsync(
        ("materials/t/stone.vmt", "\"LightmappedGeneric\" { \"$basetexture\" \"t/stone\" \"$color\" \"[1 0.5 0]\" }"u8.ToArray()),
        ("materials/t/stone.vtf", Texture()),
        ("materials/t/fence.vmt", "\"LightmappedGeneric\" { \"$basetexture\" \"t/stone\" \"$alphatest\" 1 \"$alphatestreference\" 0.3 \"$nocull\" 1 }"u8.ToArray()),
        ("materials/t/glass.vmt", "\"UnlitGeneric\" { \"$basetexture\" \"t/stone\" \"$translucent\" 1 \"$alpha\" 0.4 }"u8.ToArray()),
        ("materials/t/water.vmt", "\"Water\" { \"$fogcolor\" \"{20 40 60}\" }"u8.ToArray()),
        ("materials/t/flat.vmt", "\"LightmappedGeneric\" { \"$color\" \"[0 1 0]\" }"u8.ToArray()),
        ("materials/t/lost.vmt", "\"LightmappedGeneric\" { \"$basetexture\" \"t/nothing\" }"u8.ToArray()),
        ("materials/t/broken.vmt", "\"LightmappedGeneric\" { \"$basetexture\" \"t/broken\" }"u8.ToArray()),
        ("materials/t/broken.vtf", [1, 2, 3, 4]),
        ("materials/skybox/testrt.vmt", "\"UnlitGeneric\" { \"$basetexture\" \"t/stone\" }"u8.ToArray()));

    [Fact]
    public async Task AFoundMaterialDecodesItsBaseTextureAtTheLargestMipThatFits()
    {
        await using ContentFileSystem content = await ContentAsync();

        PreviewMaterial full = await new PreviewMaterialLibrary(content).ResolveAsync("t/stone");
        PreviewMaterial small = await new PreviewMaterialLibrary(content, maxTextureEdge: 2).ResolveAsync("T/Stone");

        Assert.Equal(MaterialProblem.None, full.Problem);
        Assert.Equal(4, full.Texture!.Width);
        Assert.Equal(255, full.Texture.Get(0, 0).B);
        Assert.Equal(2, small.Texture!.Width);
        Assert.Equal(255, small.Texture.Get(0, 0).R);
        Assert.Equal(new Rgba(255, 128, 0), full.Colour);
        Assert.Equal("lightmappedgeneric", full.Shader);
        Assert.False(full.Unlit);
    }

    [Fact]
    public async Task AlphaFlagsAndUnlitShadersAreRead()
    {
        await using ContentFileSystem content = await ContentAsync();
        PreviewMaterialLibrary library = new(content);

        PreviewMaterial fence = await library.ResolveAsync("t/fence");
        PreviewMaterial glass = await library.ResolveAsync("t/glass");

        Assert.Equal(MaterialOpacity.AlphaTest, fence.Opacity);
        Assert.Equal(0.3f, fence.AlphaReference, 4);
        Assert.True(fence.NoCull);
        Assert.Equal(MaterialOpacity.Translucent, glass.Opacity);
        Assert.Equal(0.4f, glass.Alpha, 4);
        Assert.True(glass.Unlit);
    }

    [Fact]
    public async Task WaterDrawsItsFogColourTranslucently()
    {
        await using ContentFileSystem content = await ContentAsync();

        PreviewMaterial water = await new PreviewMaterialLibrary(content).ResolveAsync("t/water");

        Assert.Null(water.Texture);
        Assert.Equal(new Rgba(20, 40, 60), water.Colour);
        Assert.Equal(MaterialOpacity.Translucent, water.Opacity);
        Assert.Equal(new Rgba(20, 40, 60), water.Sample(0.3f, 0.7f));
    }

    [Fact]
    public async Task AMaterialWithNoBaseTextureDrawsItsColour()
    {
        await using ContentFileSystem content = await ContentAsync();

        PreviewMaterial flat = await new PreviewMaterialLibrary(content).ResolveAsync("t/flat");

        Assert.Null(flat.Texture);
        Assert.Equal(new Rgba(0, 255, 0), flat.Colour);
        Assert.False(flat.IsMissing);
    }

    [Theory]
    [InlineData("t/absent", MaterialProblem.MissingMaterial)]
    [InlineData("t/lost", MaterialProblem.MissingTexture)]
    [InlineData("t/broken", MaterialProblem.UndecodableTexture)]
    [InlineData(BspSceneBuilder.MissingModelMaterial, MaterialProblem.MissingModel)]
    [InlineData(BspSceneBuilder.MissingSlotMaterial, MaterialProblem.MissingMaterial)]
    public async Task ProblemsDrawAsTheChecker(string name, MaterialProblem problem)
    {
        await using ContentFileSystem content = await ContentAsync();

        PreviewMaterial material = await new PreviewMaterialLibrary(content).ResolveAsync(name);

        Assert.Equal(problem, material.Problem);
        Assert.True(material.IsMissing);
        Assert.Equal(PreviewMaterial.CheckerA, material.Sample(0.1f, 0.1f));
        Assert.Equal(PreviewMaterial.CheckerB, material.Sample(0.6f, 0.1f));
    }

    [Fact]
    public async Task ResolvedMaterialsAreCachedAndListsKeepTheirOrder()
    {
        await using ContentFileSystem content = await ContentAsync();
        PreviewMaterialLibrary library = new(content);

        PreviewMaterial[] list = await library.ResolveAsync(["t/flat", "t/stone", "t/flat"]);

        Assert.Same(list[0], list[2]);
        Assert.Equal("t/stone", list[1].Name);
    }

    [Fact]
    public async Task TheSkyLoadsTheFacesItHasAndAveragesThemForTheRest()
    {
        await using ContentFileSystem content = await ContentAsync();
        PreviewMaterialLibrary library = new(content);

        PreviewSky sky = await library.LoadSkyAsync("test");
        PreviewSky none = await library.LoadSkyAsync(null);

        Assert.NotNull(sky.Faces[0]);
        Assert.All(sky.Faces.Skip(1), f => Assert.Null(f));
        Assert.Equal(new Rgba(0, 0, 255), sky.Fallback);
        Assert.All(none.Faces, f => Assert.Null(f));
        Assert.Equal(new Rgba(150, 180, 215), none.Fallback);
    }

    [Fact]
    public void SamplingWrapsBothWays()
    {
        PreviewMaterial material = PreviewTestMaps.Flat(new Rgba(1, 2, 3)) with { Texture = RgbaImage.Filled(2, 2, new Rgba(9, 9, 9)) };
        material.Texture!.Set(1, 1, new Rgba(7, 7, 7));

        Assert.Equal(new Rgba(7, 7, 7), material.Sample(-0.25f, -0.25f));
        Assert.Equal(new Rgba(7, 7, 7), material.Sample(1.75f, 2.75f));
        Assert.Equal(new Rgba(9, 9, 9), material.Sample(0.25f, 0.25f));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => new PreviewMaterialLibrary(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewMaterialLibrary(new ContentFileSystem([]), 0));
    }
}
