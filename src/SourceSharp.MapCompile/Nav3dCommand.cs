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
/// <c>ssmap nav3d &lt;map.bsp&gt; [-out &lt;file.nav3d&gt;] [-game &lt;dir&gt;] [-nav-codec &lt;codec&gt;] [-voxel N] [-threads N]</c>:
/// the 3D navigation of any compiled map, built from the <c>.bsp</c> alone.
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
/// </remarks>
public static class Nav3dCommand
{
    private const string Usage = "usage: ssmap nav3d <map.bsp> [-out <file.nav3d>] [-game <dir>] [-nav-codec <codec>] [-voxel <units>] [-threads <n>]";

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
            || !VPath.TryCreate(Path.GetFullPath(target), out VPath mapPath)
            || (outText is not null && !VPath.TryCreate(Path.GetFullPath(outText), out _)))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        VPath navPath = outText is null ? NavPathOf(mapPath) : VPath.Create(Path.GetFullPath(outText));
        string? gameDirectory = game is not null ? Path.GetFullPath(game) : await DefaultGameAsync(disk, mapPath, cancellationToken).ConfigureAwait(false);
        if (gameDirectory is null)
        {
            await output.WriteLineAsync(
                $"ssmap nav3d: warning: no -game and no gameinfo.txt above {HostPaths.Display(mapPath)}: props are left out of the navigation")
                .ConfigureAwait(false);
        }

        return await BuildAndWriteAsync(
            disk, steamRoots, mapPath, navPath, gameDirectory, compression, Options(voxel, threads), "ssmap nav3d", output, cancellationToken)
            .ConfigureAwait(false);
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
        BspData bsp;
        Nav3dBspBinding binding;
        try
        {
            byte[] bytes;
            await using (Stream stream = await disk.OpenReadAsync(mapPath, cancellationToken).ConfigureAwait(false))
            {
                using MemoryStream copy = new();
                await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                bytes = copy.ToArray();
            }

            binding = Nav3dBspBinding.Of(bytes);
            bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidBspException)
        {
            await output.WriteLineAsync($"{tool}: {display}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        IReadOnlyList<string> models = LevelNavFromBsp.Models(bsp);
        IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?> hulls = new Dictionary<string, (Vec3, Vec3)?>();
        IReadOnlyDictionary<string, IReadOnlyList<(Vec3 Mins, Vec3 Maxs)>?> pieces = new Dictionary<string, IReadOnlyList<(Vec3, Vec3)>?>();
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
                return RoomCommands.ExitFailed;
            }

            await using (mounted.Content.ConfigureAwait(false))
            {
                await VbspCommand.WriteSkippedAsync(mounted, tool, output).ConfigureAwait(false);
                hulls = await NavModelBounds.LoadAsync(mounted.Content, models, cancellationToken).ConfigureAwait(false);
                pieces = await NavModelBounds.LoadPiecesAsync(mounted.Content, NavModelBounds.StaticPropModels(bsp), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        BspNavResult result;
        try
        {
            result = await LevelNavFromBsp.BuildAsync(
                bsp, binding, m => hulls.TryGetValue(m, out (Vec3, Vec3)? hull) ? hull : null,
                options with { ModelPieces = m => pieces.TryGetValue(m, out IReadOnlyList<(Vec3, Vec3)>? found) ? found : null },
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

        await output.WriteLineAsync($"{tool}: wrote {HostPaths.Display(navPath)}: {Summary(result.Level, length, clock.Elapsed)}").ConfigureAwait(false);
        return Program.ExitSuccess;
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

    private static bool Is(string arg, string name) =>
        string.Equals(arg, "-" + name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase);
}
