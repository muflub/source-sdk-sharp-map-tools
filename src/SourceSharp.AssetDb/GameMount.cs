//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb;

/// <summary>
/// A mounted game's content, held for the lifetime of one export.
/// </summary>
/// <remarks>
/// <para>
/// The exporter reads a whole game read-only, so the mount is opened once and
/// disposed when the export ends; an export that throws must still dispose,
/// because mounts hold VPK readers and file handles that a long-lived service
/// cannot afford to leak per run.
/// </para>
/// </remarks>
public sealed record GameMount : IAsyncDisposable
{
    /// <summary>Gets the gameinfo this mount was opened from, when it parsed.</summary>
    public GameInfo? Info { get; init; }

    /// <summary>Gets the resolved, de-duplicated content view over every search path.</summary>
    public required ContentFileSystem Content { get; init; }

    /// <summary>Gets the search paths that matched nothing, for the host to report.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>Gets the directory the <c>gameinfo.txt</c> was loaded from.</summary>
    public required string GameDirectory { get; init; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Mounts the game whose directory is given, exactly as the compilers see
    /// it: the <c>gameinfo.txt</c> search paths, with <c>|appid_N|</c> paths
    /// resolved against the first Steam root that holds a library list.
    /// </summary>
    /// <param name="disk">The host disk; the compilers root theirs at <c>/</c>.</param>
    /// <param name="gameDirectory">The directory holding <c>gameinfo.txt</c>.</param>
    /// <param name="steamRoots">Where Steam keeps its games; the first root that carries <c>steamapps/libraryfolders.vcf</c> wins.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The mounted content; always dispose it.</returns>
    public static async Task<GameMount> OpenAsync(
        IFileSystem disk,
        string gameDirectory,
        IReadOnlyList<string>? steamRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        ArgumentNullException.ThrowIfNull(disk);

        ReadOnlyFileSystem readOnly = new(disk);
        VPath gameInfoPath = VPath.Create(Path.Combine(gameDirectory, "gameinfo.txt"));

        // Said up front and in the operator's terms: the commonest mistake is
        // pointing at the install (".../Team Fortress 2") instead of the game
        // directory inside it (".../Team Fortress 2/tf").
        if (!await disk.ExistsAsync(gameInfoPath, cancellationToken).ConfigureAwait(false))
        {
            throw new FileNotFoundException(
                $"no gameinfo.txt in {gameDirectory}; point --game at the directory that holds it "
                + "(e.g. \".../Team Fortress 2/tf\", not \".../Team Fortress 2\")",
                gameInfoPath.Value);
        }
        GameInfo gameInfo = await GameInfo.LoadAsync(readOnly, gameInfoPath, cancellationToken).ConfigureAwait(false);

        ISteamAppLocator? steam = null;
        if (steamRoots is { Count: > 0 })
        {
            foreach (string root in steamRoots)
            {
                // A root is a steamapps/common directory, the shape
                // Program.DefaultSteamRoots yields; the library list lives
                // beside it, under the sibling steamapps.
                VPath steamRoot = VPath.Create(root).Directory.Directory;
                VPath libraryList = VPath.Create(steamRoot.Value + "/" + SteamLibraryFolders.RelativePath);
                if (await disk.ExistsAsync(libraryList, cancellationToken).ConfigureAwait(false))
                {
                    steam = SteamLibraryFolders.Deferred(disk, steamRoot);
                    break;
                }
            }
        }

        GameContentMounter.Result mounted = await GameContentMounter.MountAsync(
            readOnly,
            gameInfo,
            new GameContentRoots(gameInfoPath.Directory, VPath.Create(Path.GetDirectoryName(gameDirectory)!))
            {
                Steam = steam,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new GameMount
        {
            Info = mounted.GameInfo,
            Content = mounted.Content,
            Skipped = mounted.Skipped,
            GameDirectory = gameDirectory,
        };
    }
}
