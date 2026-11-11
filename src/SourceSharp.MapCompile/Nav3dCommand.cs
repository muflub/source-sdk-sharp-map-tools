//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Globalization;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap nav3d &lt;map.bsp&gt; [-out &lt;file.nav3d&gt;] [-game &lt;dir&gt;] [-nav-codec &lt;codec&gt;] [-voxel N] [-threads N] [-playspace on|off] [-hull [widths]] [-timings] [-check]</c>:
/// the 3D navigation of any compiled map, built from the <c>.bsp</c> alone,
/// or with <c>-check</c> a built one checked against the map.
/// </summary>
/// <remarks>
/// <para>
/// Named like <c>ssmap map2d</c>, which builds the other per-map sidecar
/// from a compiled map. The work is the library's
/// (<see cref="LevelNavFromBsp"/>); the command reads the map, loads the
/// prop models' hulls from the game's content, and writes the file beside
/// the map (<c>&lt;map&gt;.nav3d</c>) unless <c>-out</c> names it, bound to
/// the map's bytes (<see cref="Nav3dBspBinding"/>).
/// </para>
/// <para>
/// <b>The game.</b> Props are solid by their models' hulls, which only the
/// game's content has: <c>-game</c> names the directory holding
/// <c>gameinfo.txt</c>; without it the map's folder's parent is used when it
/// holds one (a map in <c>&lt;game&gt;/maps/</c>), and otherwise the
/// navigation is built without props and says so.
/// </para>
/// <para>
/// <b>The check.</b> <c>-check</c> builds nothing: it reads the navigation
/// (beside the map, or where <c>-out</c> says), refuses one bound to other
/// bytes, and checks that every place a player stands has ground under it
/// in the map's own collision (<see cref="NavFloorCheck"/>), the props'
/// from the game's content. It prints the count and the first places that
/// have none, and fails when there is any.
/// </para>
/// <para>
/// <b>The hull navigation.</b> <c>-hull</c> writes a <c>.navhull</c>
/// (<see cref="HullNavFromBsp"/>) in place of the <c>.nav3d</c>: the same
/// grid, kept for bodies of the widths given (comma-separated, default 32
/// and 48), which merges into boxes and so holds a map whose <c>.nav3d</c>
/// would be too large, a jump map's. It goes beside the map as
/// <c>&lt;map&gt;.navhull</c> unless <c>-out</c> names it; <c>-check</c>
/// does not read one.
/// </para>
/// </remarks>
public static class Nav3dCommand
{
    private const string Usage = "usage: ssmap nav3d <map.bsp> [-out <file.nav3d>] [-game <dir>] [-nav-codec <codec>] [-voxel <units>] [-threads <n>] [-playspace on|off] [-hull [widths]] [-timings] [-check]";

    /// <summary>How many places without ground <c>-check</c> lists before it says how many more there are.</summary>
    public const int ListedFindings = 20;

    /// <summary>Runs <c>ssmap nav3d</c>.</summary>
    /// <param name="disk">Where the map, the game and the output live.</param>
    /// <param name="steamRoots">Where Steam keeps its games, for a <c>gameinfo.txt</c> that mounts <c>|appid_N|</c> paths.</param>
    /// <param name="args">The arguments after <c>nav3d</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the reads, the build and the write.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IFileSystem disk, IReadOnlyList<VPath> steamRoots, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(steamRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? target = null;
        string? outText = null;
        string? game = null;
        NavCompression compression = LevelNavFromPack.DefaultCompression;
        int threads = Environment.ProcessorCount;
        float voxel = NavSettings.DefaultVoxelSize;
        bool check = false;
        bool playSpace = true;
        bool timings = false;
        IReadOnlyList<float>? hull = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Count;
            if (Is(arg, "out") && hasValue && outText is null)
            {
                outText = args[++i];
            }
            else if (Is(arg, "game") && hasValue && game is null)
            {
                game = args[++i];
            }
            else if (Is(arg, "nav-codec") && hasValue && NavCompression.TryParse(args[i + 1], out NavCompression codec))
            {
                compression = codec;
                i++;
            }
            else if (Is(arg, "voxel") && hasValue && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                && v > 0 && float.IsFinite(v))
            {
                voxel = v;
                i++;
            }
            else if (Is(arg, "threads") && hasValue && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1)
            {
                threads = n;
                i++;
            }
            else if (Is(arg, "playspace") && hasValue && args[i + 1] is "on" or "off")
            {
                playSpace = args[++i] == "on";
            }
            else if (Is(arg, "hull") && hull is null)
            {
                hull = HullNavFromBsp.DefaultWidths;
                if (hasValue && TryWidths(args[i + 1], out float[] widths))
                {
                    hull = widths;
                    i++;
                }
            }
            else if (Is(arg, "timings") && !timings)
            {
                timings = true;
            }
            else if (Is(arg, "check") && !check)
            {
                check = true;
            }
            else if (target is null && !arg.StartsWith('-'))
            {
                target = arg;
            }
            else
            {
                await output.WriteLineAsync(Usage).ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        if (target is null
            || (hull is not null && check)
            || !VPath.TryCreate(Path.GetFullPath(target), out VPath mapPath)
            || (outText is not null && !VPath.TryCreate(Path.GetFullPath(outText), out _)))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        VPath navPath = outText is not null ? VPath.Create(Path.GetFullPath(outText))
            : hull is not null ? VPath.Create(Path.ChangeExtension(mapPath.ToString(), NavHullFormat.Extension))
            : NavPathOf(mapPath);
        string? gameDirectory = game is not null ? Path.GetFullPath(game) : await DefaultGameAsync(disk, mapPath, cancellationToken).ConfigureAwait(false);
        if (gameDirectory is null)
        {
            await output.WriteLineAsync(
                $"ssmap nav3d: warning: no -game and no gameinfo.txt above {HostPaths.Display(mapPath)}: props are left out of the navigation")
                .ConfigureAwait(false);
        }

        if (check)
        {
            return await CheckAsync(disk, steamRoots, mapPath, navPath, gameDirectory, threads, "ssmap nav3d", output, cancellationToken).ConfigureAwait(false);
        }

        NavBuildTimer? timer = timings ? new NavBuildTimer(() => Environment.CpuUsage.TotalTime) : null;
        BspNavOptions options = Options(voxel, threads) with { PlaySpace = playSpace, Timer = timer };
        int exit = hull is not null
            ? await BuildAndWriteHullAsync(disk, steamRoots, mapPath, navPath, gameDirectory, compression, hull, options, "ssmap nav3d", output, cancellationToken)
                .ConfigureAwait(false)
            : await BuildAndWriteAsync(disk, steamRoots, mapPath, navPath, gameDirectory, compression, options, "ssmap nav3d", output, cancellationToken)
                .ConfigureAwait(false);
        if (timer is not null)
        {
            foreach (string line in TimingLines(timer))
            {
                await output.WriteLineAsync($"ssmap nav3d: {line}").ConfigureAwait(false);
            }
        }

        return exit;
    }

    /// <summary>
    /// A timed build's report (<c>-timings</c>): one line per phase with its
    /// wall time, its CPU time and how many cores were busy on average over
    /// it (CPU over wall), then the cells' work and the slowest cell, and the
    /// total.
    /// </summary>
    /// <param name="timer">The build's timer.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> TimingLines(NavBuildTimer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        List<string> lines = [];
        TimeSpan cpuTotal = TimeSpan.Zero;
        bool anyCpu = false;
        foreach (NavPhaseTime phase in timer.Phases)
        {
            string cpu = phase.Cpu is { } used
                ? string.Create(CultureInfo.InvariantCulture, $" {used.TotalSeconds,8:0.000} s cpu {Busy(used, phase.Wall),5:0.0} busy")
                : string.Empty;
            anyCpu |= phase.Cpu is not null;
            cpuTotal += phase.Cpu ?? TimeSpan.Zero;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"time {phase.Name,-13} {phase.Wall.TotalSeconds,8:0.000} s wall{cpu}"));
        }

        (int slowCell, TimeSpan slowTime) = timer.SlowestCell;
        if (timer.Cells > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"time cells: {timer.Cells:N0} built, {timer.CellWork.TotalSeconds:0.000} s of work, slowest cell {slowCell} {slowTime.TotalSeconds:0.000} s"));
        }

        string total = anyCpu
            ? string.Create(CultureInfo.InvariantCulture, $" {cpuTotal.TotalSeconds,8:0.000} s cpu {Busy(cpuTotal, timer.Total),5:0.0} busy")
            : string.Empty;
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"time {"total",-13} {timer.Total.TotalSeconds,8:0.000} s wall{total}"));
        return lines;

        static double Busy(TimeSpan cpu, TimeSpan wall) => wall > TimeSpan.Zero ? cpu / wall : 0;
    }

    /// <summary>
    /// Checks a built navigation against its map: every place a player
    /// stands has ground under it in the map's collision (<see cref="NavFloorCheck"/>).
    /// </summary>
    /// <param name="disk">Where the map, the game and the navigation live.</param>
    /// <param name="steamRoots">Where Steam keeps its games.</param>
    /// <param name="mapPath">The <c>.bsp</c>.</param>
    /// <param name="navPath">The <c>.nav3d</c> built from it.</param>
    /// <param name="gameDirectory">The directory holding <c>gameinfo.txt</c>, or null to check without props.</param>
    /// <param name="threads">How many leaves are checked at once.</param>
    /// <param name="tool">The prefix for each line.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="Program.ExitSuccess"/> when every place has ground, else <see cref="RoomCommands.ExitFailed"/>.</returns>
    public static async Task<int> CheckAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> steamRoots,
        VPath mapPath,
        VPath navPath,
        string? gameDirectory,
        int threads,
        string tool,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(steamRoots);
        ArgumentNullException.ThrowIfNull(output);
        Stopwatch clock = Stopwatch.StartNew();
        if (await ReadMapAsync(disk, mapPath, tool, output, cancellationToken).ConfigureAwait(false) is not { } map)
        {
            return RoomCommands.ExitFailed;
        }

        Nav3dReader nav;
        try
        {
            byte[] bytes;
            await using (Stream stream = await disk.OpenReadAsync(navPath, cancellationToken).ConfigureAwait(false))
            {
                using MemoryStream copy = new();
                await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                bytes = copy.ToArray();
            }

            nav = Nav3dReader.Open(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await output.WriteLineAsync($"{tool}: {HostPaths.Display(navPath)}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        if (!nav.MatchesBsp(map.Binding))
        {
            await output.WriteLineAsync($"{tool}: {HostPaths.Display(navPath)} was not built from {HostPaths.Display(mapPath)}: build it again").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        if (await LoadModelsAsync(disk, steamRoots, map.Bsp, gameDirectory, tool, output, cancellationToken).ConfigureAwait(false) is not { } models)
        {
            return RoomCommands.ExitFailed;
        }

        NavFloorReport report = NavFloorCheck.Check(
            nav, map.Bsp, models.Hull, models.Pieces, new NavFloorCheckOptions { MaxDegree = Math.Max(1, threads) }, cancellationToken);
        foreach (string warning in report.Warnings)
        {
            await output.WriteLineAsync($"{tool}: warning: {warning}").ConfigureAwait(false);
        }

        foreach (NavFloorFinding finding in report.Missing.Take(ListedFindings))
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{tool}: no ground under ({finding.Position.X:0.##} {finding.Position.Y:0.##} {finding.Position.Z:0.##}), leaf {finding.Leaf}")).ConfigureAwait(false);
        }

        if (report.Missing.Count > ListedFindings)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{tool}: ... and {report.Missing.Count - ListedFindings:N0} more")).ConfigureAwait(false);
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{tool}: checked {HostPaths.Display(navPath)}: {report.Standable:N0} places a player stands, {report.Missing.Count:N0} with no ground within {report.Depth:0.##} units under its box, {clock.Elapsed.TotalSeconds:0.0} s"))
            .ConfigureAwait(false);
        return report.Missing.Count == 0 ? Program.ExitSuccess : RoomCommands.ExitFailed;
    }

    /// <summary>The grid a command line asks for: the defaults with its voxel and thread count.</summary>
    /// <param name="voxel">The voxel's edge in units; it must divide the 256-unit cell.</param>
    /// <param name="threads">How many cells are built at once.</param>
    /// <returns>The options.</returns>
    public static BspNavOptions Options(float voxel, int threads) => new()
    {
        Settings = NavSettings.Default with { VoxelSize = voxel },
        MaxDegree = Math.Max(1, threads),
    };

    /// <summary>Where a map's navigation goes by default: <c>&lt;map&gt;.nav3d</c> beside it.</summary>
    /// <param name="mapPath">The map.</param>
    /// <returns>The path.</returns>
    public static VPath NavPathOf(VPath mapPath)
    {
        string full = mapPath.ToString();
        return VPath.Create(Path.ChangeExtension(full, Nav3dFormat.Extension));
    }

    /// <summary>
    /// Reads a compiled map, builds its navigation and writes it: the one
    /// path <c>ssmap nav3d</c> and <c>ssmap all</c> share.
    /// </summary>
    /// <param name="disk">Where the map, the game and the output live.</param>
    /// <param name="steamRoots">Where Steam keeps its games.</param>
    /// <param name="mapPath">The <c>.bsp</c>.</param>
    /// <param name="navPath">Where the <c>.nav3d</c> goes.</param>
    /// <param name="gameDirectory">The directory holding <c>gameinfo.txt</c>, or null to build without props.</param>
    /// <param name="compression">How the file is stored.</param>
    /// <param name="options">The grid and the thread count (<see cref="Options"/>).</param>
    /// <param name="tool">The prefix for each line (<c>ssmap nav3d</c>, <c>ssmap all</c>).</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="Program.ExitSuccess"/>, or <see cref="RoomCommands.ExitFailed"/> with a line saying why.</returns>
    public static async Task<int> BuildAndWriteAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> steamRoots,
        VPath mapPath,
        VPath navPath,
        string? gameDirectory,
        NavCompression compression,
        BspNavOptions options,
        string tool,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(steamRoots);
        ArgumentNullException.ThrowIfNull(output);
        Stopwatch clock = Stopwatch.StartNew();
        string display = HostPaths.Display(mapPath);
        if (await ReadMapAsync(disk, mapPath, tool, output, cancellationToken).ConfigureAwait(false) is not { } map)
        {
            return RoomCommands.ExitFailed;
        }

        options.Timer?.Mark("read");
        if (await LoadModelsAsync(disk, steamRoots, map.Bsp, gameDirectory, tool, output, cancellationToken).ConfigureAwait(false) is not { } models)
        {
            return RoomCommands.ExitFailed;
        }

        options.Timer?.Mark("models");
        BspData bsp = map.Bsp;
        Nav3dBspBinding binding = map.Binding;
        BspNavResult result;
        try
        {
            result = await LevelNavFromBsp.BuildAsync(
                bsp, binding, models.Hull, options with { ModelPieces = models.Pieces },
                cancellationToken).ConfigureAwait(false);
        }
        catch (NavBuildException exception)
        {
            await output.WriteLineAsync($"{tool}: {display}: no navigation: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        foreach (string warning in result.Warnings)
        {
            await output.WriteLineAsync($"{tool}: warning: {warning}").ConfigureAwait(false);
        }

        long length;
        try
        {
            length = await LevelNavPlan.WriteAsync(disk, navPath, result.Level, compression, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"{tool}: cannot write {HostPaths.Display(navPath)}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        options.Timer?.Mark("write");
        await output.WriteLineAsync($"{tool}: wrote {HostPaths.Display(navPath)}: {Summary(result.Level, length, clock.Elapsed)}").ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// Reads a compiled map, builds its hull navigation for the widths given
    /// and writes it (<c>ssmap nav3d -hull</c>).
    /// </summary>
    /// <param name="disk">Where the map, the game and the output live.</param>
    /// <param name="steamRoots">Where Steam keeps its games.</param>
    /// <param name="mapPath">The <c>.bsp</c>.</param>
    /// <param name="navPath">Where the <c>.navhull</c> goes.</param>
    /// <param name="gameDirectory">The directory holding <c>gameinfo.txt</c>, or null to build without props.</param>
    /// <param name="compression">How the file's image is stored.</param>
    /// <param name="widths">The body widths to build ceilings for.</param>
    /// <param name="options">The grid.</param>
    /// <param name="tool">The name the report's lines start with.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see cref="Program.ExitSuccess"/>, or <see cref="RoomCommands.ExitFailed"/> with a line saying why.</returns>
    public static async Task<int> BuildAndWriteHullAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> steamRoots,
        VPath mapPath,
        VPath navPath,
        string? gameDirectory,
        NavCompression compression,
        IReadOnlyList<float> widths,
        BspNavOptions options,
        string tool,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(steamRoots);
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        Stopwatch clock = Stopwatch.StartNew();
        if (await ReadMapAsync(disk, mapPath, tool, output, cancellationToken).ConfigureAwait(false) is not { } map)
        {
            return RoomCommands.ExitFailed;
        }

        options.Timer?.Mark("read");
        if (await LoadModelsAsync(disk, steamRoots, map.Bsp, gameDirectory, tool, output, cancellationToken).ConfigureAwait(false) is not { } models)
        {
            return RoomCommands.ExitFailed;
        }

        options.Timer?.Mark("models");
        HullNavResult result;
        byte[] bytes;
        try
        {
            result = await HullNavFromBsp.BuildAsync(
                map.Bsp, map.Binding, models.Hull, widths, options with { ModelPieces = models.Pieces }, cancellationToken).ConfigureAwait(false);
            bytes = NavHullWriter.Write(result.Level, compression);
        }
        catch (Exception exception) when (exception is NavBuildException or ArgumentException)
        {
            await output.WriteLineAsync($"{tool}: {HostPaths.Display(mapPath)}: no navigation: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        foreach (string warning in result.Warnings)
        {
            await output.WriteLineAsync($"{tool}: warning: {warning}").ConfigureAwait(false);
        }

        try
        {
            await disk.ReplaceAsync(
                navPath, async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"{tool}: cannot write {HostPaths.Display(navPath)}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        options.Timer?.Mark("write");
        await output.WriteLineAsync($"{tool}: wrote {HostPaths.Display(navPath)}: {HullSummary(result.Level, bytes.Length, clock.Elapsed)}").ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>The line <c>ssmap nav3d -hull</c> ends with: the file's size, its grid, its boxes and links, and the time.</summary>
    /// <param name="level">The hull navigation.</param>
    /// <param name="bytes">The file's length.</param>
    /// <param name="elapsed">How long the read, build and write took.</param>
    /// <returns>The line.</returns>
    public static string HullSummary(NavHullLevel level, long bytes, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(level);
        int placed = Enumerable.Range(0, level.Columns * level.Rows).Count(c => level.CellStarts[c + 1] > level.CellStarts[c]);
        int floors = level.Boxes.Count(b => (level.Keys[(int)b.Key].Flags & NavHullFlags.Walkable) != 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{bytes:N0} bytes, {placed} of {level.Columns * level.Rows} cells ({level.Columns} x {level.Rows}, {level.ColumnVoxels} voxels up), "
            + $"{level.Boxes.Length:N0} boxes, {level.Keys.Count:N0} keys, {floors:N0} walkable floors, {level.Jumps.Count:N0} jump links, "
            + $"widths {string.Join(", ", level.Widths)}, {elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>Reads <c>-hull</c>'s widths: comma-separated, each a finite number above 0.</summary>
    private static bool TryWidths(string text, out float[] widths)
    {
        string[] parts = text.Split(',');
        widths = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out widths[i]) || !(widths[i] > 0) || !float.IsFinite(widths[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a compiled map and its binding, or writes a line saying why it cannot.</summary>
    private static async Task<(BspData Bsp, Nav3dBspBinding Binding)?> ReadMapAsync(
        IFileSystem disk, VPath mapPath, string tool, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            await using (Stream stream = await disk.OpenReadAsync(mapPath, cancellationToken).ConfigureAwait(false))
            {
                using MemoryStream copy = new();
                await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                bytes = copy.ToArray();
            }

            Nav3dBspBinding binding = Nav3dBspBinding.Of(bytes);
            BspData bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false), cancellationToken).ConfigureAwait(false);
            return (bsp, binding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidBspException)
        {
            await output.WriteLineAsync($"{tool}: {HostPaths.Display(mapPath)}: {exception.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// The hulls of a map's prop models and the collision pieces of its
    /// static props' models, from the game's content; none without a game.
    /// Null, after a line saying why, when the game cannot be mounted.
    /// </summary>
    private static async Task<ModelCollision?> LoadModelsAsync(
        IFileSystem disk, IReadOnlyList<VPath> steamRoots, BspData bsp, string? gameDirectory, string tool, TextWriter output,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> models = LevelNavFromBsp.Models(bsp);
        IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?> hulls = new Dictionary<string, (Vec3, Vec3)?>();
        IReadOnlyDictionary<string, IReadOnlyList<NavPiece>?> pieces = new Dictionary<string, IReadOnlyList<NavPiece>?>();
        if (gameDirectory is not null && models.Count > 0)
        {
            GameContentMounter.Result mounted;
            try
            {
                mounted = await VbspCommand.MountGameAsync(disk, gameDirectory, VbspHost.SteamFor(disk, steamRoots), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                await output.WriteLineAsync($"{tool}: cannot mount {gameDirectory}: {exception.Message}").ConfigureAwait(false);
                return null;
            }

            await using (mounted.Content.ConfigureAwait(false))
            {
                await VbspCommand.WriteSkippedAsync(mounted, tool, output).ConfigureAwait(false);
                hulls = await NavModelBounds.LoadAsync(mounted.Content, models, cancellationToken).ConfigureAwait(false);
                pieces = await NavModelBounds.LoadPiecesAsync(mounted.Content, NavModelBounds.StaticPropModels(bsp), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new ModelCollision(
            m => hulls.TryGetValue(m, out (Vec3, Vec3)? hull) ? hull : null,
            m => pieces.TryGetValue(m, out IReadOnlyList<NavPiece>? found) ? found : null);
    }

    /// <summary>One line of a built navigation's numbers.</summary>
    /// <param name="level">The navigation.</param>
    /// <param name="bytes">The file's length.</param>
    /// <param name="elapsed">How long it took.</param>
    /// <returns>The line.</returns>
    public static string Summary(Nav3dLevel level, long bytes, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(level);
        int placed = level.Roots.Count(r => r >= 0);
        int floors = level.Leaves.Count(l => l.IsWalkable(Nav3dClipClass.Player));
        int ladders = level.Leaves.Count(l => (l.Flags & Nav3dLeafFlags.Ladder) != 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{bytes:N0} bytes, {placed} of {level.Columns * level.Rows} cells ({level.Columns} x {level.Rows}, {level.CellHeight(Array.FindIndex(level.Roots, r => r >= 0) is int c and >= 0 ? c : 0)} voxels up), "
            + $"{level.Leaves.Length:N0} leaves, {floors:N0} walkable floors, {level.Jumps.Count:N0} jump links, "
            + $"{level.Obstacles.Count} obstacles, {ladders:N0} ladder leaves, {elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>The game a map in <c>&lt;game&gt;/maps/</c> belongs to: its folder's parent, when that holds a <c>gameinfo.txt</c>.</summary>
    private static async Task<string?> DefaultGameAsync(IFileSystem disk, VPath mapPath, CancellationToken cancellationToken)
    {
        string? folder = Path.GetDirectoryName(Path.GetDirectoryName(HostPaths.Display(mapPath)));
        return folder is not null && await disk.ExistsAsync(VPath.Create(Path.Combine(folder, "gameinfo.txt")), cancellationToken).ConfigureAwait(false)
            ? folder
            : null;
    }

    /// <summary>A map's model collision: hulls by path, and static props' pieces by path.</summary>
    private sealed record ModelCollision(Func<string, (Vec3 Mins, Vec3 Maxs)?> Hull, Func<string, IReadOnlyList<NavPiece>?> Pieces);

    private static bool Is(string arg, string name) =>
        string.Equals(arg, "-" + name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase);
}
