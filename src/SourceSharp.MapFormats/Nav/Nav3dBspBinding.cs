//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// What ties a navigation built from a compiled map to that map's exact
/// bytes: the map's checksum as the engine computes it, and the SHA-256 of
/// the whole file. Stored in a <c>.nav3d</c>'s optional
/// <see cref="Nav3dFormat.BspBindingTag"/> section; compared with
/// <see cref="Nav3dReader.MatchesBsp(ReadOnlySpan{byte})"/>.
/// </summary>
/// <param name="MapChecksum">
/// The map's checksum (<see cref="BspMapChecksum"/>): the CRC-32 of every
/// lump but the entities, the number the engine already holds for the map it
/// loaded and the one a <c>.map2d</c> binds to.
/// </param>
/// <param name="Sha256">The SHA-256 of the whole <c>.bsp</c> file, 64 lowercase hex digits.</param>
/// <remarks>
/// <para>
/// <b>Why both.</b> A level linked from rooms carries its own id in its
/// worldspawn (<c>ss_level_id</c>), which the navigation repeats; a map
/// compiled by anything else (a stock map such as <c>dm_lockdown</c>) has no
/// such key, so the navigation names the map's bytes instead. The engine's
/// checksum leaves the entity lump out, which suits a sidecar about geometry
/// alone, but a navigation also reads entities: doors, ladders and props come
/// from there, so an entity-only edit changes it. The SHA-256 covers those
/// bytes too. Both must match: the checksum is what a game that has only the
/// engine's number can report, the hash is what makes the match exact.
/// </para>
/// <para>
/// Computed here, in the format library, so the game computes it from the
/// same bytes the same way (<see cref="Of"/>) and nothing about the rule
/// lives in two places.
/// </para>
/// </remarks>
public readonly record struct Nav3dBspBinding(uint MapChecksum, string Sha256)
{
    /// <summary>The bytes of a SHA-256.</summary>
    public const int Sha256Bytes = 32;

    /// <summary>The binding of a map file.</summary>
    /// <param name="bspFile">The whole <c>.bsp</c>, as it is on disk.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="InvalidBspException">The bytes are not a BSP, or a lump runs outside the file (<see cref="BspMapChecksum.Compute"/>).</exception>
    public static Nav3dBspBinding Of(ReadOnlySpan<byte> bspFile) =>
        new(BspMapChecksum.Compute(bspFile), Convert.ToHexStringLower(SHA256.HashData(bspFile)));

    /// <summary>The binding as its section stores it: the checksum, four zero bytes, the hash.</summary>
    /// <returns><see cref="Nav3dFormat.BspBindingBytes"/> bytes.</returns>
    /// <exception cref="ArgumentException"><see cref="Sha256"/> is not 64 hex digits.</exception>
    public byte[] ToBytes()
    {
        if (Sha256 is not { Length: Sha256Bytes * 2 } || !Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"a bsp binding's hash is {Sha256Bytes * 2} hex digits, not \"{Sha256}\".", nameof(Sha256));
        }

        byte[] bytes = new byte[Nav3dFormat.BspBindingBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, MapChecksum);
        Convert.FromHexString(Sha256).CopyTo(bytes, 8);
        return bytes;
    }

    /// <summary>Reads a binding from its section's bytes.</summary>
    /// <param name="section">The <see cref="Nav3dFormat.BspBindingTag"/> section.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="InvalidDataException">The section is not <see cref="Nav3dFormat.BspBindingBytes"/> bytes.</exception>
    public static Nav3dBspBinding FromBytes(ReadOnlySpan<byte> section)
    {
        if (section.Length != Nav3dFormat.BspBindingBytes)
        {
            throw new InvalidDataException(
                $"the .nav3d \"{Nav3dFormat.BspBindingTag}\" section is {section.Length} bytes; a map binding is {Nav3dFormat.BspBindingBytes}.");
        }

        return new Nav3dBspBinding(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section),
            Convert.ToHexStringLower(section.Slice(8, Sha256Bytes)));
    }

    /// <summary>The checksum as eight hex digits and the hash, for messages.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"map checksum {MapChecksum:x8}, sha256 {Sha256}");
}
