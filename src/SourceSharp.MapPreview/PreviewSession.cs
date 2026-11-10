//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Cameras;
using SourceSharp.MapPreview.Materials;
using SourceSharp.MapPreview.Overlays;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Report;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Movement;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapPreview;

/// <summary>What a preview renders and writes.</summary>
public sealed record PreviewOptions
{
    /// <summary>Gets the default options: a labelled contact sheet of the standard views.</summary>
    public static PreviewOptions Default { get; } = new();

    /// <summary>Gets a value indicating whether to compose the views into one contact sheet.</summary>
    public bool Sheet { get; init; } = true;

    /// <summary>Gets a value indicating whether to also write each view as its own image.</summary>
    public bool Individual { get; init; }

    /// <summary>Gets the width and height of an individual view.</summary>
    public (int Width, int Height) ViewSize { get; init; } = (1024, 768);

    /// <summary>Gets the width and height of one contact sheet tile.</summary>
    public (int Width, int Height) TileSize { get; init; } = (512, 384);

    /// <summary>Gets the tiles per contact sheet row.</summary>
    public int SheetColumns { get; init; } = 3;

    /// <summary>Gets the scene options (HDR, tools).</summary>
    public PreviewSceneOptions Scene { get; init; } = PreviewSceneOptions.Default;

    /// <summary>Gets the render settings (exposure, threads).</summary>
    public RenderSettings Render { get; init; } = RenderSettings.Default;

    /// <summary>Gets the camera plan options (spawn count, eye height, player hull for the scale figure).</summary>
    public CameraPlanOptions Cameras { get; init; } = CameraPlanOptions.Default;

    /// <summary>Gets the player hull width the scale figure is drawn at.</summary>
    public float PlayerWidth { get; init; } = 32f;

    /// <summary>Gets the player hull height the scale figure is drawn at.</summary>
    public float PlayerHeight { get; init; } = 72f;

    /// <summary>Gets extra cameras to render after the planned ones.</summary>
    public IReadOnlyList<PreviewCamera> ExtraCameras { get; init; } = [];

    /// <summary>Gets the longest texture edge decoded.</summary>
    public int MaxTextureEdge { get; init; } = PreviewMaterialLibrary.DefaultMaxTextureEdge;

    /// <summary>
    /// Gets the movement profile to analyse reachability with, or null to
    /// skip it. When set, the preview adds a reach overview tile, markers for
    /// traps and stuck spawns, and a movement section in the report.
    /// </summary>
    public MovementProfile? Movement { get; init; }

    /// <summary>Gets how reachability is sampled.</summary>
    public ReachabilityOptions Reachability { get; init; } = ReachabilityOptions.Default;

    /// <summary>
    /// Gets how flights are fuzzed when <see cref="Movement"/> is set, or null
    /// to skip them. Problem spots get markers; floor only flights reach is
    /// drawn blue on the reach tile.
    /// </summary>
    public AirborneOptions? Airborne { get; init; } = AirborneOptions.Default;

    /// <summary>Gets the most problem spots from the airborne fuzz that get markers.</summary>
    public int AirborneMarkers { get; init; } = 8;
}

/// <summary>An image a preview produced.</summary>
/// <param name="FileName">The file name, relative to the output folder (for example <c>views/01-overview.png</c>).</param>
/// <param name="Png">The encoded PNG.</param>
public sealed record PreviewImage(string FileName, byte[] Png);

/// <summary>The result of rendering a preview.</summary>
/// <param name="Report">The report.</param>
/// <param name="Images">The images, sheet first.</param>
public sealed record PreviewResult(PreviewReport Report, IReadOnlyList<PreviewImage> Images)
{
    /// <summary>The report as the JSON written to <c>index.json</c>.</summary>
    /// <returns>UTF-8 bytes.</returns>
    public byte[] ReportJson() => PreviewReportWriter.Write(Report);
}

/// <summary>
/// Renders previews of compiled maps against one mounted game: the object a
/// host keeps for as long as it previews maps of that game.
/// </summary>
/// <remarks>
/// <para>
/// The session borrows the game content and owns nothing that outlives a
/// render: each render mounts the map's pak in front of the game, resolves
/// models and materials through that, and disposes the pak mount before it
/// returns, also when it fails or is cancelled. Two renders on one session
/// may run at once; they share only the read-only game mount.
/// </para>
/// <para>
/// A render never throws for content problems. A missing model, material or
/// texture draws as the engine's magenta checker or a magenta box, gets a
/// numbered marker, and is listed in the report, because an agent fixing a
/// map needs to see what is wrong, not a preview that refused to draw.
/// </para>
/// </remarks>
public sealed class PreviewSession
{
    private readonly IContentFileSystem _game;
    private readonly ColorSpace _colors = new();

    /// <summary>Creates a session over a mounted game.</summary>
    /// <param name="game">The game's content (not owned).</param>
    public PreviewSession(IContentFileSystem game)
    {
        ArgumentNullException.ThrowIfNull(game);
        _game = game;
    }

    /// <summary>Renders a preview of a compiled map.</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="mapName">The map's name, for labels and the report.</param>
    /// <param name="options">What to render.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The images and report.</returns>
    public async Task<PreviewResult> RenderAsync(
        BspData bsp, string mapName, PreviewOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(mapName);
        PreviewOptions o = options ?? PreviewOptions.Default;

        ReadOnlyMemory<byte> pakBytes = bsp[BspLump.PakFile].Data;
        ContentFileSystem? pak = null;
        try
        {
            IContentFileSystem content = _game;
            if (pakBytes.Length > 0)
            {
                pak = new ContentFileSystem([ArchiveContentMount.Mount(PakArchive.Open(pakBytes, mapName + ".bsp"))]);
                content = new LayeredContent(pak, _game);
            }

            return await RenderWithAsync(bsp, mapName, o, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pak is not null)
            {
                await pak.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<PreviewResult> RenderWithAsync(
        BspData bsp, string mapName, PreviewOptions o, IContentFileSystem content, CancellationToken cancellationToken)
    {
        (IReadOnlyDictionary<string, PreviewModel> models, IReadOnlyList<ModelLoadIssue> modelIssues) =
            await PreviewModelLoader.LoadAsync(BspSceneBuilder.ModelNames(bsp), content, cancellationToken).ConfigureAwait(false);
        PreviewScene scene = BspSceneBuilder.Build(bsp, models, o.Scene);
        PreviewMaterialLibrary library = new(content, o.MaxTextureEdge);
        PreviewMaterial[] materials = await library.ResolveAsync(scene.MaterialNames, cancellationToken).ConfigureAwait(false);
        PreviewSky sky = await library.LoadSkyAsync(scene.SkyName, cancellationToken).ConfigureAwait(false);
        SceneRenderer renderer = new(scene, materials, sky, _colors);

        (List<PreviewMarker> markers, List<MissingMaterialReport> missingMaterials, List<MissingModelReport> missingModels) =
            Findings(scene, materials, modelIssues);
        (ReachabilityResult? reach, MovementReport? movement, AirborneResult? air) =
            await MovementAsync(bsp, scene, o, content, markers, cancellationToken).ConfigureAwait(false);

        (int width, int height) = o.Sheet && !o.Individual ? o.TileSize : o.ViewSize;
        IReadOnlyList<PlannedView> planned = CameraPlanner.Plan(scene, o.Cameras with { Width = width, Height = height });
        List<PlannedView> views = [.. planned];
        views.AddRange(o.ExtraCameras.Select(c => new PlannedView(c with { Width = width, Height = height }, new ViewFilter(null))));

        List<RgbaImage> tiles = [];
        List<PreviewImage> images = [];
        List<CameraReport> cameras = [];
        Dictionary<int, List<string>> markerViews = [];
        int scale = Math.Max(1, width / 512);
        for (int n = 0; n < views.Count; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlannedView planned1 = views[n];
            RenderedView rendered = renderer.Render(planned1.Camera, planned1.Filter, o.Render, cancellationToken);
            CameraView view = planned1.Camera.View();
            RgbaImage? reachTile = null;
            if (reach is not null && planned1.Camera.Kind == CameraKind.Overview)
            {
                reachTile = new RgbaImage(rendered.Image.Width, rendered.Image.Height, (byte[])rendered.Image.Pixels.Clone());
                ReachOverlay.Draw(reachTile, view, rendered.Buffer, reach, Math.Max(2, scale * 2), planned1.Filter.CutAboveZ, air?.ExplosiveOnly);
                Annotator.ScaleBar(reachTile, view, scale);
                Annotator.Markers(reachTile, view, rendered.Buffer, markers, scale);
                Annotator.Label(reachTile, $"{n + 1} reach {movement!.Profile}", scale);
                BitmapFont.Draw(reachTile, ReachOverlay.Legend, 2 * scale, 12 * scale, scale, Annotator.LabelInk, Annotator.LabelBacking);
            }
            if (planned1.Camera.IsOrthographic)
            {
                Annotator.ScaleBar(rendered.Image, view, scale);
            }
            else
            {
                Vec3 ahead = new Vec3(view.Forward.X, view.Forward.Y, 0);
                float length = ahead.Length();
                ahead = length > 0 ? ahead * (1f / length) : new Vec3(1, 0, 0);
                Vec3 feet = planned1.Camera.Position - new Vec3(0, 0, o.Cameras.EyeHeight) + (ahead * 128f);
                Annotator.ScaleFigure(rendered.Image, view, rendered.Buffer, feet, o.PlayerWidth, o.PlayerHeight);
            }

            IReadOnlyList<int> drawn = Annotator.Markers(rendered.Image, view, rendered.Buffer, markers, scale);
            foreach (int number in drawn)
            {
                if (!markerViews.TryGetValue(number, out List<string>? list))
                {
                    markerViews[number] = list = [];
                }

                list.Add(planned1.Camera.Id);
            }

            string label = $"{n + 1} {planned1.Camera.Id} {planned1.Camera.Description}";
            Annotator.Label(rendered.Image, label, scale);
            string file = $"views/{n + 1:00}-{planned1.Camera.Id}.png";
            if (o.Individual || !o.Sheet)
            {
                images.Add(new PreviewImage(file, rendered.Image.ToPng()));
            }

            tiles.Add(rendered.Image);
            cameras.Add(CameraEntry(planned1.Camera, rendered, scene, drawn));
            if (reachTile is not null)
            {
                if (o.Individual || !o.Sheet)
                {
                    images.Add(new PreviewImage($"views/{n + 1:00}-reach.png", reachTile.ToPng()));
                }

                tiles.Add(reachTile);
            }
        }

        if (o.Sheet && tiles.Count > 0)
        {
            List<RgbaImage> sheetTiles = tiles;
            if (o.Individual)
            {
                // Individual views were rendered at view size; the sheet takes
                // them down to tile size so it stays one readable image.
                sheetTiles = [.. tiles.Select(t => Downscale(t, o.TileSize.Width, o.TileSize.Height))];
            }

            images.Insert(0, new PreviewImage("sheet.png", ContactSheet.Compose(sheetTiles, o.SheetColumns).ToPng()));
        }

        (Vec3 min, Vec3 max) = CameraPlanner.ContentBounds(scene);
        PreviewReport report = new(
            mapName,
            bsp.FileVersion,
            [V(min), V(max)],
            new SceneCounts(scene.Triangles.Length, scene.Surfaces.Length, scene.MaterialNames.Length, scene.Lightmaps.Length, scene.Props.Count, scene.Entities.Count),
            [.. images.Select(i => i.FileName)],
            cameras,
            [.. markers.Select(m => new MarkerReport(m.Number, m.Kind, m.Text, V(m.Position), markerViews.TryGetValue(m.Number, out List<string>? v) ? v : []))],
            new PreviewChecks(
                missingMaterials,
                missingModels,
                Lighting(scene, materials, o.Scene.Hdr),
                new SkyboxReport(scene.SkyName, sky.Faces.Count(f => f is not null)),
                [.. scene.Spawns.Select(s => new SpawnReport(s.ClassName, s.Team, V(s.Origin), s.Yaw))],
                EntityClasses(scene.Entities),
                movement));
        return new PreviewResult(report, images);
    }

    /// <summary>
    /// Runs the movement analysis when asked: the map's player collision with
    /// its props' models, walks and jumps from every floor, and markers for
    /// the stuck spawns and traps it finds.
    /// </summary>
    private static async Task<(ReachabilityResult? Result, MovementReport? Report, AirborneResult? Air)> MovementAsync(
        BspData bsp, PreviewScene scene, PreviewOptions o, IContentFileSystem content, List<PreviewMarker> markers, CancellationToken cancellationToken)
    {
        if (o.Movement is not { } profile)
        {
            return (null, null, null);
        }

        (HullTraceWorld world, IReadOnlyList<string> collisionWarnings) = await HullTraceWorld.FromBspAsync(bsp, content, cancellationToken).ConfigureAwait(false);
        (Vec3 min, Vec3 max) = CameraPlanner.ContentBounds(scene);
        ReachSpawn[] spawns = [.. scene.Spawns.Select(s => new ReachSpawn(s.Team, s.Origin))];
        ReachabilityResult result = Reachability.Analyze(
            world, profile, spawns, min - new Vec3(0, 0, 64), max + new Vec3(0, 0, 64), o.Reachability, cancellationToken);

        List<RegionReport> stuck = [];
        for (int i = 0; i < spawns.Length; i++)
        {
            if (result.SpawnNodes[i] < 0)
            {
                int number = markers.Count + 1;
                markers.Add(new PreviewMarker(number, "stuck_spawn", StuckText(spawns[i].Team, world, profile, spawns[i].Origin, scene.Props), spawns[i].Origin));
                stuck.Add(new RegionReport(0, V(spawns[i].Origin), V(spawns[i].Origin), V(spawns[i].Origin), number));
            }
        }

        // The fuzz runs before the trap markers are written: it says which
        // traps a blast jump gets out of.
        AirborneResult? air = o.Airborne is { } airOptions
            ? AirborneFuzzer.Analyze(world, profile, result, airOptions, cancellationToken)
            : null;

        List<RegionReport> traps = [];
        foreach (int t in Enumerable.Range(0, result.Traps.Count).OrderByDescending(t => result.Traps[t].Nodes.Count).ThenBy(t => result.Traps[t].Nodes[0]))
        {
            NodeGroup trap = result.Traps[t];
            IReadOnlyList<string>? escapes = air?.TrapEscapes[t];
            int number = markers.Count + 1;
            markers.Add(new PreviewMarker(number, "trap", TrapText(trap.Nodes.Count, escapes), trap.Sample));
            traps.Add(new RegionReport(trap.Nodes.Count, V(trap.Sample), V(trap.Min), V(trap.Max), number, escapes));
        }

        List<RegionReport> islands = [.. result.Islands
            .OrderByDescending(g => g.Nodes.Count).ThenBy(g => g.Nodes[0]).Take(10)
            .Select(g => new RegionReport(g.Nodes.Count, V(g.Sample), V(g.Min), V(g.Max), 0))];
        Dictionary<int, int> reached = result.ReachedBy.ToDictionary(r => r.Key, r => r.Value.Count(b => b));
        AirborneReport? airReport = null;
        if (air is not null)
        {
            List<SpotReport> problems = [];
            foreach (EventCluster spot in air.Clusters.Where(c => AirborneFuzzer.IsProblem(c.Kind)).Take(10))
            {
                int number = 0;
                if (problems.Count < o.AirborneMarkers)
                {
                    number = markers.Count + 1;
                    markers.Add(new PreviewMarker(number, Snake(spot.Kind.ToString()), $"{Snake(spot.Kind.ToString())} in {spot.Count} of {air.Trajectories} flights", spot.Centre));
                }

                problems.Add(Spot(spot, world, number));
            }

            List<SpotReport> bonks = [.. air.Clusters.Where(c => !AirborneFuzzer.IsProblem(c.Kind)).Take(5).Select(c => Spot(c, world, 0))];

            // Areas only blast jumpers reach: the largest few get a marker,
            // since a roof or a map edge a rocket jumper stands on usually
            // wants player clip, or is a deliberate reward.
            List<RegionReport> blastOnly = [];
            foreach (NodeGroup area in air.BlastOnlyAreas.Take(10))
            {
                int number = 0;
                if (area.Nodes.Count >= BlastOnlyMarkerNodes && blastOnly.Count(r => r.Marker > 0) < BlastOnlyMarkers)
                {
                    number = markers.Count + 1;
                    markers.Add(new PreviewMarker(number, "blast_only", $"only blast jumps reach here ({area.Nodes.Count} positions)", area.Sample));
                }

                blastOnly.Add(new RegionReport(area.Nodes.Count, V(area.Sample), V(area.Min), V(area.Max), number));
            }

            airReport = new AirborneReport(air.Trajectories, air.ExplosiveOnly.Count, problems, bonks, blastOnly);
        }

        return (result, new MovementReport(profile.Name, result.Nodes.Count, reached, stuck, traps, islands, result.Islands.Count, airReport, collisionWarnings), air);
    }

    /// <summary>How many blast-only areas get a marker.</summary>
    private const int BlastOnlyMarkers = 3;

    /// <summary>How many floor positions a blast-only area needs for a marker: smaller ones are a crate top or a sill.</summary>
    private const int BlastOnlyMarkerNodes = 16;

    /// <summary>
    /// A stuck spawn's marker text: why no floor position takes it. Either
    /// the player's box starts inside something, named when it is a prop
    /// (the prop nearest the solid it hit), or it is clear but has no floor
    /// under it within a step.
    /// </summary>
    /// <param name="team">The spawn's team.</param>
    /// <param name="world">The collision.</param>
    /// <param name="profile">The player, for the standing box.</param>
    /// <param name="origin">The spawn's origin.</param>
    /// <param name="props">The map's props, to name one the box is inside.</param>
    /// <returns>The text.</returns>
    internal static string StuckText(int team, HullTraceWorld world, MovementProfile profile, Vec3 origin, IReadOnlyList<PreviewProp> props)
    {
        HullTrace inside = world.Trace(origin, origin, profile.Mins(false), profile.Maxs(false));
        if (!inside.StartSolid || inside.Brush < 0)
        {
            return $"spawn team {team} has no floor under it";
        }

        // A prop's pieces lie about its origin; the prop nearest the hit
        // solid, with its origin no more than a player's width outside it,
        // is the one the box is in.
        NavBrush solid = world.Solids[inside.Brush];
        const double reach = 48;
        PreviewProp? prop = props
            .Where(p => p.Origin.X >= solid.MinX - reach && p.Origin.X <= solid.MaxX + reach
                && p.Origin.Y >= solid.MinY - reach && p.Origin.Y <= solid.MaxY + reach
                && p.Origin.Z >= solid.MinZ - reach && p.Origin.Z <= solid.MaxZ + reach)
            .OrderBy(p => (p.Origin - origin).LengthSquared())
            .FirstOrDefault();
        return prop is null
            ? $"spawn team {team} starts inside a brush"
            : $"spawn team {team} starts inside the prop {prop.Model}";
    }

    /// <summary>A trap marker's text: whether blast jumps get out, when that was tried.</summary>
    internal static string TrapText(int positions, IReadOnlyList<string>? escapes) => escapes switch
    {
        null => $"a player who gets here cannot get out ({positions} positions)",
        { Count: 0 } => $"a player who gets here cannot get out, not even by blast jumping ({positions} positions)",
        _ => $"a player who gets here cannot walk or jump out ({positions} positions); {string.Join(", ", escapes)} gets out",
    };

    private static SpotReport Spot(EventCluster c, HullTraceWorld world, int marker)
    {
        float[][]? solid = null;
        if (c.Brush >= 0)
        {
            MapFormats.Nav.NavBrush b = world.Solids[c.Brush];
            solid = [V(new Vec3((float)b.MinX, (float)b.MinY, (float)b.MinZ)), V(new Vec3((float)b.MaxX, (float)b.MaxY, (float)b.MaxZ))];
        }

        return new SpotReport(Snake(c.Kind.ToString()), c.Count, V(c.Centre), c.Launches, solid, marker);
    }

    /// <summary>Markers and report entries for every missing material and model, numbered in scene order.</summary>
    private static (List<PreviewMarker> Markers, List<MissingMaterialReport> Materials, List<MissingModelReport> Models) Findings(
        PreviewScene scene, PreviewMaterial[] materials, IReadOnlyList<ModelLoadIssue> modelIssues)
    {
        List<PreviewMarker> markers = [];
        List<MissingMaterialReport> missingMaterials = [];
        List<MissingModelReport> missingModels = [];
        Dictionary<string, string> reasons = modelIssues.ToDictionary(i => i.Model, i => i.Reason, StringComparer.Ordinal);

        for (int m = 0; m < materials.Length; m++)
        {
            PreviewMaterial material = materials[m];
            if (!material.IsMissing || material.Problem == MaterialProblem.MissingModel)
            {
                continue;
            }

            int surfaces = 0;
            Vec3? at = null;
            for (int s = 0; s < scene.Surfaces.Length; s++)
            {
                if (scene.Surfaces[s].Material == m)
                {
                    surfaces++;
                    at ??= SurfaceCentre(scene, s);
                }
            }

            if (surfaces == 0)
            {
                continue;
            }

            int number = markers.Count + 1;
            string problem = Snake(material.Problem.ToString());
            markers.Add(new PreviewMarker(number, "missing_material", $"{material.Name}: {problem}", at ?? Vec3.Zero));
            missingMaterials.Add(new MissingMaterialReport(material.Name, problem, surfaces, number));
        }

        foreach (IGrouping<string, PreviewProp> group in scene.Props.Where(p => !p.Loaded).GroupBy(p => p.Model, StringComparer.Ordinal))
        {
            int number = markers.Count + 1;
            string reason = reasons.TryGetValue(group.Key, out string? r) ? r : "not loaded";
            PreviewProp first = group.First();
            markers.Add(new PreviewMarker(number, "missing_model", $"{group.Key}: {reason}", first.Origin));
            missingModels.Add(new MissingModelReport(group.Key, reason, group.Count(), number));
        }

        return (markers, missingMaterials, missingModels);
    }

    private static Vec3? SurfaceCentre(PreviewScene scene, int surface)
    {
        foreach (PreviewTriangle tri in scene.Triangles)
        {
            if (tri.Surface == surface)
            {
                return (scene.Vertices[tri.A].Position + scene.Vertices[tri.B].Position + scene.Vertices[tri.C].Position) * (1f / 3f);
            }
        }

        return null;
    }

    private static CameraReport CameraEntry(PreviewCamera camera, RenderedView rendered, PreviewScene scene, IReadOnlyList<int> markers)
    {
        (float pitch, float yaw) = camera.Angles();
        List<EntityRef> entities = [.. rendered.VisibleEntities.Select(e => new EntityRef(e, scene.Entities[e].ClassName, scene.Entities[e].Get("targetname")))];
        List<ModelCount> models = [.. rendered.VisibleProps
            .GroupBy(p => scene.Props[p].Model, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ModelCount(g.Key, g.Count()))];
        ViewLuminance l = rendered.Luminance;
        return new CameraReport(
            camera.Id,
            camera.Kind.ToString().ToLowerInvariant(),
            camera.Description,
            V(camera.Position),
            MathF.Round(pitch, 2),
            MathF.Round(yaw, 2),
            camera.IsOrthographic ? null : camera.FovDegrees,
            camera.IsOrthographic ? camera.OrthoHalfHeight : null,
            [camera.Width, camera.Height],
            entities,
            models,
            markers,
            new LuminanceReport(Round(l.Mean), Round(l.P5), Round(l.P95), Round(l.BlackFraction), Round(l.ClippedFraction)));
    }

    /// <summary>Lightmap statistics, and how many lit world surfaces have no lightmap.</summary>
    internal static LightingReport Lighting(PreviewScene scene, PreviewMaterial[] materials, bool hdr)
    {
        long count = 0, black = 0, clipped = 0;
        double sum = 0;
        foreach (PreviewLightmap lightmap in scene.Lightmaps)
        {
            foreach (Vec3 c in lightmap.Samples)
            {
                float y = (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
                sum += y;
                black += y < 0.02f ? 1 : 0;
                clipped += y >= 1f ? 1 : 0;
                count++;
            }
        }

        int unlit = 0;
        foreach (PreviewSurface surface in scene.Surfaces)
        {
            if (surface.Kind is SurfaceKind.World or SurfaceKind.Displacement or SurfaceKind.BrushEntity
                && !surface.Sky && surface.Lightmap < 0 && !materials[surface.Material].Unlit && !materials[surface.Material].IsMissing
                && materials[surface.Material].Opacity != MaterialOpacity.Translucent)
            {
                unlit++;
            }
        }

        return count == 0
            ? new LightingReport(0, 0, 0, 0, unlit, hdr)
            : new LightingReport(count, Round((float)(sum / count)), Round(black / (float)count), Round(clipped / (float)count), unlit, hdr);
    }

    private static IReadOnlyDictionary<string, int> EntityClasses(IReadOnlyList<BspEntity> entities)
    {
        SortedDictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (BspEntity entity in entities)
        {
            string name = entity.ClassName;
            counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>Nearest-neighbour downscale for sheet tiles.</summary>
    private static RgbaImage Downscale(RgbaImage source, int width, int height)
    {
        RgbaImage target = new(width, height, new byte[width * height * 4]);
        for (int y = 0; y < height; y++)
        {
            int sy = y * source.Height / height;
            for (int x = 0; x < width; x++)
            {
                target.Set(x, y, source.Get(x * source.Width / width, sy));
            }
        }

        return target;
    }

    private static string Snake(string pascal)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLower(pascal[i], CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static float Round(float value) => MathF.Round(value, 4);

    private static float[] V(Vec3 v) => [MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2)];
}
