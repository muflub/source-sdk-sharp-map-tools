//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Runtime.CompilerServices;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapPreview.Scene;

/// <summary>
/// Two content file systems searched in order: the front one (a map's pak)
/// shadows the back one (the game).
/// </summary>
/// <remarks>
/// This is how the engine resolves a compiled map's assets: files embedded in
/// the BSP win over the game's, which is what makes the patched cubemap
/// materials vbsp writes, and any custom content a map packs, draw. Neither
/// layer is owned; the caller disposes what it opened.
/// </remarks>
public sealed class LayeredContent : IContentFileSystem
{
    private readonly IContentFileSystem _front;
    private readonly IContentFileSystem _back;

    /// <summary>Layers two content sources.</summary>
    /// <param name="front">Searched first.</param>
    /// <param name="back">Searched when the front has no such file.</param>
    public LayeredContent(IContentFileSystem front, IContentFileSystem back)
    {
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(back);
        _front = front;
        _back = back;
    }

    /// <inheritdoc/>
    public async ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default) =>
        await _front.ResolveAsync(path, cancellationToken).ConfigureAwait(false)
        ?? await _back.ResolveAsync(path, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default) =>
        await _front.ReadAsync(path, cancellationToken).ConfigureAwait(false)
        ?? await _back.ReadAsync(path, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<FileRange?> ReadRangeAsync(VPath path, long offset, int length, CancellationToken cancellationToken = default) =>
        await _front.ReadRangeAsync(path, offset, length, cancellationToken).ConfigureAwait(false)
        ?? await _back.ReadRangeAsync(path, offset, length, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HashSet<VPath> seen = [];
        await foreach (VPath path in _front.EnumerateAsync(directory, searchPattern, cancellationToken).ConfigureAwait(false))
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        await foreach (VPath path in _back.EnumerateAsync(directory, searchPattern, cancellationToken).ConfigureAwait(false))
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }
}
