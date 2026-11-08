//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.AssetDb.Database;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb;

/// <summary>
/// The <c>ssassets</c> entry point: builds and queries the offline texture,
/// material and model database agents use to pick content for maps.
/// </summary>
/// <remarks>
/// The verbs are thin: parsing, a mount, a call into the library, a report.
/// Everything the process owns (Ctrl-C, the console, exit codes, the home
/// directory) is handled here and passed down, never read below.
/// </remarks>
public static class Program
{
    /// <summary>The exit code for success.</summary>
    public const int ExitSuccess = 0;

    /// <summary>The exit code for a usage error or a cancelled run.</summary>
    public const int ExitUsage = 2;

    /// <summary>The exit code for a run that failed.</summary>
    public const int ExitFailure = 1;

    /// <summary>The usage text.</summary>
    public const string Usage = """
        ssassets - offline texture, material and model database for map design

        usage:
          ssassets import --game <dir> [--game <dir> ...] --out <dir> [options]
          ssassets list   --out <dir>
          ssassets remove --out <dir> --name <game>
          ssassets query  --out <dir> "<sql>"      (read-only)

        import options:
          --game <dir>         a directory holding gameinfo.txt (repeatable), e.g.
                               ".../steamapps/common/Team Fortress 2/tf"
          --name <name>        the name to store the game under (one --game only;
                               default: the directory's name, e.g. tf, hl2)
          --out <dir>          the database directory: assets.db, README.md, images
          --steam <dir>        a steamapps/common directory for |appid_N| paths
                               (repeatable; default: the usual Steam locations)
          --models <which>     props (default), all or none
          --no-textures        skip texture images (materials are still read)
          --texture-size <n>   longest texture image edge, 0 for full size (default 512)
          --view-size <n>      model view edge in pixels (default 384)
          --threads <n>        files processed at once (default: one per core)
          --force              decode and render again even when unchanged

        The README.md written into --out explains the schema, the image
        conventions and common queries.
        """;

    /// <summary>Runs the tool.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // First Ctrl-C winds down: the database transaction is never
            // started or rolls back, and the images written so far are reused
            // by the next run. A second Ctrl-C gets the default behaviour.
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return await RunAsync(args, Console.Out, Console.Error, home, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("ssassets: cancelled").ConfigureAwait(false);
            return ExitUsage;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Dispatches one command, with the console and home directory injected.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="error">Where progress and diagnostics go.</param>
    /// <param name="home">The user's home directory, for the default Steam locations.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        string home,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(home);

        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? ExitUsage : ExitSuccess;
        }

        CommandLine command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (FormatException e)
        {
            await error.WriteLineAsync($"ssassets: {e.Message}").ConfigureAwait(false);
            await error.WriteLineAsync("run 'ssassets --help' for usage").ConfigureAwait(false);
            return ExitUsage;
        }

        try
        {
            return command.Verb switch
            {
                "import" => await ImportAsync(command, output, error, home, cancellationToken).ConfigureAwait(false),
                "list" => await ListAsync(command, output, cancellationToken).ConfigureAwait(false),
                "remove" => await RemoveAsync(command, output, error, cancellationToken).ConfigureAwait(false),
                "query" => await QueryAsync(command, output, cancellationToken).ConfigureAwait(false),
                _ => ExitUsage,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await error.WriteLineAsync($"ssassets {command.Verb}: {e.Message}").ConfigureAwait(false);
            return ExitFailure;
        }
    }

    /// <summary>
    /// Where Steam keeps its games on this machine's usual layouts, as
    /// <c>steamapps/common</c> directories: the Linux client's two homes and
    /// the Debian package's.
    /// </summary>
    /// <param name="home">The user's home directory.</param>
    /// <returns>The candidates; the mount takes the first with a library list.</returns>
    public static IReadOnlyList<string> DefaultSteamRoots(string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return
        [
            Path.Combine(home, ".steam", "steam", "steamapps", "common"),
            Path.Combine(home, ".steam", "root", "steamapps", "common"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
            Path.Combine(home, ".steam", "debian-installation", "steamapps", "common"),
        ];
    }

    private static async Task<int> ImportAsync(
        CommandLine command,
        TextWriter output,
        TextWriter error,
        string home,
        CancellationToken cancellationToken)
    {
        string outDirectory = Path.GetFullPath(command.Out!);
        PhysicalFileSystem disk = PhysicalFileSystem.AtHostRoot();
        PhysicalFileSystem outputFiles = new(outDirectory);
        IReadOnlyList<string> steam = command.Steam.Count > 0 ? command.Steam : DefaultSteamRoots(home);

        // The database directory must exist before SQLite opens a file in it;
        // writing the README through the file system creates it.
        using AssetDatabase database = await OpenAsync(outputFiles, outDirectory, cancellationToken).ConfigureAwait(false);

        foreach (string game in command.Games)
        {
            string gameDirectory = Path.GetFullPath(game);
            string name = command.Name ?? GameName(gameDirectory);
            await error.WriteLineAsync($"ssassets: mounting {name} from {gameDirectory}").ConfigureAwait(false);

            await using GameMount mount = await GameMount.OpenAsync(disk, gameDirectory, steam, cancellationToken)
                .ConfigureAwait(false);
            foreach (string skipped in mount.Skipped)
            {
                await error.WriteLineAsync($"ssassets: {name}: search path not found, skipped: {skipped}")
                    .ConfigureAwait(false);
            }

            AssetExportOptions options = command.Options with { Progress = new ProgressPrinter(error, name) };
            ImportSummary summary = await AssetImporter.ImportAsync(
                database,
                outputFiles,
                name,
                mount.Info?.Game,
                gameDirectory,
                mount.Content,
                options,
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{summary.Game}: {summary.Textures} textures, {summary.Materials} materials, "
                + $"{summary.Models} models, {summary.Issues} issues, {summary.PrunedImages} stale images removed"))
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync($"database: {Path.Combine(outDirectory, AssetDatabase.FileName)}").ConfigureAwait(false);
        return ExitSuccess;
    }

    private static async Task<int> ListAsync(CommandLine command, TextWriter output, CancellationToken cancellationToken)
    {
        using AssetDatabase database = await OpenExistingAsync(command.Out!, readOnly: true, cancellationToken).ConfigureAwait(false);
        foreach (GameSummary game in database.Games())
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{game.Name}\t{game.Title}\t{game.Textures} textures\t{game.Materials} materials\t"
                + $"{game.Models} models\t{game.Issues} issues\t{game.ImportedUtc}"));
        }

        return ExitSuccess;
    }

    private static async Task<int> RemoveAsync(
        CommandLine command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string outDirectory = Path.GetFullPath(command.Out!);
        using AssetDatabase database = await OpenExistingAsync(outDirectory, readOnly: false, cancellationToken).ConfigureAwait(false);
        if (!database.RemoveGame(command.Name!))
        {
            await error.WriteLineAsync($"ssassets remove: no game named {command.Name}").ConfigureAwait(false);
            return ExitFailure;
        }

        PhysicalFileSystem files = new(outDirectory);
        int pruned = await AssetImporter.PruneAsync(database, files, cancellationToken).ConfigureAwait(false);
        await AssetImporter.WriteReadmeAsync(database, files, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"removed {command.Name}; {pruned} images deleted").ConfigureAwait(false);
        return ExitSuccess;
    }

    private static async Task<int> QueryAsync(CommandLine command, TextWriter output, CancellationToken cancellationToken)
    {
        using AssetDatabase database = await OpenExistingAsync(command.Out!, readOnly: true, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows) = database.Query(command.Sql!);
        output.WriteLine(string.Join('\t', columns));
        foreach (string?[] row in rows)
        {
            output.WriteLine(string.Join('\t', row.Select(v => v ?? "NULL")));
        }

        return ExitSuccess;
    }

    /// <summary>The default game name: the gameinfo directory's own name, lower case.</summary>
    /// <param name="gameDirectory">The directory holding gameinfo.txt.</param>
    /// <returns>The name, e.g. <c>tf</c>.</returns>
    public static string GameName(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDirectory)).ToLowerInvariant();
    }

    private static async Task<AssetDatabase> OpenAsync(
        IFileSystem outputFiles,
        string outDirectory,
        CancellationToken cancellationToken)
    {
        // Creating the README first both makes the directory and leaves a
        // marker explaining the directory if the first import is interrupted.
        if (!await outputFiles.ExistsAsync(VPath.Create(AssetImporter.ReadmeFileName), cancellationToken).ConfigureAwait(false))
        {
            byte[] text = System.Text.Encoding.UTF8.GetBytes(AgentReadme.Build([]));
            await outputFiles.ReplaceAsync(
                VPath.Create(AssetImporter.ReadmeFileName),
                async (stream, token) => await stream.WriteAsync(text, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        return AssetDatabase.Open(Path.Combine(outDirectory, AssetDatabase.FileName));
    }

    /// <summary>
    /// Opens the database a verb names, refusing to create one: SQLite would
    /// happily make an empty file for a mistyped directory.
    /// </summary>
    private static async Task<AssetDatabase> OpenExistingAsync(
        string outDirectory,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetFullPath(outDirectory), AssetDatabase.FileName);
        PhysicalFileSystem disk = PhysicalFileSystem.AtHostRoot();
        if (!await disk.ExistsAsync(VPath.Create(path), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"no asset database at {path}");
        }

        return readOnly ? AssetDatabase.OpenReadOnly(path) : AssetDatabase.Open(path);
    }

    /// <summary>Prints a pass's progress every ten percent, and once at the end.</summary>
    private sealed class ProgressPrinter(TextWriter error, string game) : IProgress<ImportProgress>
    {
        private readonly Lock _gate = new();
        private string _stage = string.Empty;
        private int _lastTenth = -1;

        public void Report(ImportProgress value)
        {
            lock (_gate)
            {
                if (value.Stage != _stage)
                {
                    _stage = value.Stage;
                    _lastTenth = -1;
                }

                int tenth = value.Total == 0 ? 10 : (int)((long)value.Done * 10 / value.Total);
                if (tenth == _lastTenth)
                {
                    return;
                }

                _lastTenth = tenth;
                error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"ssassets: {game}: {value.Stage} {value.Done}/{value.Total}"));
            }
        }
    }
}

/// <summary>A parsed <c>ssassets</c> command line.</summary>
public sealed record CommandLine
{
    /// <summary>Gets the verb: <c>import</c>, <c>list</c>, <c>remove</c> or <c>query</c>.</summary>
    public required string Verb { get; init; }

    /// <summary>Gets the database directory.</summary>
    public string? Out { get; init; }

    /// <summary>Gets the game directories to import.</summary>
    public IReadOnlyList<string> Games { get; init; } = [];

    /// <summary>Gets the name to store a single game under, or remove.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the Steam library directories given.</summary>
    public IReadOnlyList<string> Steam { get; init; } = [];

    /// <summary>Gets the SQL for <c>query</c>.</summary>
    public string? Sql { get; init; }

    /// <summary>Gets the export options the flags set.</summary>
    public AssetExportOptions Options { get; init; } = new();

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The arguments, verb first.</param>
    /// <returns>The command.</returns>
    /// <exception cref="FormatException">The arguments are not a valid command; the message says why.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            throw new FormatException("no command given");
        }

        string verb = args[0];
        if (verb is not ("import" or "list" or "remove" or "query"))
        {
            throw new FormatException($"unknown command '{verb}'");
        }

        string? outDirectory = null, name = null, sql = null;
        List<string> games = [], steam = [];
        AssetExportOptions options = new();

        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--out":
                    outDirectory = Value(args, ref i);
                    break;
                case "--game":
                    games.Add(Value(args, ref i));
                    break;
                case "--name":
                    name = Value(args, ref i);
                    break;
                case "--steam":
                    steam.Add(Value(args, ref i));
                    break;
                case "--models":
                    string models = Value(args, ref i);
                    options = options with
                    {
                        Models = models switch
                        {
                            "props" => ModelSelection.Props,
                            "all" => ModelSelection.All,
                            "none" => ModelSelection.None,
                            _ => throw new FormatException($"--models takes props, all or none, not '{models}'"),
                        },
                    };
                    break;
                case "--no-textures":
                    options = options with { Textures = false };
                    break;
                case "--texture-size":
                    options = options with { MaxTextureSize = Number(args, ref i, arg, minimum: 0) };
                    break;
                case "--view-size":
                    options = options with { ViewSize = Number(args, ref i, arg, minimum: 64) };
                    break;
                case "--threads":
                    options = options with { MaxParallelism = Number(args, ref i, arg, minimum: 1) };
                    break;
                case "--force":
                    options = options with { Force = true };
                    break;
                default:
                    if (verb == "query" && sql is null && !arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        sql = arg;
                        break;
                    }

                    throw new FormatException($"unknown option '{arg}' for {verb}");
            }
        }

        if (outDirectory is null)
        {
            throw new FormatException($"{verb} needs --out <dir>");
        }

        switch (verb)
        {
            case "import" when games.Count == 0:
                throw new FormatException("import needs at least one --game <dir>");
            case "import" when name is not null && games.Count > 1:
                throw new FormatException("--name names one game; give one --game with it, or none");
            case "remove" when name is null:
                throw new FormatException("remove needs --name <game>");
            case "query" when sql is null:
                throw new FormatException("query needs the SQL to run");
        }

        return new CommandLine
        {
            Verb = verb,
            Out = outDirectory,
            Games = games,
            Name = name,
            Steam = steam,
            Sql = sql,
            Options = options,
        };
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
        {
            throw new FormatException($"{args[i]} needs a value");
        }

        return args[++i];
    }

    private static int Number(IReadOnlyList<string> args, ref int i, string flag, int minimum)
    {
        string text = Value(args, ref i);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum)
        {
            throw new FormatException($"{flag} takes a whole number of at least {minimum}, not '{text}'");
        }

        return value;
    }
}
