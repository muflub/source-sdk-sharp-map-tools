//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb.Imaging;
using SourceSharp.AssetDb.Models;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapGen;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The software model renderer: triangle collection through the VVD and VTX,
/// view orientation, the shared scale, texturing, alpha testing, the grid and
/// the sheet.
/// </summary>
public sealed class ModelRendererTests
{
    private const int Size = 96;

    // A tetrahedron: every view sees real area and no two views agree.
    private static readonly Vec3[] Tetra =
    [
        new(0, -40, 0),
        new(40, 20, 0),
        new(-40, 20, 0),
        new(0, 0, 70),
    ];

    private static readonly int[] TetraIndices = [0, 1, 2, 0, 1, 3, 1, 2, 3, 2, 0, 3];

    private static IReadOnlyList<ModelTriangle> TetraTriangles()
    {
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(Tetra, TetraIndices);
        return ModelRenderer.CollectTriangles(MdlFile.Parse(mdl), VvdFile.Parse(vvd), VtxFile.Parse(vtx));
    }

    /// <summary>An axis-aligned square at height <paramref name="z"/>, two triangles, slot <paramref name="slot"/>.</summary>
    private static IEnumerable<ModelTriangle> Square(float x0, float y0, float x1, float y1, float z, int slot)
    {
        Vec3 a = new(x0, y0, z), b = new(x1, y0, z), c = new(x1, y1, z), d = new(x0, y1, z);
        yield return new ModelTriangle(a, b, c, (0, 0), (1, 0), (1, 1), slot);
        yield return new ModelTriangle(a, c, d, (0, 0), (1, 1), (0, 1), slot);
    }

    /// <summary>A vertical square facing X at <paramref name="x"/>, spanning Y and Z.</summary>
    private static IEnumerable<ModelTriangle> Wall(float x, float y0, float y1, float z0, float z1, int slot)
    {
        Vec3 a = new(x, y0, z0), b = new(x, y1, z0), c = new(x, y1, z1), d = new(x, y0, z1);
        yield return new ModelTriangle(a, b, c, (0, 0), (1, 0), (1, 1), slot);
        yield return new ModelTriangle(a, c, d, (0, 0), (1, 1), (0, 1), slot);
    }

    private static RenderMaterial Solid(Rgba colour, bool alphaTest = false) =>
        new(RgbaImage.Filled(2, 2, colour), alphaTest);

    private static RgbaImage View(IReadOnlyList<ModelTriangle> triangles, IReadOnlyList<RenderMaterial?> materials, ModelView view) =>
        ModelRenderer.RenderView(triangles, materials, view, Size, ModelRenderer.Bounds(triangles));

    private static bool IsModelPixel(Rgba p) =>
        p != ModelRenderer.Background && p != ModelRenderer.GridLine && p != ModelRenderer.OriginLine
        && p != new Rgba(236, 236, 236) && p != new Rgba(16, 16, 16); // label backing and ink

    private static int Covered(RgbaImage image) =>
        Enumerable.Range(0, image.Width * image.Height).Count(i => IsModelPixel(image.Get(i % image.Width, i / image.Width)));

    /// <summary>Which of two solid colours dominates the model pixels, red or blue.</summary>
    private static (int Red, int Blue) RedBlue(RgbaImage image, int x0, int x1)
    {
        int red = 0, blue = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                Rgba p = image.Get(x, y);
                if (!IsModelPixel(p))
                {
                    continue;
                }

                if (p.R > 0 && p.B == 0)
                {
                    red++;
                }
                else if (p.B > 0 && p.R == 0)
                {
                    blue++;
                }
            }
        }

        return (red, blue);
    }

    [Fact]
    public void CollectedTrianglesCarryTheirUvsAndSlot()
    {
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(
            Tetra, [0, 1, 3], uvs: [(0.1f, 0.2f), (0.3f, 0.4f), (0.5f, 0.6f), (0.7f, 0.8f)]);

        ModelTriangle triangle = Assert.Single(
            ModelRenderer.CollectTriangles(MdlFile.Parse(mdl), VvdFile.Parse(vvd), VtxFile.Parse(vtx)));

        Assert.Equal(Tetra[0], triangle.A);
        Assert.Equal(Tetra[3], triangle.C);
        Assert.Equal((0.1f, 0.2f), triangle.UvA);
        Assert.Equal((0.7f, 0.8f), triangle.UvC);
        Assert.Equal(0, triangle.Slot);
    }

    [Fact]
    public void IndicesNamingMissingVerticesAreDroppedNotGuessed()
    {
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(Tetra, [0, 1, 2, 0, 1, 9]);

        Assert.Single(ModelRenderer.CollectTriangles(MdlFile.Parse(mdl), VvdFile.Parse(vvd), VtxFile.Parse(vtx)));
    }

    [Fact]
    public void BoundsCoverEveryCornerAndAreZeroForNothing()
    {
        (Vec3 min, Vec3 max) = ModelRenderer.Bounds(TetraTriangles());

        Assert.Equal(new Vec3(-40, -40, 0), min);
        Assert.Equal(new Vec3(40, 20, 70), max);
        Assert.Equal((Vec3.Zero, Vec3.Zero), ModelRenderer.Bounds([]));
    }

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(8f, 1)]
    [InlineData(9f, 2)]
    [InlineData(45f, 8)]
    [InlineData(437f, 64)]
    [InlineData(4000f, 512)]
    public void TheGridStepIsThePowerOfTwoGivingAtMostEightCells(float longest, int expected) =>
        Assert.Equal(expected, ModelRenderer.GridStep((Vec3.Zero, new Vec3(1, longest, 1))));

    [Fact]
    public void EveryViewIsAnOpaqueSquareWithTheModelInIt()
    {
        IReadOnlyDictionary<ModelView, RgbaImage> views = ModelRenderer.RenderAll(TetraTriangles(), [], Size);

        Assert.Equal(Enum.GetValues<ModelView>(), views.Keys.Order());
        foreach ((ModelView view, RgbaImage image) in views)
        {
            Assert.Equal((Size, Size), (image.Width, image.Height));
            Assert.True(Covered(image) > 100, $"{view} shows almost nothing");
            Assert.All(Enumerable.Range(0, Size * Size), i => Assert.Equal(255, image.Pixels[(i * 4) + 3]));
        }
    }

    [Fact]
    public void NoTwoViewsOfTheTetrahedronAgree()
    {
        IReadOnlyDictionary<ModelView, RgbaImage> views = ModelRenderer.RenderAll(TetraTriangles(), [], Size);
        ModelView[] all = Enum.GetValues<ModelView>();

        for (int a = 0; a < all.Length; a++)
        {
            for (int b = a + 1; b < all.Length; b++)
            {
                Assert.NotEqual(views[all[a]].Pixels, views[all[b]].Pixels);
            }
        }
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        IReadOnlyDictionary<ModelView, RgbaImage> a = ModelRenderer.RenderAll(TetraTriangles(), [], Size);
        IReadOnlyDictionary<ModelView, RgbaImage> b = ModelRenderer.RenderAll(TetraTriangles(), [], Size);

        Assert.All(Enum.GetValues<ModelView>(), v => Assert.Equal(a[v].Pixels, b[v].Pixels));
    }

    [Fact]
    public void TheTopViewLooksDownSoTheHigherSurfaceWins()
    {
        // Red above blue, the same footprint: from above, red covers blue.
        List<ModelTriangle> triangles = [.. Square(-10, -10, 10, 10, 20, 0), .. Square(-10, -10, 10, 10, 0, 1)];

        RgbaImage top = View(triangles, [Solid(new Rgba(255, 0, 0)), Solid(new Rgba(0, 0, 255))], ModelView.Top);

        (int red, int blue) = RedBlue(top, 0, Size);
        Assert.True(red > 0);
        Assert.Equal(0, blue);
    }

    [Fact]
    public void TheTopViewPutsPlusXOnTheRight()
    {
        // Red on the +X half, blue on the -X half.
        List<ModelTriangle> triangles = [.. Square(0, -10, 10, 10, 0, 0), .. Square(-10, -10, 0, 10, 0, 1)];

        RgbaImage top = View(triangles, [Solid(new Rgba(255, 0, 0)), Solid(new Rgba(0, 0, 255))], ModelView.Top);

        Assert.True(RedBlue(top, Size / 2, Size).Red > RedBlue(top, Size / 2, Size).Blue);
        Assert.True(RedBlue(top, 0, Size / 2).Blue > RedBlue(top, 0, Size / 2).Red);
    }

    [Fact]
    public void TheFrontViewLooksFromPlusXWithPlusYOnTheRight()
    {
        // Two walls: red nearer +X, blue behind it; a third, green-free check
        // on Y: the red wall covers only +Y, so from the front it is on the right.
        List<ModelTriangle> triangles = [.. Wall(10, 0, 10, 0, 10, 0), .. Wall(-10, -10, 10, 0, 10, 1)];

        RgbaImage front = View(triangles, [Solid(new Rgba(255, 0, 0)), Solid(new Rgba(0, 0, 255))], ModelView.Front);
        RgbaImage back = View(triangles, [Solid(new Rgba(255, 0, 0)), Solid(new Rgba(0, 0, 255))], ModelView.Back);

        Assert.True(RedBlue(front, Size / 2, Size).Red > 0, "front: the near red wall is on the right");
        Assert.Equal(0, RedBlue(front, 0, Size / 2 - 1).Red);
        Assert.Equal(0, RedBlue(back, 0, Size).Red); // from behind, blue hides it
    }

    [Fact]
    public void AxisViewsShareOneScale()
    {
        // A pole: tall in the front view, a dot from the top. Fitting each
        // view on its own would blow the dot up to fill the panel.
        List<ModelTriangle> triangles = [.. Wall(0, -2, 2, 0, 100, 0), .. Square(-2, -2, 2, 2, 100, 0)];

        IReadOnlyDictionary<ModelView, RgbaImage> views = ModelRenderer.RenderAll(triangles, [], Size);

        Assert.True(Covered(views[ModelView.Front]) > 10 * Covered(views[ModelView.Top]));
    }

    [Fact]
    public void FacesAreTexturedAndShadedFromTheirMaterial()
    {
        List<ModelTriangle> triangles = [.. Wall(0, -10, 10, 0, 20, 0)];

        RgbaImage front = View(triangles, [Solid(new Rgba(200, 100, 0))], ModelView.Front);

        Rgba centre = front.Get(Size / 2, Size / 2);
        Assert.True(centre.R > centre.G && centre.G > centre.B, $"expected an orange shade, got {centre}");
        Assert.True(centre.R <= 200, "shading never brightens past the texel");
    }

    [Fact]
    public void AMissingMaterialOrSlotDrawsFlatGrey()
    {
        List<ModelTriangle> triangles = [.. Wall(0, -10, 10, 0, 20, 5)];

        Rgba centre = View(triangles, [null], ModelView.Front).Get(Size / 2, Size / 2);

        Assert.Equal(centre.R, centre.G);
        Assert.Equal(centre.G, centre.B);
        Assert.True(centre.R > 0);
    }

    [Fact]
    public void AlphaTestCutsHolesWhereTheTextureIsClear()
    {
        List<ModelTriangle> triangles = [.. Wall(0, -10, 10, 0, 20, 0)];
        RenderMaterial clear = Solid(new Rgba(255, 0, 0, 0), alphaTest: true);
        RenderMaterial opaqueTexels = Solid(new Rgba(255, 0, 0, 0), alphaTest: false);

        Assert.Equal(0, Covered(View(triangles, [clear], ModelView.Front)));
        Assert.True(Covered(View(triangles, [opaqueTexels], ModelView.Front)) > 0);
    }

    [Fact]
    public void ADegenerateTriangleDrawsNothing()
    {
        Vec3 p = new(1, 2, 3);
        List<ModelTriangle> triangles = [ModelTriangle.Untextured(p, p, p)];

        Assert.Equal(0, Covered(View(triangles, [], ModelView.Front)));
    }

    [Fact]
    public void AxisViewsDrawTheGridWithTheOriginDarker()
    {
        RgbaImage front = View(TetraTriangles(), [], ModelView.Front);
        RgbaImage iso = View(TetraTriangles(), [], ModelView.Iso);

        IEnumerable<Rgba> pixels = Enumerable.Range(0, Size * Size).Select(i => front.Get(i % Size, i / Size));
        Assert.Contains(ModelRenderer.GridLine, pixels);
        Assert.Contains(ModelRenderer.OriginLine, pixels);
        Assert.DoesNotContain(ModelRenderer.GridLine, Enumerable.Range(0, Size * Size).Select(i => iso.Get(i % Size, i / Size)));
    }

    [Fact]
    public void EveryViewIsLabelledInItsTopLeftCorner()
    {
        RgbaImage top = View(TetraTriangles(), [], ModelView.Top);

        // Scale 1 below 192 pixels: text at (3, 3), its backing one pixel out.
        Assert.Equal(new Rgba(236, 236, 236), top.Get(2, 2));
        Assert.NotEqual(new Rgba(236, 236, 236), top.Get(1, 1));
    }

    [Fact]
    public void ATinyPanelIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModelRenderer.RenderView(TetraTriangles(), [], ModelView.Front, 8, ModelRenderer.Bounds(TetraTriangles())));

    [Fact]
    public void TheSheetLaysTheViewsOutInItsOrderBetweenGaps()
    {
        IReadOnlyDictionary<ModelView, RgbaImage> views = ModelRenderer.RenderAll(TetraTriangles(), [], Size);

        RgbaImage sheet = ModelRenderer.Sheet(views);

        int gap = ModelRenderer.SheetGap;
        Assert.Equal((3 * Size) + (4 * gap), sheet.Width);
        Assert.Equal((2 * Size) + (3 * gap), sheet.Height);
        Assert.Equal(ModelRenderer.SheetBorder, sheet.Get(0, 0));
        for (int i = 0; i < ModelRenderer.SheetOrder.Count; i++)
        {
            int left = gap + ((i % 3) * (Size + gap));
            int top = gap + ((i / 3) * (Size + gap));
            RgbaImage view = views[ModelRenderer.SheetOrder[i]];
            Assert.Equal(view.Get(Size / 2, Size / 3), sheet.Get(left + (Size / 2), top + (Size / 3)));
        }
    }

    [Fact]
    public void TheFixtureModelRendersSixDistinctCoveredViews()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        Assert.False(root is null, "no checkout root above the test binary");
        string dir = Path.Combine(root!, "src", "SourceSharp.Tests", "Fixtures", "models", "hs_sign");

        MdlFile mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(dir, "hs_sign.mdl")));
        VvdFile vvd = VvdFile.Parse(File.ReadAllBytes(Path.Combine(dir, "hs_sign.vvd")));
        VtxFile vtx = VtxFile.Parse(File.ReadAllBytes(Path.Combine(dir, "hs_sign.dx80.vtx")));

        IReadOnlyList<ModelTriangle> triangles = ModelRenderer.CollectTriangles(mdl, vvd, vtx);
        IReadOnlyDictionary<ModelView, RgbaImage> views = ModelRenderer.RenderAll(triangles, [], Size);

        // The fixture exists to be a shipped file: the layout arithmetic
        // (fixups, mesh bases, strip flags) is exercised for truth, against
        // whatever geometry the author actually published.
        Assert.Equal(vtx.TriangleCount(0), triangles.Count);
        Assert.All(views.Values, v => Assert.True(Covered(v) > 0));
        Assert.Equal(6, views.Values.Select(v => Convert.ToHexString(v.Pixels)).Distinct().Count());
    }
}
