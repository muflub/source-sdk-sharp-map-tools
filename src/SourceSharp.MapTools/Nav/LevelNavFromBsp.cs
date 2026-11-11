//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>A compiled map's navigation could not be built; the message says why.</summary>
public sealed class NavBuildException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NavBuildException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public NavBuildException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public NavBuildException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>How <see cref="LevelNavFromBsp"/> lays its grid over a compiled map.</summary>
public sealed record BspNavOptions
{
    /// <summary>The default cell: 256 units, sixteen 16-unit voxels a side, as a room library's.</summary>
    public const float DefaultCellSize = 256f;

    /// <summary>The default reach: brushes up to 128 units outside a cell are part of its build.</summary>
    public const float DefaultReach = 128f;

    /// <summary>The cells' edge in units; the voxel (<see cref="NavSettings.VoxelSize"/>) must divide it.</summary>
    public float CellSize { get; init; } = DefaultCellSize;

    /// <summary>
    /// The voxel, the walkable slope, the step and jump limits, the costs and
    /// the presets: a room library's defaults unless told otherwise (16-unit
    /// voxels, step 18, jump 56, reach 100).
    /// </summary>
    public NavSettings Settings { get; init; } = NavSettings.Default;

    /// <summary>
    /// How far outside a cell a solid still counts in that cell's build. Each
    /// cell is built from the solids within this many units of it, so a
    /// clearance record is exact for every agent up to twice this wide; a
    /// wider agent may be told it fits where a wall farther off would stop it.
    /// </summary>
    public float Reach { get; init; } = DefaultReach;

    /// <summary>
    /// A static prop model's collision pieces by path (<see cref="NavModelBounds.LoadPiecesAsync"/>),
    /// or null: a prop whose model has them is solid by each piece up to its
    /// own top, one without by its hull box (<see cref="NavGeometry.StaticProps"/>).
    /// </summary>
    public Func<string, IReadOnlyList<NavPiece>?>? ModelPieces { get; init; }

    /// <summary>How many cells are built at once. The file is the same at any degree.</summary>
    public int MaxDegree { get; init; } = 1;

    /// <summary>
    /// Told the cells built so far and the cells in all, once after each cell
    /// is built, on the thread that built it (so concurrently at a degree
    /// above one, and a count may arrive after a higher one); null tells
    /// nothing. It never changes the file.
    /// </summary>
    public Action<int, int>? Progress { get; init; }

    /// <summary>
    /// Whether the build keeps to the space a player can reach (<see cref="NavPlayableSpace"/>):
    /// the grid to its bounds, and every voxel outside its leaves (a 3D skybox, a summary stage,
    /// sealed space no spawn joins) solid. On by default; off builds the whole world, as before
    /// (<c>ssmap nav3d -playspace off</c>).
    /// </summary>
    public bool PlaySpace { get; init; } = true;

    /// <summary>
    /// Where the build reports its phases' times and each cell's
    /// (<see cref="NavBuildTimer"/>), or null to time nothing. It never
    /// changes the file.
    /// </summary>
    public NavBuildTimer? Timer { get; init; }
}

/// <summary>What <see cref="LevelNavFromBsp.Build"/> made: the navigation, and what it could not read.</summary>
/// <param name="Level">The navigation.</param>
/// <param name="Warnings">One sentence for each thing left out: a prop whose model the content lacks, and the like.</param>
public sealed record BspNavResult(Nav3dLevel Level, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds a <c>.nav3d</c> straight from a compiled map, any map: a grid of
/// cells laid over the world model's bounds, each cell's clearance grid built
/// from the map's solids around it, stitched as the room link stitches rooms,
/// and bound to the map's bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is solid.</b> The world's brushes of every contents, those its
/// tree holds (<see cref="NavGeometry.FromBsp(BspData, Func{string, ValueTuple{Vec3, Vec3}?})"/>: player clips are the player class's,
/// monster clips the NPC class's), each displacement's triangles
/// (<see cref="NavGeometry.Displacements(BspData, double)"/>, one voxel deep) and each solid
/// static prop's collision pieces, each solid up to its own top
/// (<see cref="NavGeometry.StaticProps"/>).
/// Doors, movers, breakables and physics props are dynamic obstacles, open
/// space tagged with their names; ladders come from <c>CONTENTS_LADDER</c>
/// brushes and the two ladder entities.
/// </para>
/// <para>
/// <b>Outside is solid.</b> A compiled map's world tree marks the space
/// outside its sealed hull solid. A voxel whose centre lies in such a leaf
/// is solid in the navigation, so the space between the map's outer walls
/// and its bounding box (and any sealed void) holds no leaves; a voxel that
/// straddles a wall is solid already, because the wall's brush overlaps it.
/// A cell that is outside throughout is an empty cell.
/// </para>
/// <para>
/// <b>Cells are a grid, not rooms.</b> Every cell is
/// <see cref="BspNavOptions.CellSize"/> square and as tall as the map (the
/// file's version 3 cell heights when that is not one cell), with no room,
/// no doors and no points of interest: a reader's neighbours run across
/// cell faces wherever the columns are open, so a step that crosses a cell
/// face is only that, not a doorway.
/// </para>
/// <para>
/// <b>Deterministic.</b> Cells are built independently (at
/// <see cref="BspNavOptions.MaxDegree"/> at once), each from its solids in
/// the map's order, and stitched in cell order, records numbered in the
/// order first met, jumps sorted; the file is the same bytes at any thread
/// count and on every run.
/// </para>
/// <para>
/// <b>Nothing to leak.</b> The build holds only managed memory it allocates
/// itself, nothing shared between builds; a cancelled or failed build
/// throws and leaves nothing behind.
/// </para>
/// </remarks>
public static class LevelNavFromBsp
{
    /// <summary>
    /// How many voxels the grid keeps around the play space's bounds: the
    /// floor under its lowest leaf and the solid over its highest are in the
    /// grid, so a floor at the bottom of the play space stands on something.
    /// </summary>
    public const int PlayMarginVoxels = 2;

    /// <summary>The model paths whose hulls a build of this map asks for: its obstacle props', then its solid static props'.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The paths, distinct.</returns>
    public static IReadOnlyList<string> Models(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        return [.. NavModelBounds.PropModels(bsp).Concat(NavModelBounds.StaticPropModels(bsp)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Builds a compiled map's navigation, on the thread pool.</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="binding">The map file's binding (<see cref="Nav3dBspBinding.Of"/>), written into the file.</param>
    /// <param name="modelBounds">A model's hull by path (<see cref="NavModelBounds.LoadAsync"/> over <see cref="Models"/>), or null to leave props out.</param>
    /// <param name="options">The grid; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the build between columns.</param>
    /// <returns>The navigation and its warnings.</returns>
    public static Task<BspNavResult> BuildAsync(
        BspData bsp,
        Nav3dBspBinding binding,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds,
        BspNavOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        return Task.Run(() => Build(bsp, binding, modelBounds, options, cancellationToken), cancellationToken);
    }

    /// <summary>Builds a compiled map's navigation on the calling thread (and <see cref="BspNavOptions.MaxDegree"/> pool threads).</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="binding">The map file's binding, written into the file.</param>
    /// <param name="modelBounds">A model's hull by path, or null to leave props out.</param>
    /// <param name="options">The grid; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the build between columns.</param>
    /// <returns>The navigation and its warnings.</returns>
    /// <exception cref="NavBuildException">The map has no world, or is too tall for the file's columns at this voxel.</exception>
    public static BspNavResult Build(
        BspData bsp,
        Nav3dBspBinding binding,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds,
        BspNavOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        options ??= new BspNavOptions();
        NavSettings settings = options.Settings;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDegree, 1);
        if (!(options.Reach >= 0) || !float.IsFinite(options.Reach))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "the reach is a finite number of units, at least 0.");
        }

        int n;
        try
        {
            n = settings.CellVoxels(options.CellSize);
        }
        catch (RoomLibraryException exception)
        {
            throw new NavBuildException(exception.Message, exception);
        }

        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        if (models.Length == 0 || !(models[0].Mins.X < models[0].Maxs.X && models[0].Mins.Y < models[0].Maxs.Y && models[0].Mins.Z < models[0].Maxs.Z))
        {
            throw new NavBuildException("the map has no world model to build navigation over.");
        }

        // The geometry: the world's brushes, the displacements' shells and
        // the static props' boxes are solid; doors and the like obstacles.
        double voxel = settings.VoxelSize;
        NavBuildTimer? timer = options.Timer;
        NavGeometry entities = NavGeometry.FromBsp(bsp, modelBounds, options.MaxDegree);
        timer?.Mark("brushes");
        (IReadOnlyList<NavBrush> props, IReadOnlyList<string> propWarnings) = NavGeometry.StaticProps(bsp, modelBounds, options.ModelPieces);
        timer?.Mark("props");
        IReadOnlyList<NavBrush> displacements = NavGeometry.Displacements(bsp, voxel, options.MaxDegree);
        timer?.Mark("displacements");
        NavGeometry geometry = new()
        {
            Brushes = [.. entities.Brushes, .. displacements, .. props],
            Obstacles = entities.Obstacles,
            Ladders = ClimbVolumes(bsp, entities.Brushes),
            Warnings = [.. entities.Warnings, .. propWarnings],
        };
        timer?.Mark("geometry");

        // The grid: voxel-aligned at the world's low corner, cells across,
        // one column of voxels up the whole map.
        DModel world = models[0];
        double ox = Math.Floor(world.Mins.X / voxel) * voxel;
        double oy = Math.Floor(world.Mins.Y / voxel) * voxel;
        double oz = Math.Floor(world.Mins.Z / voxel) * voxel;
        double cell = options.CellSize;
        (double topX, double topY, double topZ) = (world.Maxs.X, world.Maxs.Y, world.Maxs.Z);

        // Where a player can be, when the map says (NavPlayableSpace): the
        // grid keeps to it, give or take a margin, so a 3D skybox far above
        // or below the play space does not set the column's height. The
        // grid's lines stay where the world's corner puts them: whole cells
        // and whole voxels are dropped, never shifted.
        NavPlaySpace? space = options.PlaySpace ? NavPlayableSpace.Find(bsp) : null;
        timer?.Mark("play space");
        if (space?.Bounds is { } play)
        {
            double margin = PlayMarginVoxels * voxel;
            ox += Math.Max(0, Math.Floor((play.Mins.X - margin - ox) / cell)) * cell;
            oy += Math.Max(0, Math.Floor((play.Mins.Y - margin - oy) / cell)) * cell;
            oz += Math.Max(0, Math.Floor((play.Mins.Z - margin - oz) / voxel)) * voxel;
            topX = Math.Min(topX, play.Maxs.X + margin);
            topY = Math.Min(topY, play.Maxs.Y + margin);
            topZ = Math.Min(topZ, play.Maxs.Z + margin);
        }

        int columns = Math.Max(1, (int)Math.Ceiling((topX - ox) / cell));
        int rows = Math.Max(1, (int)Math.Ceiling((topY - oy) / cell));
        int nz = Math.Max(1, (int)Math.Ceiling((topZ - oz) / voxel));
        if (nz > Nav3dFormat.MaxColumnVoxels)
        {
            throw new NavBuildException(string.Create(CultureInfo.InvariantCulture,
                $"the map is {topZ - oz:0.##} units tall, {nz} voxels of {voxel:0.##}; a .nav3d column holds at most {Nav3dFormat.MaxColumnVoxels}: build it with a larger voxel."));
        }

        BspTreeView tree = BspTreeView.FromBsp(bsp);
        List<NavBrush> overhang = [.. NavClearanceBuilder.OverhangBrushes(geometry)];
        Dictionary<NavBrush, int> overhangIndex = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < overhang.Count; i++)
        {
            overhangIndex[overhang[i]] = i;
        }

        timer?.Mark("tree");

        // Costliest first, one cell at a time: a cell's time grows with the
        // slanted solids near it, and a few cells of a map can take many
        // times the rest; started last, one of them would hold the build
        // while the other threads wait. Each cell is built alone into its
        // own slot, so the order cannot change a byte.
        CellBuild?[] built = new CellBuild?[columns * rows];
        int[] order = CostliestFirst(geometry.Brushes, columns, rows, ox, oy, cell, options.Reach);
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.MaxDegree, CancellationToken = cancellationToken };
        int done = 0;
        System.Threading.Tasks.Parallel.ForEach(Partitioner.Create(order, EnumerablePartitionerOptions.NoBuffering), parallel, c =>
        {
            long started = timer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            NavRegion region = new(ox + ((c % columns) * cell), oy + ((c / columns) * cell), oz, n, n, nz, voxel);
            built[c] = BuildCell(geometry, region, settings, options.Reach, tree, space, overhangIndex, cancellationToken);
            timer?.Cell(c, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            options.Progress?.Invoke(Interlocked.Increment(ref done), built.Length);
        });
        timer?.Mark("cells");

        return new BspNavResult(
            Stitch(built, geometry, overhang, settings, options, columns, rows, n, nz, new Vec3((float)ox, (float)oy, (float)oz), binding, cancellationToken),
            geometry.Warnings);
    }

    /// <summary>
    /// Where a climbing player's origin can be: each ladder grown sideways by
    /// a player's half-width (<see cref="NavGeometry.LadderHalfWidth"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game moves a climbing player with its box held against the
    /// ladder, so its origin is a half-width out from the ladder's face. A
    /// <c>func_useableladder</c>'s volume already says so (its climb line is
    /// where the origin runs, widened by the half-width). An
    /// <c>info_ladder</c>'s box and a <c>CONTENTS_LADDER</c> brush are the
    /// ladder itself: flagging only the voxels they overlap marks the voxels
    /// the player's box reaches into, which for a ladder against a wall are
    /// the ones it does not fit in, and for a solid ladder brush none at all.
    /// Grown by the half-width, they mark the voxels a climbing player's
    /// origin is in, as the useable ladder does.
    /// </para>
    /// <para>
    /// Only for a compiled map's navigation: a room's navigation keeps its
    /// ladder volumes as they were, so a room pack's bytes do not change.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<Box> ClimbVolumes(BspData bsp, IReadOnlyList<NavBrush> worldBrushes)
    {
        float grow = NavGeometry.LadderHalfWidth;
        Vec3 side = new(grow, grow, 0);
        List<Box> volumes = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            string lower = entity.ClassName.ToLowerInvariant();
            if (lower == "info_ladder" && NavGeometry.LadderBox(entity) is { } box)
            {
                volumes.Add(new Box(box.Mins - side, box.Maxs + side));
            }
            else if (lower == "func_useableladder" && NavGeometry.UseableLadder(entity) is { } climb)
            {
                volumes.Add(climb);
            }
        }

        foreach (NavBrush brush in worldBrushes)
        {
            if ((brush.Contents & Nav3dFormat.LadderContents) != 0)
            {
                volumes.Add(new Box(
                    new Vec3((float)brush.MinX - grow, (float)brush.MinY - grow, (float)brush.MinZ),
                    new Vec3((float)brush.MaxX + grow, (float)brush.MaxY + grow, (float)brush.MaxZ)));
            }
        }

        return volumes;
    }

    /// <summary>One cell: its columns, its own records, and its overhanging brushes' places in the level's list; null when it is outside throughout.</summary>
    private static CellBuild? BuildCell(
        NavGeometry geometry, NavRegion region, NavSettings settings, double reach, BspTreeView tree, NavPlaySpace? space,
        Dictionary<NavBrush, int> overhangIndex, CancellationToken cancellationToken)
    {
        // Which voxels lie outside the map: their centre in a solid leaf, or,
        // with a play space, in a leaf outside it (the 3D skybox, a stage no
        // spawn joins), which no player reaches and the build never voxelises.
        // A column's centres share their x and y, so the tree is walked for
        // the whole column at once (BspTreeView.LeavesAlong): each centre's
        // leaf is the one LeafOf gives it.
        bool[] outside = new bool[region.SizeX * region.SizeY * region.SizeZ];
        bool anyInside = false;
        float[] heights = new float[region.SizeZ];
        int[] leaves = new int[region.SizeZ];
        for (int z = 0; z < region.SizeZ; z++)
        {
            NavBox v = region.Voxel(0, 0, z);
            heights[z] = (float)((v.MinZ + v.MaxZ) / 2);
        }

        for (int y = 0; y < region.SizeY; y++)
        {
            for (int x = 0; x < region.SizeX; x++)
            {
                NavBox v = region.Voxel(x, y, 0);
                tree.LeavesAlong((float)((v.MinX + v.MaxX) / 2), (float)((v.MinY + v.MaxY) / 2), heights, leaves);
                for (int z = 0; z < region.SizeZ; z++)
                {
                    int leaf = leaves[z];
                    bool solid = (leaf >= 0 && leaf < tree.LeafContents.Length && (tree.LeafContents[leaf] & BspTreeView.ContentsSolid) != 0)
                        || (space is not null && !space.Holds(leaf));
                    outside[region.Index(x, y, z)] = solid;
                    anyInside |= !solid;
                }
            }
        }

        if (!anyInside)
        {
            return null;
        }

        // The solids near enough to matter: within the reach of the cell's
        // footprint (any height); an obstacle out of reach keeps its index
        // with no solids, so records name obstacles by their level index.
        double x0 = region.OriginX - reach;
        double y0 = region.OriginY - reach;
        double x1 = region.OriginX + (region.SizeX * region.VoxelSize) + reach;
        double y1 = region.OriginY + (region.SizeY * region.VoxelSize) + reach;
        bool Near(double minX, double minY, double maxX, double maxY) => maxX > x0 && minX < x1 && maxY > y0 && minY < y1;
        NavGeometry near = new()
        {
            Brushes = [.. geometry.Brushes.Where(b => Near(b.MinX, b.MinY, b.MaxX, b.MaxY))],
            Obstacles = [.. geometry.Obstacles.Select(o => o.Brushes.Any(b => Near(b.MinX, b.MinY, b.MaxX, b.MaxY)) ? o : o with { Brushes = [] })],
            Ladders = [.. geometry.Ladders.Where(l => Near(l.Mins.X, l.Mins.Y, l.Maxs.X, l.Maxs.Y))],
        };

        NavRecordTable records = new();
        NavGrid grid = NavClearanceBuilder.Build(near, region, settings, records, outside, cancellationToken);
        NavVoxelKey[] keys = grid.Keys;
        int[] brushes = [.. NavClearanceBuilder.OverhangBrushes(near).Select(b => overhangIndex[b])];
        return new CellBuild(NavColumns.Of(region, keys, records), records, brushes);
    }

    /// <summary>
    /// The grid's cells, costliest first by an estimate: how many slanted
    /// solids (sloped and overhanging brushes, whose tests are a voxel's
    /// costly part) are within the reach of each cell's footprint, ties in
    /// cell order.
    /// </summary>
    /// <remarks>
    /// Only the order the cells are built in follows from it, never what a
    /// cell holds, so an estimate is enough: a solid is counted in every
    /// cell its footprint grown by the reach touches, a box test the cell's
    /// own filter makes too, give or take its open and closed ends.
    /// </remarks>
    internal static int[] CostliestFirst(IReadOnlyList<NavBrush> brushes, int columns, int rows, double ox, double oy, double cell, double reach)
    {
        long[] cost = new long[columns * rows];
        foreach (NavBrush brush in brushes)
        {
            if (brush.IsAxial)
            {
                continue;
            }

            int cx0 = Math.Clamp((int)Math.Floor((brush.MinX - reach - ox) / cell), 0, columns - 1);
            int cx1 = Math.Clamp((int)Math.Floor((brush.MaxX + reach - ox) / cell), 0, columns - 1);
            int cy0 = Math.Clamp((int)Math.Floor((brush.MinY - reach - oy) / cell), 0, rows - 1);
            int cy1 = Math.Clamp((int)Math.Floor((brush.MaxY + reach - oy) / cell), 0, rows - 1);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    cost[(cy * columns) + cx]++;
                }
            }
        }

        int[] order = [.. Enumerable.Range(0, cost.Length)];
        Array.Sort(order, (a, b) => cost[a] != cost[b] ? cost[b].CompareTo(cost[a]) : a.CompareTo(b));
        return order;
    }

    /// <summary>The cells stitched in cell order into one level, with its jump links.</summary>
    private static Nav3dLevel Stitch(
        CellBuild?[] built, NavGeometry geometry, List<NavBrush> overhang, NavSettings settings, BspNavOptions options,
        int columns, int rows, int n, int nz, Vec3 origin, Nav3dBspBinding binding, CancellationToken cancellationToken)
    {
        int[] roots = new int[columns * rows];
        Array.Fill(roots, -1);
        int[]? heights = nz == n ? null : new int[columns * rows];
        int block = n * n;

        // The placed cells in cell order: each owns the next block of
        // columns, and its runs follow the runs of the cells before it in
        // the order its columns hold them (y, then x, as its build laid them).
        List<int> placed = [];
        for (int c = 0; c < built.Length; c++)
        {
            if (built[c] is null)
            {
                continue;
            }

            roots[c] = placed.Count * block;
            if (heights is not null)
            {
                heights[c] = nz;
            }

            placed.Add(c);
        }

        long[] runStart = new long[placed.Count + 1];
        for (int i = 0; i < placed.Count; i++)
        {
            runStart[i + 1] = runStart[i] + built[placed[i]]!.Columns.Runs.Length;
        }

        // Each cell's records in the order its runs first name them (the
        // player's, then the NPC's, run by run), each with its bytes as the
        // level holds them (overhanging brushes renumbered) and their hash:
        // the costly part, on the build's threads.
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.MaxDegree, CancellationToken = cancellationToken };
        CellRecords[] cellRecords = new CellRecords[placed.Count];
        System.Threading.Tasks.Parallel.For(0, placed.Count, parallel, i => cellRecords[i] = CellRecords.Of(built[placed[i]]!));

        // The level's records, numbered in the order first met: cell by
        // cell, each cell's in its own first-met order, a record already in
        // the level taking its number. This is the order a walk over every
        // run meets them in, so the numbers are the same.
        List<byte[]> levelRecords = [Nav3dClearance.BlockedRecord.ToArray()];
        Dictionary<HashedRecord, int> levelIndex = new(HashedRecord.Comparer)
        {
            [HashedRecord.Of(levelRecords[0])] = NavRecordTable.BlockedIndex,
        };
        int[][] maps = new int[placed.Count][];
        for (int i = 0; i < placed.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CellRecords cell = cellRecords[i];
            int[] map = new int[built[placed[i]]!.Records.Count];
            for (int k = 0; k < cell.Order.Length; k++)
            {
                int record = cell.Order[k];
                if (record == NavRecordTable.BlockedIndex)
                {
                    map[record] = NavRecordTable.BlockedIndex;
                    continue;
                }

                if (!levelIndex.TryGetValue(cell.Records[k], out int number))
                {
                    number = levelRecords.Count;
                    levelIndex.Add(cell.Records[k], number);
                    levelRecords.Add(cell.Records[k].Bytes);
                }

                map[record] = number;
            }

            maps[i] = map;
        }

        uint[] offsets = new uint[levelRecords.Count];
        long length = 0;
        for (int r = 0; r < levelRecords.Count; r++)
        {
            offsets[r] = (uint)length;
            length += levelRecords[r].Length;
        }

        byte[] clearance = new byte[length];
        System.Threading.Tasks.Parallel.For(0, (levelRecords.Count + 4095) / 4096, parallel, k =>
        {
            for (int r = k * 4096; r < Math.Min(levelRecords.Count, (k + 1) * 4096); r++)
            {
                levelRecords[r].CopyTo(clearance, offsets[r]);
            }
        });

        // The leaves and the column starts, each cell into its own place.
        Nav3dLeaf[] leaves = new Nav3dLeaf[runStart[^1]];
        uint[] starts = new uint[(placed.Count * block) + 1];
        System.Threading.Tasks.Parallel.For(0, placed.Count, parallel, i =>
        {
            NavColumns cell = built[placed[i]]!.Columns;
            int[] map = maps[i];
            long first = runStart[i];
            for (int k = 0; k < block; k++)
            {
                starts[(i * block) + k] = (uint)(first + cell.ColumnStarts[k]);
            }

            for (int k = 0; k < cell.Runs.Length; k++)
            {
                NavRun run = cell.Runs[k];
                leaves[first + k] = new Nav3dLeaf(
                    run.ZLo, run.Height, run.Key.Flags, run.Key.Cost, offsets[map[run.Key.PlayerRecord]], offsets[map[run.Key.NpcRecord]],
                    run.Key.PlayerFloorZ, run.Key.NpcFloorZ);
            }
        });
        starts[^1] = (uint)runStart[^1];
        options.Timer?.Mark("stitch");
        List<Nav3dJump> jumps = roots.Any(r => r >= 0)
            ? LevelNavLinker.FindJumps(
                new LevelNavLinker.Grid(columns, rows, n, settings.VoxelSize, roots, starts, leaves, offsets),
                settings.StepHeight, settings.JumpHeight, settings.JumpDistance, options.MaxDegree, LevelNavLinker.JumpChunk, cancellationToken)
            : [];
        options.Timer?.Mark("jumps");

        float cellSize = options.CellSize;
        uint CellOf(Box bounds)
        {
            int cx = Math.Clamp((int)Math.Floor((((bounds.Mins.X + bounds.Maxs.X) / 2) - origin.X) / cellSize), 0, columns - 1);
            int cy = Math.Clamp((int)Math.Floor((((bounds.Mins.Y + bounds.Maxs.Y) / 2) - origin.Y) / cellSize), 0, rows - 1);
            return (uint)((cy * columns) + cx);
        }

        Nav3dLevel level = new()
        {
            CellSize = cellSize,
            VoxelSize = settings.VoxelSize,
            CellVoxels = n,
            CellHeights = heights,
            Columns = columns,
            Rows = rows,
            Origin = origin,
            FloorNormalZ = settings.FloorNormalZ,
            StepHeight = settings.StepHeight,
            JumpHeight = settings.JumpHeight,
            JumpDistance = settings.JumpDistance,
            Cells = [.. Enumerable.Range(0, columns * rows).Select(_ => new Nav3dCell(null, 0, Nav3dRoomRole.None, 0, 0))],
            Presets = [.. settings.Agents.Select(a => new Nav3dPreset(a.Name, a.Width, a.Height, a.ClipClass))],
            Roots = roots,
            ColumnStarts = starts,
            Leaves = leaves,
            Clearance = clearance,
            Obstacles = [.. geometry.Obstacles.Select(o => new Nav3dObstacle(
                o.TargetName, o.ClassName, CellOf(o.Bounds), o.HammerId, o.Kind, o.Bounds.Mins, o.Bounds.Maxs))],
            Brushes = [.. overhang.Select(b => b.PlaneFloats())],
            Jumps = jumps,
            Bsp = binding,
        };
        options.Timer?.Mark("level");
        return level;
    }

    /// <summary>A cell's record with its overhanging brushes renumbered into the level's list (the map is ascending, so the list stays sorted).</summary>
    private static byte[] Remap(byte[] record, int[] brushes)
    {
        NavRecord decoded = NavRecord.Decode(record);
        return (decoded with { Brushes = [.. decoded.Brushes.Select(b => brushes[b])] }).Encode();
    }

    private sealed record CellBuild(NavColumns Columns, NavRecordTable Records, int[] Brushes);

    /// <summary>
    /// A cell's records as the stitch takes them: the cell's record numbers
    /// in the order its runs first name them, and for each its bytes as the
    /// level holds them, hashed.
    /// </summary>
    private sealed record CellRecords(int[] Order, HashedRecord[] Records)
    {
        public static CellRecords Of(CellBuild cell)
        {
            bool[] seen = new bool[cell.Records.Count];
            List<int> order = [];
            foreach (NavRun run in cell.Columns.Runs)
            {
                foreach (int record in (ReadOnlySpan<int>)[run.Key.PlayerRecord, run.Key.NpcRecord])
                {
                    if (!seen[record])
                    {
                        seen[record] = true;
                        order.Add(record);
                    }
                }
            }

            HashedRecord[] records = new HashedRecord[order.Count];
            for (int k = 0; k < order.Count; k++)
            {
                int record = order[k];
                if (record != NavRecordTable.BlockedIndex)
                {
                    records[k] = HashedRecord.Of(cell.Records.HasBrushes(record) ? Remap(cell.Records[record], cell.Brushes) : cell.Records[record]);
                }
            }

            return new CellRecords([.. order], records);
        }
    }

    /// <summary>A record's bytes with their hash worked out once, compared by content.</summary>
    private readonly record struct HashedRecord(byte[] Bytes, int Hash)
    {
        public static IEqualityComparer<HashedRecord> Comparer { get; } = new ContentComparer();

        public static HashedRecord Of(byte[] bytes)
        {
            HashCode hash = new();
            hash.AddBytes(bytes);
            return new HashedRecord(bytes, hash.ToHashCode());
        }

        private sealed class ContentComparer : IEqualityComparer<HashedRecord>
        {
            public bool Equals(HashedRecord x, HashedRecord y) => x.Hash == y.Hash && x.Bytes.AsSpan().SequenceEqual(y.Bytes);

            public int GetHashCode(HashedRecord obj) => obj.Hash;
        }
    }
}
