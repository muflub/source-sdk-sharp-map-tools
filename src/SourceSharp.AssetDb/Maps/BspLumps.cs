//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.AssetDb.Compression;

namespace SourceSharp.AssetDb.Maps;

/// <summary>
/// Read-only access to a shipped map's lumps, decompressing the ones the
/// engine stored LZMA-compressed.
/// </summary>
/// <remarks>
/// <para>
/// Shipped maps are often compressed lump by lump (Team Fortress 2's are),
/// which the compilers never produce and their loader does not unpack. This
/// reader exists so the usage scan can read those maps without changing what
/// the compile-side loader accepts: a compressed lump carries its unpacked
/// size in the directory's fourth field, and its bytes are an
/// <see cref="ValveLzma"/> container.
/// </para>
/// <para>
/// Game lump entries are compressed individually. A compressed entry's
/// directory length is its UNPACKED size, so its packed bytes run to the
/// next entry's offset; the engine writes an empty terminating entry so the
/// last one has a next offset too.
/// </para>
/// </remarks>
public sealed class BspLumps
{
    private const int GameLumpCompressed = 1;
    private readonly byte[] _file;
    private readonly (int Offset, int Length, int Version, int Unpacked)[] _directory;

    private BspLumps(byte[] file, (int, int, int, int)[] directory)
    {
        _file = file;
        _directory = directory;
    }

    /// <summary>Reads a map's header and lump directory.</summary>
    /// <param name="file">The whole <c>.bsp</c>.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidBspException">The file is not a BSP, or its directory points outside it.</exception>
    public static BspLumps Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < BspData.HeaderSize || BinaryPrimitives.ReadInt32LittleEndian(file) != BspData.Ident)
        {
            throw new InvalidBspException("not a BSP: the VBSP header is missing");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
        if (version is < BspData.MinVersion or > BspData.MaxVersion)
        {
            throw new InvalidBspException($"BSP version {version} is outside {BspData.MinVersion}..{BspData.MaxVersion}");
        }

        (int, int, int, int)[] directory = new (int, int, int, int)[BspData.HeaderLumps];
        for (int i = 0; i < directory.Length; i++)
        {
            ReadOnlySpan<byte> entry = file.AsSpan(8 + (i * 16), 16);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(entry);
            int length = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            if (length != 0 && (offset < 0 || length < 0 || (long)offset + length > file.Length))
            {
                throw new InvalidBspException($"lump {i} spans {offset}..{(long)offset + length} of a {file.Length}-byte file");
            }

            directory[i] = (offset, length, BinaryPrimitives.ReadInt32LittleEndian(entry[8..]), BinaryPrimitives.ReadInt32LittleEndian(entry[12..]));
        }

        return new BspLumps(file, directory);
    }

    /// <summary>Gets whether any lump is stored compressed.</summary>
    public bool AnyCompressed => _directory.Any(d => d.Unpacked != 0);

    /// <summary>One lump's bytes, unpacked.</summary>
    /// <param name="lump">The lump.</param>
    /// <returns>The bytes; empty for an absent lump.</returns>
    /// <exception cref="InvalidDataException">A compressed lump does not unpack to its declared size.</exception>
    public ReadOnlyMemory<byte> Lump(BspLump lump)
    {
        (int offset, int length, _, int unpacked) = _directory[(int)lump];
        ReadOnlyMemory<byte> stored = _file.AsMemory(offset, length);
        if (unpacked == 0 || length == 0)
        {
            return stored;
        }

        byte[] bytes = ValveLzma.Decompress(stored.Span);
        if (bytes.Length != unpacked)
        {
            throw new InvalidDataException($"lump {lump} unpacked to {bytes.Length} bytes, not the {unpacked} its directory declares");
        }

        return bytes;
    }

    /// <summary>The game lump's entries, each unpacked, with the compressed flag cleared.</summary>
    /// <returns>The entries in directory order, the empty terminator left out.</returns>
    /// <exception cref="InvalidBspException">The directory points outside the file.</exception>
    public IReadOnlyList<GameLumpEntry> GameLumps()
    {
        ReadOnlySpan<byte> lump = Lump(BspLump.GameLump).Span;
        if (lump.Length < 4)
        {
            return [];
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(lump);
        if (count < 0 || 4 + ((long)count * 16) > lump.Length)
        {
            throw new InvalidBspException($"the game lump directory claims {count} entries in {lump.Length} bytes");
        }

        List<GameLumpEntry> entries = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = lump.Slice(4 + (i * 16), 16);
            int id = BinaryPrimitives.ReadInt32LittleEndian(entry);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            if (id == 0 && length == 0)
            {
                continue; // the terminator compressed maps carry
            }

            if ((flags & GameLumpCompressed) == 0)
            {
                CheckSpan(i, offset, length);
                entries.Add(new GameLumpEntry(id, flags, version, _file.AsMemory(offset, length)));
                continue;
            }

            int next = i + 1 < count ? BinaryPrimitives.ReadInt32LittleEndian(lump.Slice(4 + ((i + 1) * 16) + 8, 4)) : 0;
            int packed = next > offset ? next - offset : _file.Length - offset;
            CheckSpan(i, offset, packed);
            byte[] bytes = ValveLzma.Decompress(_file.AsSpan(offset, packed));
            entries.Add(new GameLumpEntry(id, (ushort)(flags & ~GameLumpCompressed), version, bytes));
        }

        return entries;
    }

    private void CheckSpan(int index, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > _file.Length)
        {
            throw new InvalidBspException($"game lump {index} spans {offset}..{(long)offset + length} of a {_file.Length}-byte file");
        }
    }
}
