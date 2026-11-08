//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.AssetDb.Imaging;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.AssetDb.Models;

/// <summary>
/// One triangle of a studio model's level-of-detail-zero mesh, in model space,
/// with its texture coordinates and the skin slot its mesh draws with.
/// </summary>
/// <param name="A">The first corner.</param>
/// <param name="B">The second corner.</param>
/// <param name="C">The third corner.</param>
/// <param name="UvA">The first corner's texture coordinate.</param>
/// <param name="UvB">The second corner's texture coordinate.</param>
/// <param name="UvC">The third corner's texture coordinate.</param>
/// <param name="Slot">
/// The mesh's material slot: the column of the skin table, which the active
/// skin family maps to a texture.
/// </param>
public readonly record struct ModelTriangle(
    Vec3 A,
    Vec3 B,
    Vec3 C,
    (float U, float V) UvA,
    (float U, float V) UvB,
    (float U, float V) UvC,
    int Slot)
{
    /// <summary>A triangle with no texture coordinates, drawing slot 0.</summary>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <returns>The triangle.</returns>
    public static ModelTriangle Untextured(Vec3 a, Vec3 b, Vec3 c) => new(a, b, c, default, default, default, 0);
}

/// <summary>What one skin slot looks like in a render.</summary>
/// <param name="Texture">The base texture, decoded, or null to draw flat grey.</param>
/// <param name="AlphaTest">Whether texels under half alpha are holes (foliage, fences, grates).</param>
public sealed record RenderMaterial(RgbaImage? Texture, bool AlphaTest);

/// <summary>The views a model is rendered from.</summary>
/// <remarks>
/// Named from the model's point of view, in Source's convention that a model
/// faces +X: <see cref="Left"/> is the side the model's left hand is on (+Y),
/// so it is what a viewer standing on that side sees.
/// </remarks>
public enum ModelView
{
    /// <summary>From +X, looking back at the model's face; +Y to the right, +Z up.</summary>
    Front,

    /// <summary>From -X; -Y to the right, +Z up.</summary>
    Back,

    /// <summary>From +Y, the model's left; -X to the right, +Z up.</summary>
    Left,

    /// <summary>From -Y, the model's right; +X to the right, +Z up.</summary>
    Right,

    /// <summary>From +Z looking down, as Hammer's top view: +X to the right, +Y up.</summary>
    Top,

    /// <summary>From front-right and above, a three-quarter view with its own fit.</summary>
    Iso,
}

/// <summary>
/// Renders a studio model's level-of-detail-zero mesh, textured, from the six
/// <see cref="ModelView"/>s, plus a labelled sheet of all six.
/// </summary>
/// <remarks>
/// <para>
/// View convention. A view is a pair (toward, up) of unit model-space
/// vectors: a point projects to screen X along <c>right = up x toward</c> and
/// to screen Y along <c>up</c>, and its depth is <c>dot(p, toward)</c>, where
/// larger is nearer the viewer, which is what the z-buffer keeps.
/// </para>
/// <para>
/// Scale. The five axis views share one scale, chosen so the model's longest
/// bounding-box edge spans 90 percent of the panel, and each is centred on
/// the box. A pole is therefore tall in the front view and a small dot in the
/// top view, as it should be; fitting each view separately would blow the dot
/// up to fill the panel and make every view lie about proportion. A grid in
/// world units, labelled with its step, sits behind the model in those views,
/// with the model's origin lines drawn darker: the origin is the point a map
/// places the prop at. The iso view is fitted on its own and has no grid.
/// </para>
/// <para>
/// Shading. A face's colour is its texel times
/// <c>0.30 + 0.45 (n . toward) + 0.35 max(0, n . key)</c>, clamped to one, with
/// <c>n</c> the geometric normal turned to face the viewer and <c>key</c> a
/// fixed light from above front-left. Faces are not culled, so an open shell
/// keeps its inside instead of growing a hole. Textures are sampled nearest,
/// wrapping. Only IEEE-exact arithmetic is used (products, sums, square
/// roots, floors), so the bytes are the same on every machine.
/// </para>
/// </remarks>
public static class ModelRenderer
{
    /// <summary>The order views appear in on the sheet: left to right, top row first.</summary>
    public static IReadOnlyList<ModelView> SheetOrder { get; } =
        [ModelView.Front, ModelView.Left, ModelView.Right, ModelView.Back, ModelView.Top, ModelView.Iso];

    /// <summary>How many views the sheet holds across.</summary>
    public const int SheetColumns = 3;

    /// <summary>How many rows of views the sheet holds.</summary>
    public const int SheetRows = 2;

    /// <summary>The gap between and around the sheet's panels, in pixels.</summary>
    public const int SheetGap = 4;

    /// <summary>The empty-panel colour: a pale blue-grey most textures read well against.</summary>
    public static readonly Rgba Background = new(168, 186, 204);

    /// <summary>The colour of the grid lines.</summary>
    public static readonly Rgba GridLine = new(150, 168, 186);

    /// <summary>The colour of the grid lines through the model's origin.</summary>
    public static readonly Rgba OriginLine = new(96, 112, 132);

    /// <summary>The flat colour of a face whose material has no texture.</summary>
    public static readonly Rgba Untextured = new(200, 200, 200);

    /// <summary>The colour of the sheet's gaps.</summary>
    public static readonly Rgba SheetBorder = new(40, 44, 48);

    private static readonly Rgba LabelInk = new(16, 16, 16);
    private static readonly Rgba LabelBacking = new(236, 236, 236);

    /// <summary>The fraction of the panel the model's longest edge spans.</summary>
    private const float FrameFill = 0.9f;

    /// <summary>The most grid cells across the model's longest edge.</summary>
    private const int MaxGridCells = 8;

    /// <summary>
    /// Collects the level-of-detail-zero triangles the VTX draws, with their
    /// positions and texture coordinates resolved through the VVD.
    /// </summary>
    /// <param name="mdl">The model.</param>
    /// <param name="vvd">The shared vertex block, fixups applied.</param>
    /// <param name="vtx">The strip groups.</param>
    /// <returns>One entry per drawn triangle, in file order.</returns>
    /// <remarks>
    /// <para>
    /// The two-step every studio reader gets wrong once: a strip index names a
    /// GROUP vertex, whose <c>OrigMeshVertId</c> names a vertex of the MESH,
    /// which the model's byte offset into the VVD and the mesh's own run
    /// offset base into the file's vertex block. Skipping the VVD's fixup
    /// table picks the right COUNT of vertices and the wrong ones.
    /// </para>
    /// <para>
    /// A strip flagged as a triangle strip contributes one triangle per index
    /// past the first two, every second one wound the other way so the surface
    /// normal is continuous along the strip; any other strip is a triangle
    /// list read three indices at a time. A triangle whose indices name
    /// vertices the file does not hold is dropped, not guessed at. The VTX and
    /// MDL hierarchies are walked in lockstep and stop at whichever runs out
    /// first, the same way the reference loaders pair them.
    /// </para>
    /// <para>
    /// Of each body part, only the first model that yields triangles is drawn.
    /// A bodygroup's models are alternatives, and drawing them all overlays
    /// every variant at once; the first is the default look, and when it is
    /// the usual empty "off" option the next one is what the part shows.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ModelTriangle> CollectTriangles(MdlFile mdl, VvdFile vvd, VtxFile vtx)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ArgumentNullException.ThrowIfNull(vvd);
        ArgumentNullException.ThrowIfNull(vtx);

        StudioVertex[] vertices = vvd.VerticesForLod(0);
        List<ModelTriangle> triangles = [];

        IReadOnlyList<(int Offset, VtxBodyPartHeader Header)> vtxParts = vtx.BodyParts();
        int partLimit = Math.Min(vtxParts.Count, mdl.BodyParts().Length);

        for (int partId = 0; partId < partLimit; partId++)
        {
            (int partOffset, VtxBodyPartHeader vtxPart) = vtxParts[partId];
            IReadOnlyList<(int Offset, VtxModelHeader Header)> vtxModels = vtx.Models(partOffset, vtxPart);
            IReadOnlyList<StudioModel> mdlModels = [.. mdl.Models(partId)];
            int modelLimit = Math.Min(vtxModels.Count, mdlModels.Count);

            for (int modelId = 0; modelId < modelLimit; modelId++)
            {
                int before = triangles.Count;
                (int modelOffset, VtxModelHeader vtxModel) = vtxModels[modelId];
                IReadOnlyList<(int Offset, VtxModelLodHeader Header)> lods = vtx.Lods(modelOffset, vtxModel);
                if (lods.Count == 0)
                {
                    continue;
                }

                (int lodOffset, VtxModelLodHeader vtxLod) = lods[0];
                IReadOnlyList<(int Offset, VtxMeshHeader Header)> vtxMeshes = vtx.Meshes(lodOffset, vtxLod);
                StudioModel mdlModel = mdlModels[modelId];
                ReadOnlySpan<StudioMesh> mdlMeshes = mdl.Meshes(partId, modelId);
                int meshLimit = Math.Min(vtxMeshes.Count, mdlMeshes.Length);

                for (int meshId = 0; meshId < meshLimit; meshId++)
                {
                    (int meshOffset, VtxMeshHeader vtxMesh) = vtxMeshes[meshId];
                    CollectMeshTriangles(vtx, vtxMesh, meshOffset, mdlModel, mdlMeshes[meshId], vertices, triangles);
                }

                if (triangles.Count > before)
                {
                    break;
                }
            }
        }

        return triangles;
    }

    /// <summary>The axis-aligned box around every corner of every triangle.</summary>
    /// <param name="triangles">The triangles.</param>
    /// <returns>The box; both corners zero when there are no triangles.</returns>
    public static (Vec3 Min, Vec3 Max) Bounds(IReadOnlyList<ModelTriangle> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        if (triangles.Count == 0)
        {
            return (Vec3.Zero, Vec3.Zero);
        }

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (ModelTriangle t in triangles)
        {
            foreach (Vec3 p in (ReadOnlySpan<Vec3>)[t.A, t.B, t.C])
            {
                minX = MathF.Min(minX, p.X);
                minY = MathF.Min(minY, p.Y);
                minZ = MathF.Min(minZ, p.Z);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
                maxZ = MathF.Max(maxZ, p.Z);
            }
        }

        return (new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
    }

    /// <summary>
    /// The grid step for a model: the smallest power of two, at least one
    /// unit, that cuts the longest edge into at most eight cells.
    /// </summary>
    /// <param name="bounds">The model's box.</param>
    /// <returns>The step in world units.</returns>
    public static int GridStep((Vec3 Min, Vec3 Max) bounds)
    {
        float longest = LongestEdge(bounds);
        int step = 1;
        while (step < (1 << 20) && longest / step > MaxGridCells)
        {
            step *= 2;
        }

        return step;
    }

    /// <summary>Renders every view.</summary>
    /// <param name="triangles">The model's triangles.</param>
    /// <param name="materials">What each skin slot draws with; a slot past the end, or null, draws flat grey.</param>
    /// <param name="size">The panel edge, in pixels.</param>
    /// <returns>One image per view, in <see cref="ModelView"/> order.</returns>
    public static IReadOnlyDictionary<ModelView, RgbaImage> RenderAll(
        IReadOnlyList<ModelTriangle> triangles,
        IReadOnlyList<RenderMaterial?> materials,
        int size)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(materials);

        (Vec3 Min, Vec3 Max) bounds = Bounds(triangles);
        Dictionary<ModelView, RgbaImage> views = [];
        foreach (ModelView view in Enum.GetValues<ModelView>())
        {
            views[view] = RenderView(triangles, materials, view, size, bounds);
        }

        return views;
    }

    /// <summary>Renders one view.</summary>
    /// <param name="triangles">The model's triangles.</param>
    /// <param name="materials">What each skin slot draws with.</param>
    /// <param name="view">Which view.</param>
    /// <param name="size">The panel edge, in pixels; at least 16.</param>
    /// <param name="bounds">The model's box, from <see cref="Bounds"/>.</param>
    /// <returns>The panel: opaque, background where the model is not.</returns>
    public static RgbaImage RenderView(
        IReadOnlyList<ModelTriangle> triangles,
        IReadOnlyList<RenderMaterial?> materials,
        ModelView view,
        int size,
        (Vec3 Min, Vec3 Max) bounds)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);

        Frame frame = FrameFor(view);
        RgbaImage image = RgbaImage.Filled(size, size, Background);

        float scale;
        float centreU, centreV;
        if (view == ModelView.Iso)
        {
            (scale, centreU, centreV) = FitCorners(frame, bounds, size);
        }
        else
        {
            float longest = LongestEdge(bounds);
            scale = longest > 0f ? (FrameFill * size) / longest : 1f;
            Vec3 centre = (bounds.Min + bounds.Max) * 0.5f;
            centreU = Vec3.Dot(centre, frame.Right);
            centreV = Vec3.Dot(centre, frame.Up);
            DrawGrid(image, GridStep(bounds), scale, centreU, centreV);
        }

        float[] zbuffer = new float[size * size];
        Array.Fill(zbuffer, float.NegativeInfinity);
        Projection projection = new(frame, scale, centreU, centreV, size);
        foreach (ModelTriangle triangle in triangles)
        {
            RenderMaterial? material = (uint)triangle.Slot < (uint)materials.Count ? materials[triangle.Slot] : null;
            FillTriangle(image, zbuffer, projection, triangle, material);
        }

        int labelScale = Math.Max(1, size / 192);
        int margin = 2 * labelScale;
        BitmapFont.Draw(image, view.ToString(), margin + labelScale, margin + labelScale, labelScale, LabelInk, LabelBacking);
        if (view != ModelView.Iso)
        {
            // Top right, not bottom left: a prop's origin is usually at its
            // base, so the origin line runs along the bottom of the panel.
            string grid = $"GRID {GridStep(bounds)}";
            BitmapFont.Draw(
                image,
                grid,
                size - margin - labelScale - BitmapFont.Measure(grid, labelScale),
                margin + labelScale,
                labelScale,
                LabelInk,
                LabelBacking);
        }

        return image;
    }

    /// <summary>
    /// Lays the views out on one sheet, <see cref="SheetColumns"/> by
    /// <see cref="SheetRows"/> in <see cref="SheetOrder"/>, with a dark gap
    /// between and around them.
    /// </summary>
    /// <param name="views">The views, all the same square size.</param>
    /// <returns>The sheet.</returns>
    public static RgbaImage Sheet(IReadOnlyDictionary<ModelView, RgbaImage> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        int size = views[ModelView.Front].Width;
        int width = (SheetColumns * size) + ((SheetColumns + 1) * SheetGap);
        int height = (SheetRows * size) + ((SheetRows + 1) * SheetGap);
        RgbaImage sheet = RgbaImage.Filled(width, height, SheetBorder);
        for (int i = 0; i < SheetOrder.Count; i++)
        {
            int column = i % SheetColumns;
            int row = i / SheetColumns;
            sheet.Blit(
                views[SheetOrder[i]],
                SheetGap + (column * (size + SheetGap)),
                SheetGap + (row * (size + SheetGap)));
        }

        return sheet;
    }

    /// <summary>The view's frame; see the class remarks for the convention.</summary>
    private static Frame FrameFor(ModelView view) => view switch
    {
        ModelView.Front => new Frame(new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
        ModelView.Back => new Frame(new Vec3(-1, 0, 0), new Vec3(0, 0, 1)),
        ModelView.Left => new Frame(new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
        ModelView.Right => new Frame(new Vec3(0, -1, 0), new Vec3(0, 0, 1)),
        ModelView.Top => new Frame(new Vec3(0, 0, 1), new Vec3(0, 1, 0)),
        ModelView.Iso => IsoFrame(),
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
    };

    /// <summary>
    /// Front-right and above: toward (1, -1, 0.8) normalised, with up the
    /// world's +Z made perpendicular to it.
    /// </summary>
    private static Frame IsoFrame()
    {
        Vec3 toward = Unit(new Vec3(1f, -1f, 0.8f));
        Vec3 z = new(0, 0, 1);
        Vec3 up = Unit(z - (toward * Vec3.Dot(z, toward)));
        return new Frame(toward, up);
    }

    /// <summary>The fixed key light, from above front-left.</summary>
    private static Vec3 KeyLight() => Unit(new Vec3(0.5f, 0.3f, 0.8f));

    private static Vec3 Unit(Vec3 v) => v * (1f / v.Length());

    private static float LongestEdge((Vec3 Min, Vec3 Max) bounds)
    {
        Vec3 extent = bounds.Max - bounds.Min;
        return MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
    }

    /// <summary>Fits the eight box corners' projection into the panel.</summary>
    private static (float Scale, float CentreU, float CentreV) FitCorners(
        Frame frame,
        (Vec3 Min, Vec3 Max) bounds,
        int size)
    {
        float minU = float.PositiveInfinity, maxU = float.NegativeInfinity;
        float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            Vec3 p = new(
                (corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            float u = Vec3.Dot(p, frame.Right);
            float v = Vec3.Dot(p, frame.Up);
            minU = MathF.Min(minU, u);
            maxU = MathF.Max(maxU, u);
            minV = MathF.Min(minV, v);
            maxV = MathF.Max(maxV, v);
        }

        float span = MathF.Max(maxU - minU, maxV - minV);
        float scale = span > 0f ? (FrameFill * size) / span : 1f;
        return (scale, (minU + maxU) * 0.5f, (minV + maxV) * 0.5f);
    }

    /// <summary>Grid lines every <paramref name="step"/> units, the origin's darker.</summary>
    private static void DrawGrid(RgbaImage image, int step, float scale, float centreU, float centreV)
    {
        int size = image.Width;
        float half = size * 0.5f;

        // The world range the panel shows along each axis, widened to whole steps.
        float spanWorld = half / scale;
        int firstU = (int)MathF.Floor((centreU - spanWorld) / step);
        int lastU = (int)MathF.Ceiling((centreU + spanWorld) / step);
        int firstV = (int)MathF.Floor((centreV - spanWorld) / step);
        int lastV = (int)MathF.Ceiling((centreV + spanWorld) / step);

        // Ordinary lines first, origin lines last, so a crossing shows the origin.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = firstU; k <= lastU; k++)
            {
                bool origin = k == 0;
                if (origin != (pass == 1))
                {
                    continue;
                }

                int x = (int)MathF.Floor(half + (((k * (float)step) - centreU) * scale));
                image.FillRect(x, 0, 1, size, origin ? OriginLine : GridLine);
            }

            for (int k = firstV; k <= lastV; k++)
            {
                bool origin = k == 0;
                if (origin != (pass == 1))
                {
                    continue;
                }

                int y = (int)MathF.Floor(half - (((k * (float)step) - centreV) * scale));
                image.FillRect(0, y, size, 1, origin ? OriginLine : GridLine);
            }
        }
    }

    /// <summary>
    /// One mesh's triangles: group vertices based into the VVD's block, then
    /// every strip unpacked.
    /// </summary>
    /// <remarks>
    /// The model's <c>VertexIndex</c> is a BYTE offset into the VVD's block
    /// (divide by the vertex size); the mesh's <c>VertexOffset</c> is an INDEX
    /// into the model's run. Confusing the two scales one of them by 48.
    /// </remarks>
    private static void CollectMeshTriangles(
        VtxFile vtx,
        in VtxMeshHeader mesh,
        int meshOffset,
        in StudioModel owner,
        in StudioMesh ownerMesh,
        StudioVertex[] vertices,
        List<ModelTriangle> into)
    {
        int vertexBase = (owner.VertexIndex / Unsafe.SizeOf<StudioVertex>()) + ownerMesh.VertexOffset;
        int slot = ownerMesh.Material;

        foreach ((int groupOffset, VtxStripGroupHeader group) in vtx.StripGroups(meshOffset, mesh))
        {
            ReadOnlySpan<VtxVertex> groupVertices = vtx.Vertices(groupOffset, group);
            ReadOnlySpan<ushort> groupIndices = vtx.Indices(groupOffset, group);

            foreach (VtxStripHeader strip in vtx.Strips(groupOffset, group))
            {
                bool isStrip = ((VtxStripFlags)strip.Flags & VtxStripFlags.IsTriStrip) != 0;
                int triangles = isStrip
                    ? strip.NumIndices - 2
                    : strip.NumIndices - (strip.NumIndices % 3);

                for (int i = 0; i < triangles; i++)
                {
                    int a = isStrip ? i : i * 3;
                    int b = a + 1;
                    int c = a + 2;
                    if (isStrip && (i & 1) != 0)
                    {
                        (b, c) = (c, b); // keep the winding continuous along the strip
                    }

                    AddTriangle(
                        vertexBase,
                        slot,
                        groupVertices,
                        groupIndices,
                        strip.IndexOffset + a,
                        strip.IndexOffset + b,
                        strip.IndexOffset + c,
                        vertices,
                        into);
                }
            }
        }
    }

    /// <summary>Resolves three strip indices to vertices and appends the triangle.</summary>
    private static void AddTriangle(
        int vertexBase,
        int slot,
        ReadOnlySpan<VtxVertex> groupVertices,
        ReadOnlySpan<ushort> groupIndices,
        int a,
        int b,
        int c,
        StudioVertex[] vertices,
        List<ModelTriangle> into)
    {
        if ((uint)a >= (uint)groupIndices.Length || (uint)b >= (uint)groupIndices.Length ||
            (uint)c >= (uint)groupIndices.Length)
        {
            return;
        }

        int ga = groupIndices[a], gb = groupIndices[b], gc = groupIndices[c];
        if (ga >= groupVertices.Length || gb >= groupVertices.Length || gc >= groupVertices.Length)
        {
            return;
        }

        int ia = vertexBase + groupVertices[ga].OrigMeshVertId;
        int ib = vertexBase + groupVertices[gb].OrigMeshVertId;
        int ic = vertexBase + groupVertices[gc].OrigMeshVertId;

        if ((uint)ia >= (uint)vertices.Length
            || (uint)ib >= (uint)vertices.Length
            || (uint)ic >= (uint)vertices.Length)
        {
            return;
        }

        into.Add(new ModelTriangle(
            vertices[ia].Position,
            vertices[ib].Position,
            vertices[ic].Position,
            (vertices[ia].TexCoord[0], vertices[ia].TexCoord[1]),
            (vertices[ib].TexCoord[0], vertices[ib].TexCoord[1]),
            (vertices[ic].TexCoord[0], vertices[ic].TexCoord[1]),
            slot));
    }

    /// <summary>
    /// Fills one triangle with a z test, every edge inclusive so shared edges
    /// leave no seam; the z test is strictly greater, so earlier triangles win
    /// ties deterministically by draw order.
    /// </summary>
    private static void FillTriangle(
        RgbaImage image,
        float[] zbuffer,
        in Projection projection,
        in ModelTriangle triangle,
        RenderMaterial? material)
    {
        int size = image.Width;
        (float ax, float ay, float az) = projection.Apply(triangle.A);
        (float bx, float by, float bz) = projection.Apply(triangle.B);
        (float cx, float cy, float cz) = projection.Apply(triangle.C);

        // Twice the signed area in pixels; zero means no pixels to fill.
        float area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (area == 0f || !float.IsFinite(area))
        {
            return;
        }

        float shade = Shade(triangle, projection.Frame.Toward);
        float invArea = 1f / area;

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
        int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
        int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        RgbaImage? texture = material?.Texture;
        bool alphaTest = material?.AlphaTest == true;
        byte[] pixels = image.Pixels;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;

                // Barycentric weights from the three edge functions, divided by
                // the signed area so either winding fills: an interior point's
                // weights all share the area's sign, and sum to one.
                float wa = (((cx - bx) * (py - by)) - ((cy - by) * (px - bx))) * invArea;
                float wb = (((ax - cx) * (py - cy)) - ((ay - cy) * (px - cx))) * invArea;
                float wc = (((bx - ax) * (py - ay)) - ((by - ay) * (px - ax))) * invArea;
                if (wa < 0f || wb < 0f || wc < 0f)
                {
                    continue;
                }

                float z = (wa * az) + (wb * bz) + (wc * cz);
                int cell = (y * size) + x;
                if (z <= zbuffer[cell])
                {
                    continue;
                }

                Rgba texel = Untextured;
                if (texture is not null)
                {
                    float u = (wa * triangle.UvA.U) + (wb * triangle.UvB.U) + (wc * triangle.UvC.U);
                    float v = (wa * triangle.UvA.V) + (wb * triangle.UvB.V) + (wc * triangle.UvC.V);
                    texel = Sample(texture, u, v);
                    if (alphaTest && texel.A < 128)
                    {
                        continue;
                    }
                }

                zbuffer[cell] = z;
                int at = cell * 4;
                pixels[at] = Lit(texel.R, shade);
                pixels[at + 1] = Lit(texel.G, shade);
                pixels[at + 2] = Lit(texel.B, shade);
                pixels[at + 3] = 255;
            }
        }
    }

    /// <summary>The face's light, per the class remarks.</summary>
    private static float Shade(in ModelTriangle triangle, Vec3 toward)
    {
        Vec3 normal = Vec3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
        float length = normal.Length();
        if (!(length > 0f))
        {
            return 0.30f;
        }

        normal *= 1f / length;
        float facing = Vec3.Dot(normal, toward);
        if (facing < 0f)
        {
            normal = -normal;
            facing = -facing;
        }

        float key = MathF.Max(0f, Vec3.Dot(normal, KeyLight()));
        return MathF.Min(1f, 0.30f + (0.45f * facing) + (0.35f * key));
    }

    private static byte Lit(byte channel, float shade) => (byte)MathF.Min(255f, (channel * shade) + 0.5f);

    /// <summary>Nearest texel, wrapping in both directions.</summary>
    private static Rgba Sample(RgbaImage texture, float u, float v)
    {
        if (!float.IsFinite(u) || !float.IsFinite(v))
        {
            return Untextured;
        }

        float fu = u - MathF.Floor(u);
        float fv = v - MathF.Floor(v);
        int x = Math.Min(texture.Width - 1, (int)(fu * texture.Width));
        int y = Math.Min(texture.Height - 1, (int)(fv * texture.Height));
        return texture.Get(x, y);
    }

    /// <summary>One view's frame: the unit vector toward the viewer and the one up the panel.</summary>
    private readonly record struct Frame(Vec3 Toward, Vec3 Up)
    {
        /// <summary>The panel's horizontal axis; the cross of perpendicular unit vectors is unit.</summary>
        public Vec3 Right => Vec3.Cross(Up, Toward);
    }

    /// <summary>Model space to panel pixels, for one view.</summary>
    private readonly struct Projection
    {
        private readonly Vec3 _right;
        private readonly float _scale;
        private readonly float _centreU;
        private readonly float _centreV;
        private readonly float _half;

        public Projection(Frame frame, float scale, float centreU, float centreV, int size)
        {
            Frame = frame;
            _right = frame.Right;
            _scale = scale;
            _centreU = centreU;
            _centreV = centreV;
            _half = size * 0.5f;
        }

        public Frame Frame { get; }

        /// <summary>Panel X, panel Y (downwards, so +up is up) and depth (larger is nearer).</summary>
        public (float X, float Y, float Depth) Apply(Vec3 p) =>
            (_half + ((Vec3.Dot(p, _right) - _centreU) * _scale),
             _half - ((Vec3.Dot(p, Frame.Up) - _centreV) * _scale),
             Vec3.Dot(p, Frame.Toward));
    }
}
