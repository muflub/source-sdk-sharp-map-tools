//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.AssetDb;

/// <summary>
/// What the database already holds of one asset kind, keyed by path and
/// content hash, for an import to reuse instead of decoding again.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks>
/// <para>
/// Built once before an import's parallel pass and only read during it, so
/// the workers share it without a lock.
/// </para>
/// <para>
/// It also answers which image locations are taken. A path's natural image
/// location belongs to whichever content was exported there first; different
/// bytes at the same path, from another game, get the location with a hash
/// suffix instead, so importing a second game never overwrites the first
/// game's pictures.
/// </para>
/// </remarks>
public sealed class AssetIndex<T>
    where T : class
{
    private readonly Dictionary<(string Path, string Sha256), T> _byIdentity;
    private readonly HashSet<string> _images;

    /// <summary>Indexes some records.</summary>
    /// <param name="records">The records the database holds.</param>
    /// <param name="path">A record's path.</param>
    /// <param name="sha256">A record's content hash.</param>
    /// <param name="image">
    /// A record's image location in the form <see cref="ImageLocation"/> is
    /// asked about: a path without extension, or a directory of views.
    /// </param>
    public AssetIndex(
        IEnumerable<T> records,
        Func<T, string> path,
        Func<T, string> sha256,
        Func<T, string> image)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(image);

        _byIdentity = [];
        _images = new HashSet<string>(StringComparer.Ordinal);
        foreach (T record in records)
        {
            _byIdentity[(path(record), sha256(record))] = record;
            _images.Add(image(record));
        }
    }

    /// <summary>An index of nothing, for a fresh database.</summary>
    /// <returns>The empty index.</returns>
    public static AssetIndex<T> Empty() => new([], _ => string.Empty, _ => string.Empty, _ => string.Empty);

    /// <summary>Gets how many records are indexed.</summary>
    public int Count => _byIdentity.Count;

    /// <summary>The record for exactly this content at this path, or null.</summary>
    /// <param name="path">The asset's path.</param>
    /// <param name="sha256">The hash of its bytes.</param>
    /// <returns>The record, or null.</returns>
    public T? Find(string path, string sha256) =>
        _byIdentity.GetValueOrDefault((path, sha256));

    /// <summary>
    /// Where a new asset's image goes: <paramref name="natural"/> unless an
    /// existing record holds it, in which case the location with the first
    /// eight hex digits of the hash appended (<c>name~1a2b3c4d</c>).
    /// </summary>
    /// <param name="natural">The location derived from the path alone, without extension.</param>
    /// <param name="sha256">The new content's hash.</param>
    /// <returns>The location to write, without extension.</returns>
    public string ImageLocation(string natural, string sha256)
    {
        ArgumentNullException.ThrowIfNull(natural);
        ArgumentNullException.ThrowIfNull(sha256);
        return _images.Contains(natural) ? $"{natural}~{sha256[..Math.Min(8, sha256.Length)]}" : natural;
    }
}
