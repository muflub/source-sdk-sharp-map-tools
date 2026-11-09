//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Movement;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap movecal</c>: the movement calibration. <c>write</c> writes the
/// calibration map and the game console script that plays it;
/// <c>compare</c> reads the game's console log from that run and compares
/// every lane with the movement simulator.
/// </summary>
/// <remarks>
/// The game run itself is outside ssmap: compile the map, put it in the
/// game's <c>maps</c> folder and the script in its <c>cfg</c> folder, start
/// the game with <c>-condebug +map ss_movecal +exec movecal</c>, and pass the
/// resulting <c>console.log</c> to <c>compare</c>.
/// <c>tools/preview-calibrate/movement_calibrate.py</c> does all of it.
/// </remarks>
public static class MoveCalCommand
{
    /// <summary>The map's name, which the script and the game's <c>+map</c> use.</summary>
    public const string MapName = "ss_movecal";

    /// <summary>The script's name, for the game's <c>+exec</c>.</summary>
    public const string ScriptName = "movecal";

    private const string Usage =
        "usage: ssmap movecal write <dir> [-class <tf2 class>]\n" +
        "       ssmap movecal compare <console.log> [-class <tf2 class>]";

    /// <summary>Runs the command.</summary>
    /// <param name="disk">Where the files are written and read.</param>
    /// <param name="args">The arguments after <c>movecal</c>.</param>
    /// <param name="output">Where the table and errors go.</param>
    /// <param name="cancellationToken">Cancels the file IO.</param>
    /// <returns>0 when written, or when every lane agrees or differs only as known; <see cref="RoomCommands.ExitFailed"/> otherwise.</returns>
    public static async Task<int> RunAsync(
        IFileSystem disk,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (!TryParse(args, out string? verb, out string? path, out string playerClass)
            || !VPath.TryCreate(Path.GetFullPath(path!), out VPath target))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        MovementProfile profile = MovementProfile.Tf2(playerClass);
        IReadOnlyList<CalibrationLane> lanes = MovementCalibration.Lanes();
        try
        {
            if (verb == "write")
            {
                await WriteAsync(disk, target.Combine(MapName + ".vmf"), MovementCalibration.WriteVmf(lanes), cancellationToken).ConfigureAwait(false);
                await WriteAsync(disk, target.Combine(ScriptName + ".cfg"), MovementCalibration.WriteConsoleScript(lanes, playerClass), cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"ssmap movecal: {lanes.Count} lanes -> {HostPaths.Display(target)}/{MapName}.vmf and {ScriptName}.cfg").ConfigureAwait(false);
                return Program.ExitSuccess;
            }

            string log;
            await using (Stream stream = await disk.OpenReadAsync(target, cancellationToken).ConfigureAwait(false))
            using (StreamReader reader = new(stream, Encoding.UTF8))
            {
                log = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<CalibrationRow> rows = MovementCalibration.Compare(lanes, MovementCalibration.ParseConsoleLog(log), profile);
            await output.WriteAsync(MovementCalibration.Format(rows)).ConfigureAwait(false);
            return rows.All(r => r.Agrees || r.Known) ? Program.ExitSuccess : RoomCommands.ExitFailed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap movecal: {HostPaths.Display(target)}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }
    }

    /// <summary>Reads the arguments: the verb, its path and an optional class (soldier by default).</summary>
    /// <param name="args">The arguments after <c>movecal</c>.</param>
    /// <param name="verb"><c>write</c> or <c>compare</c>.</param>
    /// <param name="path">The directory or log.</param>
    /// <param name="playerClass">The TF2 class.</param>
    /// <returns>False when the arguments do not make sense.</returns>
    public static bool TryParse(IReadOnlyList<string> args, out string? verb, out string? path, out string playerClass)
    {
        ArgumentNullException.ThrowIfNull(args);
        verb = null;
        path = null;
        playerClass = "soldier";
        if (args.Count < 2 || args[0] is not ("write" or "compare"))
        {
            return false;
        }

        verb = args[0];
        path = args[1];
        for (int i = 2; i < args.Count; i++)
        {
            if (args[i] == "-class" && i + 1 < args.Count)
            {
                playerClass = args[++i];
                try
                {
                    _ = MovementProfile.Tf2(playerClass);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WriteAsync(IFileSystem disk, VPath path, string text, CancellationToken cancellationToken)
    {
        await using Stream stream = await disk.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
