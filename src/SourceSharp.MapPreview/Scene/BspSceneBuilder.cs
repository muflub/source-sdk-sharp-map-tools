//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapPreview.Scene;

/// <summary>How a scene is built from a BSP.</summary>
public sealed record PreviewSceneOptions
{
    /// <summary>Gets the default options: LDR lighting, tools hidden.</summary>
    public static PreviewSceneOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether to read the HDR lighting lumps. When the
    /// requested set is absent the other is used, so a map compiled for only
    /// one of them still lights.
    /// </summary>
    public bool Hdr { get; init; }

    /// <summary>
    /// Gets a value indicating whether to keep faces the engine never draws
    /// (nodraw, skip, hint, trigger) and invisible brush entities, for a tools
    /// view.
    /// </summary>
    public bool IncludeTools { get; init; }
}

/// <summary>
/// Builds a <see cref="PreviewScene"/> from a compiled map: world faces,
/// displacements, brush entities and placed models, with each face's baked
/// lightmap and each model lit from the ambient cubes.
/// </summary>
/// <remarks>
/// <para>
/// Pure computation: models are loaded beforehand (see
/// <see cref="PreviewModelLoader"/>) and passed in, so the builder does no IO
/// and runs the same in a test as against a real game. A model the caller
/// could not load becomes a magenta stand-in box and stays in the scene, so a
/// missing asset is visible rather than silently absent.
/// </para>
/// <para>
/// Brush entity models are placed at their entity's origin. Their angles are
/// not applied: for most brush entities (doors, buttons, trains) the
/// <c>angles</c> key is a movement direction, not a rotation of the model.
/// </para>
/// </remarks>
public static class BspSceneBuilder
{
    /// <summary>The material a missing model's stand-in box draws with.</summary>
    public const string MissingModelMaterial = "__preview/missing_model";

    /// <summary>The material a model slot with no material draws with.</summary>
    public const string MissingSlotMaterial = "__preview/missing_slot";

    /// <summary>
    /// Scales a decoded lightmap or ambient sample to display light: the
    /// engine reads a sample as <c>mantissa * 2^exponent / 255</c>, so 1.0 is
    /// a texture shown at its authored brightness.
    /// </summary>
    public const float LightScale = 1f / 255f;

    /// <summary>The half-size of a missing model's stand-in box when its bounds are unknown.</summary>
    public const float MissingModelHalfSize = 16f;

    /// <summary>
    /// Brush entity classes the engine never draws: volumes, portals and
    /// markers whose brushes only define a region.
    /// </summary>
    private static readonly ImmutableHashSet<string> InvisibleBrushClasses =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "func_areaportal",
            "func_areaportalwindow",
            "func_occluder",
            "func_viscluster",
            "func_clip_vphysics",
            "func_respawnroom",
            "func_nobuild",
            "func_regenerate",
            "func_capturezone",
            "func_ladder",
            "func_buyzone",
            "func_bomb_target",
            "func_hostage_rescue",
            "func_precipitation",
            "func_dustcloud",
            "func_smokevolume",
            "func_nav_avoid",
            "func_nav_prefer",
            "func_nav_blocker",
            "func_upgradestation",
            "func_flagdetectionzone",
            "func_suggested_build");

    /// <summary>The spawn classes the planner looks at, across the common games.</summary>
    private static readonly ImmutableHashSet<string> SpawnClasses =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "info_player_start",
            "info_player_deathmatch",
            "info_player_teamspawn",
            "info_player_combine",
            "info_player_rebel",
            "info_player_counterterrorist",
            "info_player_terrorist");

    /// <summary>
    /// Every model path the map places (static props, then prop entities),
    /// normalised and de-duplicated, in first-use order.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The paths to load before <see cref="Build"/>.</returns>
    public static IReadOnlyList<string> ModelNames(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<string> names = [];
        HashSet<string> seen = [];
        StaticPropLump? props = StaticProps(bsp);
        if (props is not null)
        {
            foreach (string name in props.ModelNames)
            {
                string key = PreviewModel.Key(name);
                if (seen.Add(key))
                {
                    names.Add(key);
                }
            }
        }

        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (IsPropEntity(entity, out string? model) && seen.Add(model))
            {
                names.Add(model);
            }
        }

        return names;
    }

    /// <summary>Builds the scene.</summary>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="models">Loaded models keyed by <see cref="PreviewModel.Key"/>; absent ones draw as stand-ins.</param>
    /// <param name="options">The build options.</param>
    /// <returns>The scene.</returns>
    public static PreviewScene Build(
        BspData bsp,
        IReadOnlyDictionary<string, PreviewModel>? models = null,
        PreviewSceneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        PreviewSceneOptions settings = options ?? PreviewSceneOptions.Default;
        Builder builder = new(bsp, settings);
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        ReadOnlySpan<DModel> brushModels = BspStructView.As<DModel>(bsp[BspLump.Models]);

        CoreDispInfo[] displacements = bsp[BspLump.DispInfo].Data.Length > 0
            ? DispLightingLoader.Load(bsp, ComplianceOptions.Correct)
            : [];

        AmbientLight ambient = AmbientLight.FromBsp(bsp, settings.Hdr);
        BspEntity? skyCamera = entities.FirstOrDefault(e => e.ClassName == "sky_camera");
        builder.SkyboxArea = skyCamera is null ? -1 : ambient.AreaOf(ParseVector(skyCamera.Get("origin")));
        builder.Locate = ambient;
        builder.Lights = LocalLights.FromBsp(bsp, settings.Hdr);

        if (brushModels.Length > 0)
        {
            builder.AddModelFaces(brushModels[0], Vec3.Zero, SurfaceKind.World, -1, displacements);
        }

        for (int e = 0; e < entities.Count; e++)
        {
            BspEntity entity = entities[e];
            string? model = entity.Get("model");
            if (model is null || model.Length < 2 || model[0] != '*'
                || !int.TryParse(model.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || index <= 0 || index >= brushModels.Length)
            {
                continue;
            }

            if (!settings.IncludeTools && !IsVisibleBrushEntity(entity))
            {
                continue;
            }

            builder.AddModelFaces(brushModels[index], ParseVector(entity.Get("origin")), SurfaceKind.BrushEntity, e, displacements);
        }

        List<PreviewProp> props = [];
        StaticPropLump? staticProps = StaticProps(bsp);
        if (staticProps is not null)
        {
            foreach (StaticProp prop in staticProps.Props)
            {
                if ((prop.Flags & StaticPropFlags.NoDraw) != 0 || prop.PropType >= staticProps.ModelNames.Count)
                {
                    continue;
                }

                string key = PreviewModel.Key(staticProps.ModelNames[prop.PropType]);
                Vec3 lightAt = (prop.Flags & StaticPropFlags.UseLightingOrigin) != 0 ? prop.LightingOrigin : prop.Origin;
                builder.AddProp(props, key, prop.Origin, prop.Angles, prop.Skin, -1, SurfaceKind.StaticProp, models, ambient, lightAt);
            }
        }

        for (int e = 0; e < entities.Count; e++)
        {
            BspEntity entity = entities[e];
            if (!IsPropEntity(entity, out string? key) || entity.Get("rendermode") == "10")
            {
                continue;
            }

            Vec3 origin = ParseVector(entity.Get("origin"));
            int skin = int.TryParse(entity.Get("skin"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0;
            builder.AddProp(props, key, origin, ParseVector(entity.Get("angles")), skin, e, SurfaceKind.PropEntity, models, ambient, origin);
        }

        List<PreviewSpawn> spawns = [];
        foreach (BspEntity entity in entities)
        {
            if (SpawnClasses.Contains(entity.ClassName))
            {
                int team = int.TryParse(entity.Get("TeamNum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) ? t : 0;
                spawns.Add(new PreviewSpawn(entity.ClassName, team, ParseVector(entity.Get("origin")), ParseVector(entity.Get("angles")).Y));
            }
        }

        string? sky = entities.FirstOrDefault(e => e.ClassName == "worldspawn")?.Get("skyname");
        Vec3 min = brushModels.Length > 0 ? brushModels[0].Mins : Vec3.Zero;
        Vec3 max = brushModels.Length > 0 ? brushModels[0].Maxs : Vec3.Zero;
        return builder.Finish(entities, props, spawns, min, max, string.IsNullOrWhiteSpace(sky) ? null : sky);
    }

    /// <summary>Parses a "x y z" entity vector; missing or malformed components are zero.</summary>
    /// <param name="value">The key's value.</param>
    /// <returns>The vector.</returns>
    public static Vec3 ParseVector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Vec3.Zero;
        }

        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float Part(int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        return new Vec3(Part(0), Part(1), Part(2));
    }

    /// <summary>Whether the engine draws a brush entity's model.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>False for trigger volumes, portals and the other region-only classes, or rendermode 10.</returns>
    public static bool IsVisibleBrushEntity(BspEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        string name = entity.ClassName;
        return !name.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)
            && !InvisibleBrushClasses.Contains(name)
            && entity.Get("rendermode") != "10";
    }

    private static bool IsPropEntity(BspEntity entity, out string key)
    {
        string? model = entity.Get("model");
        key = model is null ? string.Empty : PreviewModel.Key(model);
        return key.EndsWith(".mdl", StringComparison.Ordinal);
    }

    private static StaticPropLump? StaticProps(BspData bsp)
    {
        int id = GameLumpId.MakeId(GameLumpId.StaticProps);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id == id)
            {
                return StaticPropLump.Read(entry);
            }
        }

        return null;
    }

    /// <summary>The scene under construction.</summary>
    private sealed class Builder
    {
        private readonly PreviewSceneOptions _options;
        private readonly DFace[] _faces;
        private readonly DPlane[] _planes;
        private readonly Vec3[] _vertexes;
        private readonly DEdge[] _edges;
        private readonly int[] _surfEdges;
        private readonly TexInfo[] _texInfos;
        private readonly DTexData[] _texData;
        private readonly int[] _stringTable;
        private readonly ReadOnlyMemory<byte> _strings;
        private readonly ReadOnlyMemory<byte> _lighting;
        private readonly List<PreviewVertex> _vertices = [];
        private readonly List<PreviewTriangle> _triangles = [];
        private readonly List<PreviewSurface> _surfaces = [];
        private readonly List<string> _materials = [];
        private readonly Dictionary<string, int> _materialIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PreviewLightmap> _lightmaps = [];
        private readonly Dictionary<int, int> _faceLightmap = [];

        /// <summary>Gets or sets the 3D skybox's area, or -1 when the map has none.</summary>
        public int SkyboxArea { get; set; } = -1;

        /// <summary>Gets or sets the local lights models are lit by directly.</summary>
        public LocalLights? Lights { get; set; }

        /// <summary>Gets or sets the leaf locator used to find a face's area.</summary>
        public AmbientLight? Locate { get; set; }

        public Builder(BspData bsp, PreviewSceneOptions options)
        {
            _options = options;
            (BspLump faces, BspLump lighting) = LightingLumps(bsp, options.Hdr);
            _faces = BspStructView.As<DFace>(bsp[faces]).ToArray();
            _lighting = bsp[lighting].Data;
            _planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
            _vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]).ToArray();
            _edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]).ToArray();
            _surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]).ToArray();
            _texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
            _texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray();
            _stringTable = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]).ToArray();
            _strings = bsp[BspLump.TexDataStringData].Data;
        }

        /// <summary>
        /// Picks the face and lighting lumps: the requested dynamic range when
        /// the map has it, else the other one. The HDR faces lump, when
        /// present, carries the HDR lightmap offsets for the same geometry.
        /// </summary>
        private static (BspLump Faces, BspLump Lighting) LightingLumps(BspData bsp, bool hdr)
        {
            bool hasLdr = bsp[BspLump.Lighting].Data.Length > 0;
            bool hasHdr = bsp[BspLump.LightingHdr].Data.Length > 0;
            bool useHdr = hdr ? hasHdr || !hasLdr : !hasLdr && hasHdr;
            if (!useHdr)
            {
                return (BspLump.Faces, BspLump.Lighting);
            }

            BspLump faces = bsp[BspLump.FacesHdr].Data.Length > 0 ? BspLump.FacesHdr : BspLump.Faces;
            return (faces, BspLump.LightingHdr);
        }

        public void AddModelFaces(DModel model, Vec3 offset, SurfaceKind kind, int entity, CoreDispInfo[] displacements)
        {
            int end = Math.Min(_faces.Length, model.FirstFace + model.NumFaces);
            for (int f = Math.Max(0, model.FirstFace); f < end; f++)
            {
                DFace face = _faces[f];
                if (face.TexInfo < 0 || face.TexInfo >= _texInfos.Length)
                {
                    continue;
                }

                TexInfo tex = _texInfos[face.TexInfo];
                SurfaceFlags flags = (SurfaceFlags)tex.Flags;
                bool sky = (flags & (SurfaceFlags.Sky | SurfaceFlags.Sky2D)) != 0;
                if (!sky && !_options.IncludeTools
                    && (flags & (SurfaceFlags.NoDraw | SurfaceFlags.Skip | SurfaceFlags.Hint | SurfaceFlags.Trigger)) != 0)
                {
                    continue;
                }

                int material = MaterialIndex(MaterialName(tex));
                int lightmap = sky ? -1 : LightmapFor(f, face, (flags & SurfaceFlags.BumpLight) != 0);
                if (face.DispInfo >= 0 && face.DispInfo < displacements.Length && displacements[face.DispInfo].Verts.Length > 0)
                {
                    Vec3 baseNormal = FaceNormal(face);
                    AddDisplacement(displacements[face.DispInfo], tex, offset, material, lightmap, entity, f, baseNormal);
                }
                else
                {
                    AddFace(face, tex, offset, kind, material, lightmap, entity, f, sky);
                }
            }
        }

        /// <summary>
        /// The side a face is seen from: its plane's normal as stored. The
        /// face's <c>planenum</c> already names the plane facing the face's
        /// front (its winding runs clockwise seen from there); <c>side</c>
        /// records how the face relates to the node that holds it, and
        /// flipping by it would turn about four faces in ten inside out.
        /// </summary>
        private Vec3 FaceNormal(DFace face) => _planes[face.PlaneNum].Normal;

        private void AddFace(DFace face, TexInfo tex, Vec3 offset, SurfaceKind kind, int material, int lightmap, int entity, int faceIndex, bool sky)
        {
            if (face.NumEdges < 3)
            {
                return;
            }

            Vec3 normal = FaceNormal(face);
            int surface = _surfaces.Count;
            (float w, float h) = TextureSize(tex);
            int first = _vertices.Count;
            Vec3 centre = Vec3.Zero;
            for (int k = 0; k < face.NumEdges; k++)
            {
                int se = _surfEdges[face.FirstEdge + k];
                Vec3 p = se < 0 ? _vertexes[_edges[-se].V[1]] : _vertexes[_edges[se].V[0]];
                centre += p;
                (float u, float v) = TextureUv(tex, p, w, h);
                (float s, float t) = Luxel(tex, face, p);
                _vertices.Add(new PreviewVertex(p + offset, u, v, s, t, Vec3.Zero));
            }

            centre *= 1f / face.NumEdges;
            _surfaces.Add(new PreviewSurface(kind, material, lightmap, entity, faceIndex, sky) { SkyboxArea = InSkybox(centre + normal) });

            for (int k = 1; k + 1 < face.NumEdges; k++)
            {
                _triangles.Add(new PreviewTriangle(first, first + k, first + k + 1, surface, normal));
            }
        }

        /// <remarks>
        /// Triangles are turned to face the base face's side. Displacement
        /// winding follows the grid, not a facing convention, and a sculpted
        /// surface never folds past its base plane's side by more than a
        /// right angle in a valid map, so the base normal is the reliable
        /// reference.
        /// </remarks>
        private void AddDisplacement(CoreDispInfo disp, TexInfo tex, Vec3 offset, int material, int lightmap, int entity, int faceIndex, Vec3 baseNormal)
        {
            int surface = _surfaces.Count;
            Vec3 middle = disp.Verts.Length > 0 ? disp.Verts[disp.Verts.Length / 2] : Vec3.Zero;
            _surfaces.Add(new PreviewSurface(SurfaceKind.Displacement, material, lightmap, entity, faceIndex, false)
            {
                SkyboxArea = InSkybox(middle + baseNormal),
            });
            (float w, float h) = TextureSize(tex);
            ReadOnlySpan<Vec3> verts = disp.Verts;
            ReadOnlySpan<Vec3> flat = disp.FlatVerts;
            int first = _vertices.Count;
            for (int i = 0; i < verts.Length; i++)
            {
                // Texture coordinates come from the undisplaced base quad, as the
                // engine projects them; luxel coordinates are vrad's, which put
                // sample k at k + 0.5.
                (float u, float v) = TextureUv(tex, i < flat.Length ? flat[i] : verts[i], w, h);
                DispUv luxel = disp.LuxelCoord(0, i);
                _vertices.Add(new PreviewVertex(verts[i] + offset, u, v, luxel.X - 0.5f, luxel.Y - 0.5f, Vec3.Zero));
            }

            ReadOnlySpan<ushort> indices = disp.TriIndices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vec3 a = verts[indices[i]], b = verts[indices[i + 1]], c = verts[indices[i + 2]];
                Vec3 n = FacingNormal(a, b, c, baseNormal);
                if (n == Vec3.Zero)
                {
                    continue;
                }

                _triangles.Add(new PreviewTriangle(first + indices[i], first + indices[i + 1], first + indices[i + 2], surface, n));
            }
        }

        public void AddProp(
            List<PreviewProp> props,
            string key,
            Vec3 origin,
            Vec3 angles,
            int skin,
            int entity,
            SurfaceKind kind,
            IReadOnlyDictionary<string, PreviewModel>? models,
            AmbientLight ambient,
            Vec3 lightAt)
        {
            int propIndex = props.Count;
            InstanceTransform transform = InstanceTransform.FromAngles(angles, origin);
            Vec3[]? cube = ambient.CubeAt(lightAt);
            if (models is null || !models.TryGetValue(key, out PreviewModel? model))
            {
                props.Add(new PreviewProp(key, origin, angles, skin, entity, Loaded: false));
                AddBox(origin, MissingModelHalfSize, MaterialIndex(MissingModelMaterial), SurfaceKind.MissingModel, entity, propIndex);
                return;
            }

            props.Add(new PreviewProp(key, origin, angles, skin, entity, Loaded: true));
            Vec3 centre = transform.TransformPoint((model.HullMin + model.HullMax) * 0.5f);
            if (lightAt == origin)
            {
                // The engine lights a model at the centre of its bounds, not its
                // origin, which usually sits on the floor plane where the leaf
                // below is solid and has no ambient samples.
                cube = ambient.CubeAt(centre) ?? cube;
            }

            int[] picked = Lights?.Pick(centre) ?? [];

            Dictionary<int, int> surfaceForSlot = [];
            foreach (StudioTriangle t in model.Triangles)
            {
                if (!surfaceForSlot.TryGetValue(t.Slot, out int surface))
                {
                    surface = _surfaces.Count;
                    string name = model.MaterialFor(skin, t.Slot) ?? MissingSlotMaterial;
                    _surfaces.Add(new PreviewSurface(kind, MaterialIndex(name), -1, entity, -1, false, propIndex) { SkyboxArea = InSkybox(origin) });
                    surfaceForSlot[t.Slot] = surface;
                }

                Vec3 a = transform.TransformPoint(t.A), b = transform.TransformPoint(t.B), c = transform.TransformPoint(t.C);
                Vec3 na = Unit(transform.RotateVector(t.NormalA));
                Vec3 nb = Unit(transform.RotateVector(t.NormalB));
                Vec3 nc = Unit(transform.RotateVector(t.NormalC));
                Vec3 n = FacingNormal(a, b, c, na + nb + nc);
                if (n == Vec3.Zero)
                {
                    continue;
                }

                int first = _vertices.Count;
                _vertices.Add(new PreviewVertex(a, t.UvA.U, t.UvA.V, 0, 0, ModelLight(cube, picked, a, na == Vec3.Zero ? n : na)));
                _vertices.Add(new PreviewVertex(b, t.UvB.U, t.UvB.V, 0, 0, ModelLight(cube, picked, b, nb == Vec3.Zero ? n : nb)));
                _vertices.Add(new PreviewVertex(c, t.UvC.U, t.UvC.V, 0, 0, ModelLight(cube, picked, c, nc == Vec3.Zero ? n : nc)));
                _triangles.Add(new PreviewTriangle(first, first + 1, first + 2, surface, n));
            }
        }

        /// <summary>An axis-aligned box around a centre, lit flat; six faces facing out.</summary>
        public void AddBox(Vec3 centre, float half, int material, SurfaceKind kind, int entity, int prop)
        {
            int surface = _surfaces.Count;
            _surfaces.Add(new PreviewSurface(kind, material, -1, entity, -1, false, prop));
            Vec3 light = new(1, 1, 1);
            ReadOnlySpan<(Vec3 Normal, Vec3 U, Vec3 V)> sides =
            [
                (new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
                (new Vec3(-1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, 1, 0)),
                (new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)),
                (new Vec3(0, -1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
                (new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(0, 1, 0)),
                (new Vec3(0, 0, -1), new Vec3(0, 1, 0), new Vec3(1, 0, 0)),
            ];
            foreach ((Vec3 normal, Vec3 u, Vec3 v) in sides)
            {
                Vec3 c = centre + (normal * half);
                int first = _vertices.Count;
                _vertices.Add(new PreviewVertex(c - (u * half) - (v * half), 0, 0, 0, 0, light));
                _vertices.Add(new PreviewVertex(c + (u * half) - (v * half), 1, 0, 0, 0, light));
                _vertices.Add(new PreviewVertex(c + (u * half) + (v * half), 1, 1, 0, 0, light));
                _vertices.Add(new PreviewVertex(c - (u * half) + (v * half), 0, 1, 0, 0, light));
                _triangles.Add(new PreviewTriangle(first, first + 1, first + 2, surface, normal));
                _triangles.Add(new PreviewTriangle(first, first + 2, first + 3, surface, normal));
            }
        }

        public PreviewScene Finish(
            IReadOnlyList<BspEntity> entities, IReadOnlyList<PreviewProp> props, IReadOnlyList<PreviewSpawn> spawns, Vec3 min, Vec3 max, string? sky) =>
            new(
                [.. _vertices],
                [.. _triangles],
                [.. _surfaces],
                [.. _materials],
                [.. _lightmaps],
                entities,
                props,
                spawns,
                min,
                max,
                sky);

        /// <summary>
        /// Whether a point lies in the 3D skybox's area: the miniature the
        /// engine draws scaled around the player, which a preview leaves out
        /// rather than showing as a small model off to one side.
        /// </summary>
        private bool InSkybox(Vec3 point) =>
            SkyboxArea >= 0 && Locate is not null && Locate.AreaOf(point) == SkyboxArea;

        private Vec3 ModelLight(Vec3[]? cube, int[] picked, Vec3 point, Vec3 normal) =>
            Light(cube, normal) + (picked.Length > 0 && Lights is not null ? Lights.At(picked, point, normal) : Vec3.Zero);

        private static Vec3 Light(Vec3[]? cube, Vec3 normal) =>
            cube is null ? AmbientLight.Fallback : AmbientLight.Evaluate(cube, normal);

        private static Vec3 Unit(Vec3 v)
        {
            float length = v.Length();
            return length > 0 ? v * (1f / length) : Vec3.Zero;
        }

        /// <summary>
        /// The geometric normal of a triangle, turned to agree with a reference
        /// direction (summed vertex normals), or zero for a degenerate triangle.
        /// </summary>
        private static Vec3 FacingNormal(Vec3 a, Vec3 b, Vec3 c, Vec3 reference)
        {
            Vec3 n = Unit(Vec3.Cross(b - a, c - a));
            if (n == Vec3.Zero)
            {
                return Vec3.Zero;
            }

            return Vec3.Dot(n, reference) < 0 ? -n : n;
        }

        private int MaterialIndex(string name)
        {
            if (!_materialIndex.TryGetValue(name, out int index))
            {
                index = _materials.Count;
                _materials.Add(name);
                _materialIndex[name] = index;
            }

            return index;
        }

        private string MaterialName(TexInfo tex)
        {
            if (tex.TexData < 0 || tex.TexData >= _texData.Length)
            {
                return MissingSlotMaterial;
            }

            int id = _texData[tex.TexData].NameStringTableId;
            if (id < 0 || id >= _stringTable.Length)
            {
                return MissingSlotMaterial;
            }

            ReadOnlySpan<byte> strings = _strings.Span;
            int at = _stringTable[id];
            if (at < 0 || at >= strings.Length)
            {
                return MissingSlotMaterial;
            }

            ReadOnlySpan<byte> rest = strings[at..];
            int end = rest.IndexOf((byte)0);
            return Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]);
        }

        private (float Width, float Height) TextureSize(TexInfo tex)
        {
            if (tex.TexData >= 0 && tex.TexData < _texData.Length)
            {
                DTexData data = _texData[tex.TexData];
                return (Math.Max(1, data.Width), Math.Max(1, data.Height));
            }

            return (1, 1);
        }

        private static (float U, float V) TextureUv(TexInfo tex, Vec3 p, float width, float height)
        {
            float u = (tex.TextureVecsTexelsPerWorldUnits[0] * p.X) + (tex.TextureVecsTexelsPerWorldUnits[1] * p.Y)
                + (tex.TextureVecsTexelsPerWorldUnits[2] * p.Z) + tex.TextureVecsTexelsPerWorldUnits[3];
            float v = (tex.TextureVecsTexelsPerWorldUnits[4] * p.X) + (tex.TextureVecsTexelsPerWorldUnits[5] * p.Y)
                + (tex.TextureVecsTexelsPerWorldUnits[6] * p.Z) + tex.TextureVecsTexelsPerWorldUnits[7];
            return (u / width, v / height);
        }

        private static (float S, float T) Luxel(TexInfo tex, DFace face, Vec3 p)
        {
            float s = (tex.LightmapVecsLuxelsPerWorldUnits[0] * p.X) + (tex.LightmapVecsLuxelsPerWorldUnits[1] * p.Y)
                + (tex.LightmapVecsLuxelsPerWorldUnits[2] * p.Z) + tex.LightmapVecsLuxelsPerWorldUnits[3];
            float t = (tex.LightmapVecsLuxelsPerWorldUnits[4] * p.X) + (tex.LightmapVecsLuxelsPerWorldUnits[5] * p.Y)
                + (tex.LightmapVecsLuxelsPerWorldUnits[6] * p.Z) + tex.LightmapVecsLuxelsPerWorldUnits[7];
            return (s - face.LightmapTextureMinsInLuxels[0], t - face.LightmapTextureMinsInLuxels[1]);
        }

        /// <summary>
        /// The face's lightmap: every light style it has, summed at full
        /// strength (each style's default pattern), decoded once and shared by
        /// the face's triangles; -1 when the face is unlit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A face stores up to four styles back to back. Style 0 is the
        /// static light; the others are lights a map can switch or flicker,
        /// which vrad bakes separately so the engine can scale each at run
        /// time. The engine adds them all, so a room lit only by named or
        /// flickering lights (torches, switchable lamps) is dark in style 0
        /// and lit by the sum.
        /// </para>
        /// <para>
        /// A bump-lit face stores four maps per style (the flat one, then one
        /// per bump basis direction); only the flat one is read, so the stride
        /// between styles is four maps.
        /// </para>
        /// </remarks>
        private int LightmapFor(int faceIndex, DFace face, bool bumped)
        {
            if (_faceLightmap.TryGetValue(faceIndex, out int existing))
            {
                return existing;
            }

            int result = -1;
            int width = face.LightmapTextureSizeInLuxels[0] + 1;
            int height = face.LightmapTextureSizeInLuxels[1] + 1;
            int count = width * height;
            int bytes = count * 4;
            int stride = bytes * (bumped ? 4 : 1);
            if (face.LightOfs >= 0 && face.Styles[0] != 255 && width > 0 && height > 0)
            {
                Vec3[] linear = new Vec3[count];
                bool any = false;
                for (int style = 0; style < 4 && face.Styles[style] != 255; style++)
                {
                    int at = face.LightOfs + (style * stride);
                    if (at + bytes > _lighting.Length)
                    {
                        break;
                    }

                    ReadOnlySpan<ColorRgbExp32> samples = BspStructView.As<ColorRgbExp32>(_lighting.Span.Slice(at, bytes));
                    for (int i = 0; i < count; i++)
                    {
                        linear[i] += samples[i].ToLinear() * LightScale;
                    }

                    any = true;
                }

                if (any)
                {
                    result = _lightmaps.Count;
                    _lightmaps.Add(new PreviewLightmap(width, height, linear));
                }
            }

            _faceLightmap[faceIndex] = result;
            return result;
        }
    }
}
