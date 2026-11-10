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
using SourceSharp.MapPreview;
using SourceSharp.MapPreview.Cameras;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapPreview.Report;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Movement;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap preview &lt;map.bsp&gt; [-game &lt;dir&gt;] [-out &lt;dir&gt;] [-size WxH] [-individual] [-no-sheet] [-hdr] [-tools] [-exposure X] [-threads N]</c>:
/// renders a compiled map's standard views with its textures, models and
/// baked lighting, and writes a contact sheet and a JSON report.
/// </summary>
/// <remarks>
/// <para>
/// The output folder (default <c>&lt;map&gt;.preview/</c> beside the map) gets
/// <c>sheet.png</c>, <c>index.json</c>, and with <c>-individual</c> one PNG per
/// view under <c>views/</c>. Each file is replaced atomically, so a cancelled
/// run never leaves a half-written image.
/// </para>
/// <para>
/// <b>The game.</b> Textures and models come from the game's content:
/// <c>-game</c> names the directory holding <c>gameinfo.txt</c>, defaulting
/// to the map's folder's parent when that holds one (a map in
/// <c>&lt;game&gt;/maps/</c>). Without a game the preview still renders, with
/// every material missing, which is still useful for layout and lighting.
/// A TF2 game directory selects TF2's player size for the scale figure and
/// eye height.
/// </para>
/// </remarks>
public static class PreviewCommand
{
    private const string Usage =
        "usage: ssmap preview <map.bsp> [-game <dir>] [-out <dir>] [-size WxH] [-individual] [-no-sheet] [-hdr] [-tools] [-exposure <x>] [-threads <n>] [-camera \"x y z pitch yaw [fov] [label]\"]... [-movement [class]] [-airborne <flights>]";

    /// <summary>Runs the command.</summary>
    /// <param name="disk">Where the map, the game and the output live.</param>
    /// <param name="steamRoots">Steam library roots for <c>|appid_N|</c> search paths.</param>
    /// <param name="args">The arguments after <c>preview</c>.</param>
    /// <param name="output">Where progress and errors go.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> steamRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(steamRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (!TryParse(args, out Arguments? parsed))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string mapText = Path.GetFullPath(parsed.Map!);
        if (!VPath.TryCreate(mapText, out VPath mapPath))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string mapName = Path.GetFileNameWithoutExtension(mapText);
        string outText = parsed.Out is not null
            ? Path.GetFullPath(parsed.Out)
            : Path.Combine(Path.GetDirectoryName(mapText)!, mapName + ".preview");

        BspData bsp;
        try
        {
            await using Stream stream = await disk.OpenReadAsync(mapPath, cancellationToken).ConfigureAwait(false);
            bsp = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidBspException)
        {
            await output.WriteLineAsync($"ssmap preview: {HostPaths.Display(mapPath)}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        string? gameDirectory = parsed.Game is not null
            ? Path.GetFullPath(parsed.Game)
            : await DefaultGameAsync(disk, mapText, cancellationToken).ConfigureAwait(false);

        ContentFileSystem content;
        if (gameDirectory is null)
        {
            await output.WriteLineAsync(
                $"ssmap preview: warning: no -game and no gameinfo.txt above {HostPaths.Display(mapPath)}: every material draws as missing")
                .ConfigureAwait(false);
            content = new ContentFileSystem([]);
        }
        else
        {
            try
            {
                GameContentMounter.Result mounted = await VbspCommand.MountGameAsync(
                    disk, gameDirectory, VbspHost.SteamFor(disk, steamRoots), cancellationToken).ConfigureAwait(false);
                await VbspCommand.WriteSkippedAsync(mounted, "ssmap preview", output).ConfigureAwait(false);
                content = mounted.Content;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                await output.WriteLineAsync($"ssmap preview: cannot mount {gameDirectory}: {exception.Message}").ConfigureAwait(false);
                return RoomCommands.ExitFailed;
            }
        }

        await using (content.ConfigureAwait(false))
        {
            PreviewOptions options = Options(parsed, IsTf2(gameDirectory));
            Stopwatch clock = Stopwatch.StartNew();
            PreviewResult result = await new PreviewSession(content).RenderAsync(bsp, mapName, options, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (PreviewImage image in result.Images)
                {
                    await WriteAsync(disk, Path.Combine(outText, image.FileName), image.Png, cancellationToken).ConfigureAwait(false);
                }

                await WriteAsync(disk, Path.Combine(outText, "index.json"), result.ReportJson(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync($"ssmap preview: cannot write {outText}: {exception.Message}").ConfigureAwait(false);
                return RoomCommands.ExitFailed;
            }

            await WriteWarningsAsync(output, result.Report).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"ssmap preview: {result.Report.Cameras.Count} views, {result.Report.Markers.Count} markers, "
                + $"{result.Report.Checks.MissingMaterials.Count} missing materials, {result.Report.Checks.MissingModels.Count} missing models, "
                + $"{clock.Elapsed.TotalSeconds:0.0} s -> {outText}")).ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    /// <summary>
    /// Writes the report's warnings a map's author should act on, one line
    /// each: the collision the movement check could not take as the engine
    /// would (a solid static prop with no collision model, which the engine
    /// makes non-solid; a model the content lacks).
    /// </summary>
    /// <param name="output">Where the lines go.</param>
    /// <param name="report">The preview's report.</param>
    /// <returns>A task that completes when the lines are written.</returns>
    public static async Task WriteWarningsAsync(TextWriter output, PreviewReport report)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(report);
        foreach (string warning in report.Checks.Movement?.CollisionWarnings ?? [])
        {
            await output.WriteLineAsync($"ssmap preview: warning: {warning}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a game directory is TF2's: its folder is named <c>tf</c> or a
    /// mod folder ending <c>_tf</c> (the SDK's <c>mod_tf</c>).
    /// </summary>
    /// <param name="gameDirectory">The game directory, or null.</param>
    /// <returns>True for TF2.</returns>
    public static bool IsTf2(string? gameDirectory)
    {
        if (gameDirectory is null)
        {
            return false;
        }

        string name = Path.GetFileName(gameDirectory.TrimEnd('/', '\\')).ToLowerInvariant();
        return name == "tf" || name.EndsWith("_tf", StringComparison.Ordinal);
    }

    private static PreviewOptions Options(Arguments a, bool tf2)
    {
        PreviewOptions o = PreviewOptions.Default with
        {
            Sheet = a.Sheet,
            Individual = a.Individual || !a.Sheet,
            Scene = new PreviewSceneOptions { Hdr = a.Hdr, IncludeTools = a.Tools },
            Render = RenderSettings.Default with { Exposure = a.Exposure, MaxDegreeOfParallelism = a.Threads },
        };
        if (a.Size is { } size)
        {
            o = o with { ViewSize = size };
        }

        if (a.Cameras.Count > 0)
        {
            o = o with { ExtraCameras = a.Cameras };
        }

        if (a.Movement)
        {
            o = o with
            {
                Movement = MovementFor(a.MovementClass, tf2),
                Airborne = a.Flights == 0 ? null : AirborneOptions.Default with { Trajectories = a.Flights ?? AirborneOptions.Default.Trajectories },
            };
        }

        if (tf2)
        {
            // TF2's view vectors: a 48 x 48 x 82 hull with the eye at 72.
            o = o with
            {
                PlayerWidth = 48f,
                PlayerHeight = 82f,
                Cameras = o.Cameras with { EyeHeight = 72f },
            };
        }

        return o;
    }

    private static async Task WriteAsync(IFileSystem disk, string path, byte[] bytes, CancellationToken cancellationToken) =>
        await disk.ReplaceAsync(
            VPath.Create(path),
            async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private static async Task<string?> DefaultGameAsync(IFileSystem disk, string mapText, CancellationToken cancellationToken)
    {
        string? folder = Path.GetDirectoryName(Path.GetDirectoryName(mapText));
        return folder is not null && await disk.ExistsAsync(VPath.Create(Path.Combine(folder, "gameinfo.txt")), cancellationToken).ConfigureAwait(false)
            ? folder
            : null;
    }

    private static bool TryParse(IReadOnlyList<string> args, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Arguments? parsed)
    {
        parsed = null;
        Arguments a = new();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Count;
            if (Is(arg, "game") && hasValue && a.Game is null)
            {
                a.Game = args[++i];
            }
            else if (Is(arg, "out") && hasValue && a.Out is null)
            {
                a.Out = args[++i];
            }
            else if (Is(arg, "size") && hasValue && a.Size is null && TryParseSize(args[++i], out (int, int) size))
            {
                a.Size = size;
            }
            else if (Is(arg, "exposure") && hasValue
                && float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float exposure) && exposure > 0)
            {
                a.Exposure = exposure;
            }
            else if (Is(arg, "threads") && hasValue
                && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads) && threads > 0)
            {
                a.Threads = threads;
            }
            else if (Is(arg, "camera") && hasValue && TryParseCamera(args[++i], a.Cameras.Count + 1, out PreviewCamera? camera))
            {
                a.Cameras.Add(camera);
            }
            else if (Is(arg, "movement"))
            {
                // An optional class follows: a TF2 class name, or hl2mp.
                a.Movement = true;
                if (hasValue && !args[i + 1].StartsWith('-') && IsMovementClass(args[i + 1]))
                {
                    a.MovementClass = args[++i];
                }
            }
            else if (Is(arg, "airborne") && hasValue
                && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flights) && flights >= 0)
            {
                a.Movement = true;
                a.Flights = flights;
            }
            else if (Is(arg, "individual"))
            {
                a.Individual = true;
            }
            else if (Is(arg, "no-sheet"))
            {
                a.Sheet = false;
            }
            else if (Is(arg, "hdr"))
            {
                a.Hdr = true;
            }
            else if (Is(arg, "tools"))
            {
                a.Tools = true;
            }
            else if (a.Map is null && !arg.StartsWith('-'))
            {
                a.Map = arg;
            }
            else
            {
                return false;
            }
        }

        if (a.Map is null)
        {
            return false;
        }

        parsed = a;
        return true;
    }

    /// <summary>
    /// Parses a custom camera, <c>"x y z pitch yaw [fov] [label words]"</c>:
    /// a position, Source angles (pitch positive down), an optional
    /// horizontal field of view (default 90), and an optional description.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="number">The camera's number, for its id (<c>cam1</c>, ...).</param>
    /// <param name="camera">The camera, sized later to the preview's view size.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParseCamera(string text, int number, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PreviewCamera? camera)
    {
        camera = null;
        string[] parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            return false;
        }

        float[] numbers = new float[6];
        numbers[5] = 90f;
        int count = 0;
        // Parse into a temporary: a failed parse writes zero, which would wipe
        // the default field of view when the sixth word starts the label.
        while (count < parts.Length && count < 6
            && float.TryParse(parts[count], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            numbers[count++] = value;
        }

        if (count < 5 || (count == 6 && numbers[5] is < 10f or > 170f))
        {
            return false;
        }

        string label = string.Join(' ', parts.Skip(count));
        string id = "cam" + number.ToString(CultureInfo.InvariantCulture);
        camera = PreviewCamera.FromAngles(
            id,
            CameraKind.Custom,
            new MapFormats.Geometry.Vec3(numbers[0], numbers[1], numbers[2]),
            numbers[3],
            numbers[4],
            numbers[5],
            1024,
            768,
            label.Length > 0 ? label : string.Create(CultureInfo.InvariantCulture, $"custom at ({numbers[0]:0} {numbers[1]:0} {numbers[2]:0})"));
        return true;
    }

    /// <summary>
    /// The movement profile for <c>-movement</c>: the named class, else a TF2
    /// soldier for a TF2 game and Half-Life 2 Deathmatch's player otherwise.
    /// </summary>
    /// <param name="playerClass">The class named after <c>-movement</c>, or null.</param>
    /// <param name="tf2">Whether the game is TF2.</param>
    /// <returns>The profile.</returns>
    public static MovementProfile MovementFor(string? playerClass, bool tf2) =>
        playerClass is null ? (tf2 ? MovementProfile.Tf2("soldier") : MovementProfile.Hl2mp)
        : playerClass.Equals("hl2mp", StringComparison.OrdinalIgnoreCase) ? MovementProfile.Hl2mp
        : MovementProfile.Tf2(playerClass);

    private static bool IsMovementClass(string name)
    {
        if (name.Equals("hl2mp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            _ = MovementProfile.Tf2(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parses <c>WxH</c>, each side 64 to 2048 pixels.</summary>
    /// <param name="text">The text.</param>
    /// <param name="size">The size.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParseSize(string text, out (int Width, int Height) size)
    {
        size = default;
        string[] parts = (text ?? string.Empty).Split('x', 'X');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            && w is >= 64 and <= 2048 && h is >= 64 and <= 2048)
        {
            size = (w, h);
            return true;
        }

        return false;
    }

    private static bool Is(string arg, string name) =>
        string.Equals(arg, "-" + name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase);

    private sealed class Arguments
    {
        public string? Map { get; set; }

        public string? Game { get; set; }

        public string? Out { get; set; }

        public (int Width, int Height)? Size { get; set; }

        public float Exposure { get; set; } = 1f;

        public int Threads { get; set; } = -1;

        public bool Individual { get; set; }

        public bool Sheet { get; set; } = true;

        public bool Hdr { get; set; }

        public bool Tools { get; set; }

        public List<PreviewCamera> Cameras { get; } = [];

        public bool Movement { get; set; }

        public string? MovementClass { get; set; }

        public int? Flights { get; set; }
    }
}
