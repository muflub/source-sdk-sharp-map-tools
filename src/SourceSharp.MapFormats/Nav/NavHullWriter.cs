//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// Writes a <see cref="NavHullLevel"/> as a <c>.navhull</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic.</b> Sections are written in a fixed order, strings in
/// the order the obstacles first name them, and nothing depends on a clock,
/// a hash seed or a thread, so a level writes the same bytes on every run
/// and machine.
/// </para>
/// <para>
/// <b>Checked first.</b> Every array is checked against the lengths the
/// others imply and every index against its range before a byte is
/// written, so a bug in whatever built the level surfaces here as an
/// exception naming the field, not as a file a game misreads.
/// </para>
/// </remarks>
public static class NavHullWriter
{
    /// <summary>The file's bytes, uncompressed.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The <c>.navhull</c> bytes.</returns>
    /// <exception cref="ArgumentException">The level is inconsistent; the message names the field.</exception>
    public static byte[] Write(NavHullLevel level) => Write(level, NavCompression.None);

    /// <summary>The file's bytes, with the image stored by a codec.</summary>
    /// <param name="level">The level.</param>
    /// <param name="compression">How the image is stored.</param>
    /// <returns>The <c>.navhull</c> bytes.</returns>
    /// <exception cref="ArgumentException">The level is inconsistent; the message names the field.</exception>
    public static byte[] Write(NavHullLevel level, NavCompression compression)
    {
        ArgumentNullException.ThrowIfNull(level);
        Check(level);

        Dictionary<string, uint> offsets = new(StringComparer.Ordinal) { [string.Empty] = 0 };
        List<byte> strings = [0];
        uint Offset(string? value)
        {
            if (value is null)
            {
                return NavHullFormat.NoString;
            }

            if (!offsets.TryGetValue(value, out uint offset))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(value);
                if (Array.IndexOf(utf8, (byte)0) >= 0)
                {
                    throw new ArgumentException($"the string \"{value}\" holds a NUL, which the string table cannot.", nameof(level));
                }

                offset = (uint)strings.Count;
                offsets[value] = offset;
                strings.AddRange(utf8);
                strings.Add(0);
            }

            return offset;
        }

        int w = level.Widths.Count;
        byte[] obstacles = new byte[level.Obstacles.Count * NavHullFormat.ObstacleRecordBytes];
        for (int i = 0; i < level.Obstacles.Count; i++)
        {
            Nav3dObstacle o = level.Obstacles[i];
            Span<byte> r = obstacles.AsSpan(i * NavHullFormat.ObstacleRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, Offset(o.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], Offset(o.ClassName));
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], o.Cell);
            BinaryPrimitives.WriteInt32LittleEndian(r[12..], o.HammerId);
            r[16] = (byte)o.Kind;
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], o.Mins.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], o.Mins.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[28..], o.Mins.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], o.Maxs.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], o.Maxs.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[40..], o.Maxs.Z);
        }

        byte[] widths = new byte[w * 4];
        for (int i = 0; i < w; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(widths.AsSpan(i * 4), level.Widths[i]);
        }

        byte[] cellStarts = new byte[level.CellStarts.Length * 4];
        for (int i = 0; i < level.CellStarts.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(cellStarts.AsSpan(i * 4), level.CellStarts[i]);
        }

        byte[] boxes = new byte[(long)level.Boxes.Length * NavHullFormat.BoxRecordBytes];
        for (int i = 0; i < level.Boxes.Length; i++)
        {
            NavHullBox b = level.Boxes[i];
            Span<byte> r = boxes.AsSpan(i * NavHullFormat.BoxRecordBytes);
            (r[0], r[1], r[2], r[3]) = (b.X0, b.Y0, b.X1, b.Y1);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], b.ZLo);
            BinaryPrimitives.WriteUInt16LittleEndian(r[6..], b.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], b.Key);
        }

        int keySize = NavHullFormat.KeyRecordBytes(w);
        byte[] keys = new byte[level.Keys.Count * keySize];
        for (int i = 0; i < level.Keys.Count; i++)
        {
            NavHullKey k = level.Keys[i];
            Span<byte> r = keys.AsSpan(i * keySize);
            BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)k.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], k.Cost);
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], k.FloorZ);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], k.ShutFirst);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], k.ShutCount);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], k.OverhangFirst);
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], k.OverhangCount);
            for (int c = 0; c < w; c++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(r[(24 + (c * 4))..], k.Ceilings[c]);
            }
        }

        int overhangSize = NavHullFormat.OverhangRecordBytes(w);
        byte[] overhangs = new byte[level.Overhangs.Count * overhangSize];
        for (int i = 0; i < level.Overhangs.Count; i++)
        {
            NavHullOverhang o = level.Overhangs[i];
            Span<byte> r = overhangs.AsSpan(i * overhangSize);
            for (int c = 0; c < w; c++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(r[(c * 16)..], o.Under[c]);
                BinaryPrimitives.WriteDoubleLittleEndian(r[((c * 16) + 8)..], o.Over[c]);
            }
        }

        int shutSize = NavHullFormat.ShutRecordBytes(w);
        byte[] shuts = new byte[level.Shuts.Count * shutSize];
        for (int i = 0; i < level.Shuts.Count; i++)
        {
            NavHullShut s = level.Shuts[i];
            Span<byte> r = shuts.AsSpan(i * shutSize);
            BinaryPrimitives.WriteUInt32LittleEndian(r, s.Obstacle);
            for (int c = 0; c < w; c++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(r[(4 + (c * 4))..], s.Ceilings[c]);
            }
        }

        byte[] jumps = new byte[level.Jumps.Count * NavHullFormat.JumpRecordBytes];
        for (int i = 0; i < level.Jumps.Count; i++)
        {
            NavHullJump j = level.Jumps[i];
            Span<byte> r = jumps.AsSpan(i * NavHullFormat.JumpRecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(r, j.BoxA);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], j.BoxB);
            (r[8], r[9], r[10], r[11]) = (j.AX, j.AY, j.BX, j.BY);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], j.Rise);
            r[16] = (byte)j.Direction;
            r[17] = j.Columns;
        }

        List<(string Tag, byte[] Bytes)> sections =
        [
            (NavHullFormat.StringsTag, [.. strings]),
            (NavHullFormat.WidthsTag, widths),
            (NavHullFormat.CellBoxesTag, cellStarts),
            (NavHullFormat.BoxesTag, boxes),
            (NavHullFormat.KeysTag, keys),
            (NavHullFormat.ShutsTag, shuts),
            (NavHullFormat.OverhangsTag, overhangs),
            (NavHullFormat.ObstaclesTag, obstacles),
            (NavHullFormat.JumpsTag, jumps),
        ];
        if (level.Bsp is { } binding)
        {
            sections.Add((NavHullFormat.BspBindingTag, binding.ToBytes()));
        }

        long at = NavHullFormat.HeaderBytes + ((long)sections.Count * NavHullFormat.DirectoryEntryBytes);
        long[] sectionOffsets = new long[sections.Count];
        for (int i = 0; i < sections.Count; i++)
        {
            at = (at + 3) & ~3L;
            sectionOffsets[i] = at;
            at += sections[i].Bytes.Length;
        }

        if (at > int.MaxValue)
        {
            throw new ArgumentException($"the navigation is {at} bytes, more than a file's image holds (2 GB).", nameof(level));
        }

        byte[] image = new byte[at];
        Span<byte> h = image;
        BinaryPrimitives.WriteInt32LittleEndian(h, NavHullFormat.HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], sections.Count);
        BinaryPrimitives.WriteSingleLittleEndian(h[8..], level.VoxelSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], level.CellVoxels);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], level.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], level.Rows);
        BinaryPrimitives.WriteSingleLittleEndian(h[24..], level.Origin.X);
        BinaryPrimitives.WriteSingleLittleEndian(h[28..], level.Origin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(h[32..], level.Origin.Z);
        BinaryPrimitives.WriteInt32LittleEndian(h[36..], level.ColumnVoxels);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], w);
        BinaryPrimitives.WriteSingleLittleEndian(h[44..], level.FloorNormalZ);
        BinaryPrimitives.WriteSingleLittleEndian(h[48..], level.StepHeight);
        BinaryPrimitives.WriteSingleLittleEndian(h[52..], level.JumpHeight);
        BinaryPrimitives.WriteSingleLittleEndian(h[56..], level.JumpDistance);
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], level.Boxes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[64..], level.Keys.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[68..], level.Shuts.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[72..], level.Obstacles.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[76..], level.Jumps.Count);
        for (int i = 0; i < sections.Count; i++)
        {
            Span<byte> entry = h.Slice(NavHullFormat.HeaderBytes + (i * NavHullFormat.DirectoryEntryBytes), NavHullFormat.DirectoryEntryBytes);
            Encoding.ASCII.GetBytes(sections[i].Tag, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)sectionOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)sections[i].Bytes.Length);
            sections[i].Bytes.CopyTo(image, sectionOffsets[i]);
        }

        byte[] stored = compression.Compress(image);
        byte[] file = new byte[NavHullFormat.EnvelopeBytes + stored.Length];
        Span<byte> e = file;
        NavHullFormat.Magic.CopyTo(e);
        BinaryPrimitives.WriteInt32LittleEndian(e[8..], NavHullFormat.Version);
        e[12] = (byte)compression.Codec;
        BinaryPrimitives.WriteInt32LittleEndian(e[16..], image.Length);
        BinaryPrimitives.WriteInt32LittleEndian(e[20..], stored.Length);
        stored.CopyTo(file, NavHullFormat.EnvelopeBytes);
        return file;
    }

    /// <summary>Checks a level's own consistency, naming the first field that is wrong.</summary>
    private static void Check(NavHullLevel level)
    {
        if (!(level.VoxelSize > 0) || !float.IsFinite(level.VoxelSize) || level.CellVoxels is < 1 or > NavHullFormat.MaxCellVoxels
            || level.Columns < 1 || level.Rows < 1 || (long)level.Columns * level.Rows >= int.MaxValue
            || level.ColumnVoxels is < 1 or > NavHullFormat.MaxColumnVoxels)
        {
            throw new ArgumentException("the level describes no valid grid.", nameof(level));
        }

        int w = level.Widths.Count;
        if (w is < 1 or > NavHullFormat.MaxWidths)
        {
            throw new ArgumentException($"the level is built for {w} widths; a file holds 1 to {NavHullFormat.MaxWidths}.", nameof(level));
        }

        for (int i = 0; i < w; i++)
        {
            if (!(level.Widths[i] > 0) || !float.IsFinite(level.Widths[i]) || (i > 0 && !(level.Widths[i] > level.Widths[i - 1])))
            {
                throw new ArgumentException("the widths are finite, above 0 and rising.", nameof(level));
            }
        }

        int cells = level.Columns * level.Rows;
        if (level.CellStarts.Length != cells + 1 || level.CellStarts[0] != 0 || level.CellStarts[^1] != level.Boxes.Length)
        {
            throw new ArgumentException($"the cell starts are not {cells + 1} entries from 0 to the {level.Boxes.Length} boxes.", nameof(level));
        }

        for (int c = 0; c < cells; c++)
        {
            if (level.CellStarts[c + 1] < level.CellStarts[c])
            {
                throw new ArgumentException($"cell {c}'s boxes end before they start.", nameof(level));
            }
        }

        foreach (NavHullBox b in level.Boxes)
        {
            if (b.X0 >= b.X1 || b.Y0 >= b.Y1 || b.X1 > level.CellVoxels || b.Y1 > level.CellVoxels
                || b.Height < 1 || b.ZLo + b.Height > level.ColumnVoxels || b.Key >= level.Keys.Count)
            {
                throw new ArgumentException($"a box ({b}) leaves its cell or names a key out of range.", nameof(level));
            }
        }

        foreach (NavHullKey k in level.Keys)
        {
            if (k.Ceilings.Count != w || (long)k.ShutFirst + k.ShutCount > level.Shuts.Count
                || (long)k.OverhangFirst + k.OverhangCount > level.Overhangs.Count)
            {
                throw new ArgumentException("a key has a ceiling per width and shut and overhang records in range.", nameof(level));
            }
        }

        foreach (NavHullOverhang o in level.Overhangs)
        {
            bool sound = o.Under.Count == w && o.Over.Count == w;
            for (int c = 0; sound && c < w; c++)
            {
                sound = !double.IsNaN(o.Under[c]) && !double.IsNaN(o.Over[c]);
            }

            if (!sound)
            {
                throw new ArgumentException("an overhang record has two limits per width, neither NaN.", nameof(level));
            }
        }

        foreach (NavHullShut s in level.Shuts)
        {
            if (s.Ceilings.Count != w || s.Obstacle >= level.Obstacles.Count)
            {
                throw new ArgumentException("a shut record has a ceiling per width and an obstacle in range.", nameof(level));
            }
        }

        foreach (Nav3dObstacle o in level.Obstacles)
        {
            if (o.Cell >= cells)
            {
                throw new ArgumentException($"obstacle {o.ClassName} names cell {o.Cell} of {cells}.", nameof(level));
            }
        }

        foreach (NavHullJump j in level.Jumps)
        {
            if (j.BoxA >= j.BoxB || j.BoxB >= level.Boxes.Length || (byte)j.Direction > 3 || j.Columns < 1)
            {
                throw new ArgumentException($"jump link {j} names boxes out of order or range, or no direction.", nameof(level));
            }
        }
    }
}
