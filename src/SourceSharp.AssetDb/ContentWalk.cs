//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Security.Cryptography;

using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb;

/// <summary>
/// The content reads every exporter shares: a sorted listing and a whole-file
/// read that answers null rather than throwing for a file that is not there.
/// </summary>
public static class ContentWalk
{
    /// <summary>
    /// Every content file under <paramref name="directory"/> matching
    /// <paramref name="pattern"/>, sorted ordinally by path.
    /// </summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="directory">The content directory, e.g. <c>materials</c>.</param>
    /// <param name="pattern">The file pattern, e.g. <c>*.vtf</c>.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The paths, de-duplicated by the mount, in ordinal order.</returns>
    /// <remarks>
    /// The sort is not cosmetic: mounts enumerate in an order of their own,
    /// and the export's rows and issues must come out equal for equal inputs
    /// however the content happens to be mounted.
    /// </remarks>
    public static async Task<List<VPath>> ListAsync(
        IContentFileSystem content,
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<VPath> paths = [];
        await foreach (VPath path in content.EnumerateAsync(VPath.Create(directory), pattern, cancellationToken)
            .ConfigureAwait(false))
        {
            paths.Add(path);
        }

        paths.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return paths;
    }

    /// <summary>Reads one content file whole.</summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="path">The content path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or null when no mount has the file.</returns>
    public static async Task<byte[]?> ReadAsync(
        IContentFileSystem content,
        VPath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using IMemoryOwner<byte>? owned = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return owned?.Memory.ToArray();
    }

    /// <summary>The lower-case hex SHA-256 of some bytes: the identity of a file's content.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>Sixty-four hex digits.</returns>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
