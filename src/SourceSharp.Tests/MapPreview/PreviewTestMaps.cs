//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapPreview.Materials;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.Tests.MapTools.Bsp;

namespace SourceSharp.Tests.MapPreview;

/// <summary>
/// Small maps and scenes for the preview facts: a sealed room compiled by the
/// in-process vbsp, with typed entities, and hand-built scenes for the
/// rasteriser and shader.
/// </summary>
internal static class PreviewTestMaps
{
    /// <summary>A material compiled as sky.</summary>
    public const string Sky = "unit/sky";

    /// <summary>The room's interior: 512 x 512 x 256 from the origin.</summary>
    public static readonly (Vec3 Min, Vec3 Max) Interior = (new Vec3(0, 0, 0), new Vec3(512, 512, 256));

    /// <summary>An entity to add to the room: class, key values, and an optional brush.</summary>
    public sealed record TestEntity(string ClassName, (string Key, string Value)[] Keys, (Vec3 Min, Vec3 Max)? Brush = null, string Material = UnitMap.Plain);

    /// <summary>
    /// Compiles a sealed room (walls 16 thick around <see cref="Interior"/>,
    /// the ceiling sky when asked) with entities, through vbsp only: no
    /// lighting, so faces have no lightmaps until <see cref="Light"/> adds them.
    /// </summary>
    public static async Task<BspData> RoomAsync(bool skyCeiling, params TestEntity[] entities)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        world.AddKey("skyname", "sky_test");
        int id = 10;
        (Vec3 lo, Vec3 hi) = Interior;
        const float T = 16;
        Add(world, UnitMap.Plain, lo.X - T, lo.Y - T, lo.Z - T, hi.X + T, hi.Y + T, lo.Z, ref id);
        Add(world, skyCeiling ? Sky : UnitMap.Plain, lo.X - T, lo.Y - T, hi.Z, hi.X + T, hi.Y + T, hi.Z + T, ref id);
        Add(world, UnitMap.Plain, lo.X - T, lo.Y - T, lo.Z, lo.X, hi.Y + T, hi.Z, ref id);
        Add(world, UnitMap.Plain, hi.X, lo.Y - T, lo.Z, hi.X + T, hi.Y + T, hi.Z, ref id);
        Add(world, UnitMap.Plain, lo.X, lo.Y - T, lo.Z, hi.X, lo.Y, hi.Z, ref id);
        Add(world, UnitMap.Plain, lo.X, hi.Y, lo.Z, hi.X, hi.Y + T, hi.Z, ref id);
        document.Chunks.Add(world);

        foreach (TestEntity e in entities)
        {
            VmfChunk chunk = new(MapFileLoader.EntityChunk);
            chunk.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
            chunk.AddKey("classname", e.ClassName);
            foreach ((string key, string value) in e.Keys)
            {
                chunk.AddKey(key, value);
            }

            if (e.Brush is { } brush)
            {
                Add(chunk, e.Material, brush.Min.X, brush.Min.Y, brush.Min.Z, brush.Max.X, brush.Max.Y, brush.Max.Z, ref id);
            }

            document.Chunks.Add(chunk);
        }

        VbspContext context = await ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);
        VbspResult result = await Vbsp.CompileAsync(map, context);
        return result.Bsp ?? throw new InvalidOperationException("the test room did not compile");
    }

    /// <summary>
    /// Gives every lightmapped face a style-0 lightmap of one colour, and
    /// optionally a second style, by writing a lighting lump and the faces'
    /// offsets and styles.
    /// </summary>
    /// <param name="bsp">The compiled room.</param>
    /// <param name="style0">The style-0 sample (mantissa, exponent) for every luxel.</param>
    /// <param name="style1">A second style's sample, or null for one style.</param>
    /// <param name="bumpedFace">A face to mark bump-lit: its texinfo gets the flag, and its lump holds four maps per style, the extra three a different colour.</param>
    public static void Light(BspData bsp, ColorRgbExp32 style0, ColorRgbExp32? style1 = null, int bumpedFace = -1)
    {
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        TexInfo[] texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        List<ColorRgbExp32> samples = [];
        ColorRgbExp32 bumpNoise = new() { R = 7, G = 7, B = 7, Exponent = 0 };
        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].LightmapTextureSizeInLuxels[0] < 0 || faces[f].TexInfo < 0)
            {
                continue;
            }

            int count = (faces[f].LightmapTextureSizeInLuxels[0] + 1) * (faces[f].LightmapTextureSizeInLuxels[1] + 1);
            bool bumped = f == bumpedFace;
            if (bumped)
            {
                // A texinfo of its own, so no other face picks up the flag.
                TexInfo copy = texInfos[faces[f].TexInfo];
                copy.Flags |= (int)SurfaceFlags.BumpLight;
                texInfos = [.. texInfos, copy];
                faces[f].TexInfo = (short)(texInfos.Length - 1);
            }

            faces[f].LightOfs = samples.Count * 4;
            faces[f].Styles[0] = 0;
            faces[f].Styles[1] = style1 is null ? (byte)255 : (byte)1;
            faces[f].Styles[2] = 255;
            faces[f].Styles[3] = 255;
            foreach (ColorRgbExp32? style in style1 is null ? [style0] : new ColorRgbExp32?[] { style0, style1 })
            {
                for (int map = 0; map < (bumped ? 4 : 1); map++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        samples.Add(map == 0 ? style!.Value : bumpNoise);
                    }
                }
            }
        }

        bsp.SetLump(BspLump.Faces, MemoryMarshal.AsBytes(faces.AsSpan()).ToArray());
        bsp.SetLump(BspLump.TexInfo, MemoryMarshal.AsBytes(texInfos.AsSpan()).ToArray());
        bsp.SetLump(BspLump.Lighting, MemoryMarshal.AsBytes(samples.ToArray().AsSpan()).ToArray());
    }

    /// <summary>A sample with the given mantissa and exponent on every channel.</summary>
    public static ColorRgbExp32 Sample(byte mantissa, sbyte exponent = 0) =>
        new() { R = mantissa, G = mantissa, B = mantissa, Exponent = exponent };

    /// <summary>The materials the room uses, as an in-memory content mount.</summary>
    public static async Task<ContentFileSystem> ContentAsync(params (string Path, byte[] Body)[] extra)
    {
        InMemoryFileSystem files = new();
        foreach ((string path, byte[] body) in extra)
        {
            files.AddFile(path, body);
        }

        return new ContentFileSystem([await DirectoryContentMount.MountAsync(files, VPath.Empty)]);
    }

    /// <summary>
    /// A scene of quads built by hand: each quad a surface with one material
    /// (index into <paramref name="materials"/>), its own vertex light, and an
    /// optional lightmap.
    /// </summary>
    public static PreviewScene Scene(
        IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C, Vec3 D, Vec3 Normal, int Material, Vec3 Light, int Lightmap, int Entity, bool Sky)> quads,
        int materialCount,
        PreviewLightmap[]? lightmaps = null)
    {
        List<PreviewVertex> vertices = [];
        List<PreviewTriangle> triangles = [];
        List<PreviewSurface> surfaces = [];
        foreach (var q in quads)
        {
            int surface = surfaces.Count;
            surfaces.Add(new PreviewSurface(SurfaceKind.World, q.Material, q.Lightmap, q.Entity, -1, q.Sky));
            int first = vertices.Count;
            vertices.Add(new PreviewVertex(q.A, 0, 0, 0, 0, q.Light));
            vertices.Add(new PreviewVertex(q.B, 1, 0, 1, 0, q.Light));
            vertices.Add(new PreviewVertex(q.C, 1, 1, 1, 1, q.Light));
            vertices.Add(new PreviewVertex(q.D, 0, 1, 0, 1, q.Light));
            triangles.Add(new PreviewTriangle(first, first + 1, first + 2, surface, q.Normal));
            triangles.Add(new PreviewTriangle(first, first + 2, first + 3, surface, q.Normal));
        }

        string[] names = [.. Enumerable.Range(0, materialCount).Select(i => $"m{i}")];
        return new PreviewScene([.. vertices], [.. triangles], [.. surfaces], names, lightmaps ?? [], [], [], [], new Vec3(-1000, -1000, -1000), new Vec3(1000, 1000, 1000), null);
    }

    /// <summary>A square facing -X at distance <paramref name="x"/>, spanning ±half in Y and Z.</summary>
    public static (Vec3 A, Vec3 B, Vec3 C, Vec3 D, Vec3 Normal, int Material, Vec3 Light, int Lightmap, int Entity, bool Sky) Wall(
        float x, float half, int material, Vec3 light, int lightmap = -1, int entity = -1, bool sky = false) =>
        (new Vec3(x, -half, -half), new Vec3(x, half, -half), new Vec3(x, half, half), new Vec3(x, -half, half),
            new Vec3(-1, 0, 0), material, light, lightmap, entity, sky);

    /// <summary>A flat, opaque material of one colour.</summary>
    public static PreviewMaterial Flat(Rgba colour, MaterialOpacity opacity = MaterialOpacity.Opaque, float alpha = 1f, bool unlit = false, bool noCull = false) =>
        new($"flat{colour.R}", "lightmappedgeneric", RgbaImage.Filled(2, 2, colour), new Rgba(255, 255, 255), opacity, 0.5f, alpha, unlit, noCull, MaterialProblem.None);

    private static void Add(VmfChunk parent, string material, float x0, float y0, float z0, float x1, float y1, float z1, ref int id) =>
        parent.Children.Add(UnitMap.Box(material, (x0, y0, z0), (x1, y1, z1), id++));

    private static async Task<VbspContext> ContextAsync()
    {
        VbspContext unit = await UnitMap.ContextAsync();
        InMemoryFileSystem files = new();
        foreach (string name in new[] { UnitMap.Plain, UnitMap.NoDraw, UnitMap.Hint })
        {
            string var = name == UnitMap.NoDraw ? "\t\"%compileNoDraw\" \"1\"\n" : name == UnitMap.Hint ? "\t\"%compileHint\" \"1\"\n" : string.Empty;
            files.AddText($"materials/{name}.vmt", $"\"LightmappedGeneric\"\n{{\n\t\"$basetexture\" \"unit/missing\"\n{var}}}\n");
        }

        files.AddText($"materials/{Sky}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileSky\" \"1\"\n}\n");
        files.AddText("materials/unit/trigger.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return new VbspContext(unit.Options, new ContentFileSystem([mount])) { MapBase = "unit" };
    }
}
