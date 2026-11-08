//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.Tests.MapFormats;

/// <summary>Lays out a BSP: header, then each lump in index order, game lump offsets absolute.</summary>
internal sealed class BspTestBuilder
{
    private readonly Dictionary<BspLump, (byte[] Data, int Unpacked)> _lumps = [];
    private readonly List<(int Id, ushort Flags, ushort Version, byte[] Data, int Declared)> _gameLumps = [];
    private bool _terminator;
    private bool _terminatorAtEnd;

    public BspTestBuilder Lump(BspLump lump, byte[] data, int unpacked = 0)
    {
        _lumps[lump] = (data, unpacked);
        return this;
    }

    public BspTestBuilder Structs<T>(BspLump lump, params T[] items)
        where T : unmanaged =>
        Lump(lump, MemoryMarshal.AsBytes(items.AsSpan()).ToArray());

    /// <summary>Adds a game lump entry; a compressed one (flag 1) declares its unpacked size as its length.</summary>
    public BspTestBuilder GameLump(int id, ushort flags, ushort version, byte[] data, int? declaredLength = null)
    {
        _gameLumps.Add((id, flags, version, data, declaredLength ?? data.Length));
        return this;
    }

    /// <summary>
    /// Adds the empty entry compressed maps end their game lump directory
    /// with: all zero as shipped maps write it, or carrying the end offset.
    /// </summary>
    public BspTestBuilder Terminator(bool atEnd = false)
    {
        _terminator = true;
        _terminatorAtEnd = atEnd;
        return this;
    }

    public byte[] Build()
    {
        List<byte> file = [.. new byte[BspData.HeaderSize]];
        BitConverter.GetBytes(BspData.Ident).CopyTo(CollectionsMarshal.AsSpan(file));
        BitConverter.GetBytes(20).CopyTo(CollectionsMarshal.AsSpan(file)[4..]);

        for (int index = 0; index < BspData.HeaderLumps; index++)
        {
            BspLump lump = (BspLump)index;
            byte[] data;
            int unpacked = 0;
            if (lump == BspLump.GameLump && _gameLumps.Count > 0)
            {
                data = GameLumpBytes(file.Count);
            }
            else if (_lumps.TryGetValue(lump, out (byte[] Data, int Unpacked) entry))
            {
                (data, unpacked) = entry;
            }
            else
            {
                continue;
            }

            Span<byte> header = CollectionsMarshal.AsSpan(file).Slice(8 + (index * 16), 16);
            BitConverter.GetBytes(file.Count).CopyTo(header);
            BitConverter.GetBytes(data.Length).CopyTo(header[4..]);
            BitConverter.GetBytes(unpacked).CopyTo(header[12..]);
            file.AddRange(data);
        }

        return [.. file];
    }

    private byte[] GameLumpBytes(int lumpOffset)
    {
        int entries = _gameLumps.Count + (_terminator ? 1 : 0);
        int payload = lumpOffset + 4 + (entries * 16);
        List<byte> bytes = [.. BitConverter.GetBytes(entries)];
        List<byte> payloads = [];
        foreach ((int id, ushort flags, ushort version, byte[] data, int declared) in _gameLumps)
        {
            bytes.AddRange(BitConverter.GetBytes(id));
            bytes.AddRange(BitConverter.GetBytes(flags));
            bytes.AddRange(BitConverter.GetBytes(version));
            bytes.AddRange(BitConverter.GetBytes(payload + payloads.Count));

            bytes.AddRange(BitConverter.GetBytes(declared));
            payloads.AddRange(data);
        }

        if (_terminator)
        {
            bytes.AddRange(new byte[8]);
            bytes.AddRange(BitConverter.GetBytes(_terminatorAtEnd ? payload + payloads.Count : 0));
            bytes.AddRange(new byte[4]);
        }

        bytes.AddRange(payloads);
        return [.. bytes];
    }
}
