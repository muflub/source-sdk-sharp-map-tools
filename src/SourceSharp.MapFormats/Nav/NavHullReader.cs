//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Nav;

/// <summary>One neighbour of a box, as <see cref="NavHullReader.Neighbours"/> yields it.</summary>
/// <param name="Box">The neighbouring box.</param>
/// <param name="Direction">Which way it lies: a side, up or down.</param>
public readonly record struct NavHullNeighbour(int Box, Nav3dDirection Direction);

/// <summary>
/// Reads a <c>.navhull</c> file: validates it once, derives what the file
/// leaves out (each column's boxes, each box's neighbours, each box's jump
/// links, each obstacle's boxes), and then answers point, column and fit
/// queries without allocating.
/// </summary>
/// <remarks>
/// <para>
/// <b>Boxes and columns.</b> A box covers a rectangle of a cell's voxel
/// columns. Most questions a game asks are a column's (the boxes up it, the
/// one touching another from below, a jump from one column to another), so
/// the reader answers them per (box, column), and lists each column's boxes
/// low to high as a <c>.nav3d</c> column lists its leaves.
/// </para>
/// <para>
/// <b>Fits.</b> A key stores, per built width, the lowest solid a body of
/// that width meets above a column's centre (format section 4). A body with
/// its feet at <c>z</c> fits when <c>z + height</c> is at most that ceiling,
/// so a fit is answered for any height of a built width, and exactly as the
/// <c>.nav3d</c> reader's <see cref="Nav3dReader.FitsAtCentre"/> answers it
/// from the clearance record the ceiling came from. A width the file was not
/// built for has no answer, and asking is an error.
/// </para>
/// </remarks>
public sealed class NavHullReader
{
    private readonly ReadOnlyMemory<byte> _file;
    private readonly Sections _s;
    private readonly float[] _widths;
    private readonly int[] _boxCell;
    private readonly int[] _cellColumnBase;
    private readonly int[] _columnStart;
    private readonly int[] _columnBoxes;
    private readonly int[] _neighbourStart;
    private readonly NavHullNeighbour[] _neighbours;
    private readonly int[] _jumpStart;
    private readonly int[] _jumpIndex;
    private readonly int[] _obstacleStart;
    private readonly int[] _obstacleBoxes;

    private NavHullReader(ReadOnlyMemory<byte> file, Header header, Sections sections, float[] widths, Nav3dBspBinding? bsp)
    {
        _file = file;
        _s = sections;
        _widths = widths;
        Bsp = bsp;
        Codec = header.Codec;
        VoxelSize = header.VoxelSize;
        CellVoxels = header.CellVoxels;
        Columns = header.Columns;
        Rows = header.Rows;
        Origin = header.Origin;
        ColumnVoxels = header.ColumnVoxels;
        FloorNormalZ = header.FloorNormalZ;
        StepHeight = header.StepHeight;
        JumpHeight = header.JumpHeight;
        JumpDistance = header.JumpDistance;
        BoxCount = header.BoxCount;
        KeyCount = header.KeyCount;
        ObstacleCount = header.ObstacleCount;
        JumpCount = header.JumpCount;

        _boxCell = new int[BoxCount];
        for (int c = 0; c < CellCount; c++)
        {
            (int first, int end) = CellBoxes(c);
            for (int b = first; b < end; b++)
            {
                _boxCell[b] = c;
            }
        }

        (_cellColumnBase, _columnStart, _columnBoxes) = DeriveColumns();
        (_neighbourStart, _neighbours) = DeriveNeighbours();
        (_jumpStart, _jumpIndex) = DeriveJumps();
        (_obstacleStart, _obstacleBoxes) = DeriveObstacleBoxes();
    }

    /// <summary>How the file's image was stored.</summary>
    public NavCodec Codec { get; }

    /// <summary>A voxel's edge, in units.</summary>
    public float VoxelSize { get; }

    /// <summary>Voxels along a cell's edge.</summary>
    public int CellVoxels { get; }

    /// <summary>The grid's cells west to east.</summary>
    public int Columns { get; }

    /// <summary>The grid's cells south to north.</summary>
    public int Rows { get; }

    /// <summary>How many cells the grid has.</summary>
    public int CellCount => Columns * Rows;

    /// <summary>The grid's low corner, level coordinates.</summary>
    public Vec3 Origin { get; }

    /// <summary>Voxels up every column.</summary>
    public int ColumnVoxels { get; }

    /// <summary>The least normal z of a walkable floor.</summary>
    public float FloorNormalZ { get; }

    /// <summary>The highest step a player walks up without jumping.</summary>
    public float StepHeight { get; }

    /// <summary>The highest ledge a jump link climbs, and the deepest it drops.</summary>
    public float JumpHeight { get; }

    /// <summary>The farthest a jump link reaches, between column centres.</summary>
    public float JumpDistance { get; }

    /// <summary>How many boxes the file holds.</summary>
    public int BoxCount { get; }

    /// <summary>How many distinct keys the boxes name.</summary>
    public int KeyCount { get; }

    /// <summary>How many dynamic obstacles the file names.</summary>
    public int ObstacleCount { get; }

    /// <summary>How many jump links the file holds.</summary>
    public int JumpCount { get; }

    /// <summary>The widths the file holds ceilings for, rising.</summary>
    public IReadOnlyList<float> Widths => _widths;

    /// <summary>The compiled map the navigation was built from, or null when the file names none.</summary>
    public Nav3dBspBinding? Bsp { get; }

    private ReadOnlySpan<byte> Bytes => _file.Span;

    /// <summary>Whether this navigation was built from exactly this map file (<see cref="Nav3dBspBinding.Of"/>).</summary>
    /// <param name="bspFile">The whole <c>.bsp</c>, as it is on disk.</param>
    /// <returns>True when the file records a binding and it is this map's.</returns>
    public bool MatchesBsp(ReadOnlySpan<byte> bspFile)
    {
        if (Bsp is null)
        {
            return false;
        }

        try
        {
            return Bsp == Nav3dBspBinding.Of(bspFile);
        }
        catch (InvalidBspException)
        {
            return false;
        }
    }

    /// <summary>Whether this navigation was built from the map a binding describes.</summary>
    /// <param name="binding">The loaded map's binding.</param>
    /// <returns>True when the file records a binding equal to it.</returns>
    public bool MatchesBsp(Nav3dBspBinding binding) => Bsp is { } own && own == binding;

    /// <summary>Opens a file held in memory, validating all of it and deriving its indexes.</summary>
    /// <param name="file">The whole file; a raw one is read in place, a compressed one decoded once.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a valid <c>.navhull</c> file; the message says what is wrong.</exception>
    public static NavHullReader Open(ReadOnlyMemory<byte> file)
    {
        ReadOnlySpan<byte> envelope = file.Span;
        if (envelope.Length < NavHullFormat.EnvelopeBytes || !envelope[..8].SequenceEqual(NavHullFormat.Magic))
        {
            throw new InvalidDataException("not a .navhull file: the magic is missing.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(envelope[8..]);
        if (version != NavHullFormat.Version)
        {
            throw new InvalidDataException($".navhull version {version}; this build reads version {NavHullFormat.Version}.");
        }

        NavCodec codec = (NavCodec)envelope[12];
        int imageLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[16..]);
        int storedLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[20..]);
        if (imageLength < NavHullFormat.HeaderBytes || storedLength < 0 || (long)NavHullFormat.EnvelopeBytes + storedLength != envelope.Length)
        {
            throw new InvalidDataException(
                $"the .navhull envelope says {storedLength} stored bytes for a {imageLength}-byte image, but {envelope.Length - NavHullFormat.EnvelopeBytes} follow it.");
        }

        ReadOnlyMemory<byte> stored = file[NavHullFormat.EnvelopeBytes..];
        file = codec == NavCodec.None
            ? (storedLength == imageLength ? stored : throw new InvalidDataException("a raw .navhull image whose stored and image lengths differ."))
            : NavCompression.Decompress(codec, stored.Span, imageLength);
        ReadOnlySpan<byte> b = file.Span;
        Header header = new()
        {
            Codec = codec,
            HeaderBytes = BinaryPrimitives.ReadInt32LittleEndian(b),
            SectionCount = BinaryPrimitives.ReadInt32LittleEndian(b[4..]),
            VoxelSize = BinaryPrimitives.ReadSingleLittleEndian(b[8..]),
            CellVoxels = BinaryPrimitives.ReadInt32LittleEndian(b[12..]),
            Columns = BinaryPrimitives.ReadInt32LittleEndian(b[16..]),
            Rows = BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
            Origin = new Vec3(
                BinaryPrimitives.ReadSingleLittleEndian(b[24..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[28..]),
                BinaryPrimitives.ReadSingleLittleEndian(b[32..])),
            ColumnVoxels = BinaryPrimitives.ReadInt32LittleEndian(b[36..]),
            WidthCount = BinaryPrimitives.ReadInt32LittleEndian(b[40..]),
            FloorNormalZ = BinaryPrimitives.ReadSingleLittleEndian(b[44..]),
            StepHeight = BinaryPrimitives.ReadSingleLittleEndian(b[48..]),
            JumpHeight = BinaryPrimitives.ReadSingleLittleEndian(b[52..]),
            JumpDistance = BinaryPrimitives.ReadSingleLittleEndian(b[56..]),
            BoxCount = BinaryPrimitives.ReadInt32LittleEndian(b[60..]),
            KeyCount = BinaryPrimitives.ReadInt32LittleEndian(b[64..]),
            ShutCount = BinaryPrimitives.ReadInt32LittleEndian(b[68..]),
            ObstacleCount = BinaryPrimitives.ReadInt32LittleEndian(b[72..]),
            JumpCount = BinaryPrimitives.ReadInt32LittleEndian(b[76..]),
        };

        if (header.HeaderBytes < NavHullFormat.HeaderBytes || header.SectionCount < 0 || header.WidthCount is < 1 or > NavHullFormat.MaxWidths
            || header.BoxCount < 0 || header.KeyCount < 0 || header.ShutCount < 0 || header.ObstacleCount < 0 || header.JumpCount < 0)
        {
            throw new InvalidDataException("the .navhull header's counts are out of range.");
        }

        if (!(header.VoxelSize > 0) || !float.IsFinite(header.VoxelSize) || header.CellVoxels is < 1 or > NavHullFormat.MaxCellVoxels
            || header.Columns < 1 || header.Rows < 1 || (long)header.Columns * header.Rows >= int.MaxValue
            || header.ColumnVoxels is < 1 or > NavHullFormat.MaxColumnVoxels)
        {
            throw new InvalidDataException("the .navhull header describes no valid grid.");
        }

        long directoryEnd = header.HeaderBytes + ((long)header.SectionCount * NavHullFormat.DirectoryEntryBytes);
        if (directoryEnd > b.Length)
        {
            throw new InvalidDataException("the .navhull section directory runs past the end of the file.");
        }

        Dictionary<string, Section> sections = new(StringComparer.Ordinal);
        for (int i = 0; i < header.SectionCount; i++)
        {
            ReadOnlySpan<byte> e = b.Slice(header.HeaderBytes + (i * NavHullFormat.DirectoryEntryBytes), NavHullFormat.DirectoryEntryBytes);
            string tag = Encoding.ASCII.GetString(e[..4]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (offset < directoryEnd || (offset & 3) != 0 || (long)offset + length > b.Length)
            {
                throw new InvalidDataException($"the .navhull section \"{tag}\" lies outside the file or off a four-byte boundary.");
            }

            if (!sections.TryAdd(tag, new Section((int)offset, (int)length)))
            {
                throw new InvalidDataException($"the .navhull file has two \"{tag}\" sections.");
            }
        }

        int cells = header.Columns * header.Rows;
        int w = header.WidthCount;
        Sections s = new()
        {
            Strings = Require(sections, NavHullFormat.StringsTag, 1, -1),
            Widths = Require(sections, NavHullFormat.WidthsTag, 4, w),
            CellStarts = Require(sections, NavHullFormat.CellBoxesTag, 4, cells + 1),
            Boxes = Require(sections, NavHullFormat.BoxesTag, NavHullFormat.BoxRecordBytes, header.BoxCount),
            Keys = Require(sections, NavHullFormat.KeysTag, NavHullFormat.KeyRecordBytes(w), header.KeyCount),
            Shuts = Require(sections, NavHullFormat.ShutsTag, NavHullFormat.ShutRecordBytes(w), header.ShutCount),
            Obstacles = Require(sections, NavHullFormat.ObstaclesTag, NavHullFormat.ObstacleRecordBytes, header.ObstacleCount),
            Jumps = Require(sections, NavHullFormat.JumpsTag, NavHullFormat.JumpRecordBytes, header.JumpCount),
            KeyBytes = NavHullFormat.KeyRecordBytes(w),
            ShutBytes = NavHullFormat.ShutRecordBytes(w),
        };

        if (b[s.Strings.Offset] != 0 || b[s.Strings.Offset + s.Strings.Length - 1] != 0)
        {
            throw new InvalidDataException("the .navhull string table must start with the empty string and end with a NUL.");
        }

        float[] widths = new float[w];
        for (int i = 0; i < w; i++)
        {
            widths[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(s.Widths.Offset + (i * 4))..]);
            if (!(widths[i] > 0) || !float.IsFinite(widths[i]) || (i > 0 && !(widths[i] > widths[i - 1])))
            {
                throw new InvalidDataException("the .navhull widths are not finite, above 0 and rising.");
            }
        }

        Validate(b, s, header, cells);
        Nav3dBspBinding? bsp = sections.TryGetValue(NavHullFormat.BspBindingTag, out Section binding)
            ? Nav3dBspBinding.FromBytes(b.Slice(binding.Offset, binding.Length))
            : null;
        return new NavHullReader(file, header, s, widths, bsp);
    }

    /// <summary>A width's index among the file's widths, or -1 when it was not built for it.</summary>
    /// <param name="width">The body's width, compared exactly.</param>
    /// <returns>The index.</returns>
    public int WidthIndex(float width) => Array.IndexOf(_widths, width);

    /// <summary>One box.</summary>
    /// <param name="box">The box.</param>
    /// <returns>Its record.</returns>
    public NavHullBox Box(int box)
    {
        ReadOnlySpan<byte> r = Record(_s.Boxes, box, NavHullFormat.BoxRecordBytes);
        return new NavHullBox(r[0], r[1], r[2], r[3], BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[8..]));
    }

    /// <summary>The cell a box is in.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The cell, <c>row × columns + column</c>.</returns>
    public int BoxCell(int box) => _boxCell[box];

    /// <summary>A box's bounds in level coordinates.</summary>
    /// <param name="box">The box.</param>
    /// <returns>Its corners.</returns>
    public (Vec3 Mins, Vec3 Maxs) BoxBounds(int box)
    {
        NavHullBox b = Box(box);
        int cell = _boxCell[box];
        double s = VoxelSize;
        double x0 = Origin.X + ((((cell % Columns) * CellVoxels) + b.X0) * s);
        double y0 = Origin.Y + ((((cell / Columns) * CellVoxels) + b.Y0) * s);
        return (new Vec3((float)x0, (float)y0, (float)(Origin.Z + (b.ZLo * s))),
            new Vec3((float)(x0 + ((b.X1 - b.X0) * s)), (float)(y0 + ((b.Y1 - b.Y0) * s)), (float)(Origin.Z + ((b.ZLo + b.Height) * s))));
    }

    /// <summary>A box's key, ceilings and all; allocates (for tools, not per-frame use).</summary>
    /// <param name="key">The key's index (<see cref="NavHullBox.Key"/>).</param>
    /// <returns>The key.</returns>
    public NavHullKey Key(int key)
    {
        ReadOnlySpan<byte> r = Record(_s.Keys, key, _s.KeyBytes);
        float[] ceilings = new float[_widths.Length];
        for (int c = 0; c < ceilings.Length; c++)
        {
            ceilings[c] = BinaryPrimitives.ReadSingleLittleEndian(r[(16 + (c * 4))..]);
        }

        return new NavHullKey((NavHullFlags)BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[4..]), ceilings, BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[12..]));
    }

    /// <summary>A box's flags.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The flags.</returns>
    public NavHullFlags Flags(int box) => (NavHullFlags)BinaryPrimitives.ReadUInt16LittleEndian(KeyRecord(box));

    /// <summary>A box's floor under its bottom voxel, meaningful when <see cref="NavHullFlags.Grounded"/>.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The floor's altitude.</returns>
    public float FloorZ(int box) => BinaryPrimitives.ReadSingleLittleEndian(KeyRecord(box)[4..]);

    /// <summary>A box's cost multiplier.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The multiplier: 1 is plain ground.</returns>
    public float CostMultiplier(int box) => BinaryPrimitives.ReadUInt16LittleEndian(KeyRecord(box)[2..]) / 256f;

    /// <summary>The ceiling a body of a built width meets above a column's centre in a box, every obstacle open.</summary>
    /// <param name="box">The box.</param>
    /// <param name="widthIndex">The width's index (<see cref="WidthIndex"/>).</param>
    /// <returns>The altitude; negative infinity when the width fits at no height.</returns>
    public float Ceiling(int box, int widthIndex)
    {
        if ((uint)widthIndex >= (uint)_widths.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(widthIndex), widthIndex, "no such width.");
        }

        return BinaryPrimitives.ReadSingleLittleEndian(KeyRecord(box)[(16 + (widthIndex * 4))..]);
    }

    /// <summary>
    /// Whether a body fits at a box's column centre with its feet at a height:
    /// its top is at most the ceiling for its width. The answer
    /// <see cref="Nav3dReader.FitsAtCentre"/> gives from the clearance record
    /// the ceiling was taken from, for any height.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <param name="originZ">The feet's altitude: in the box, or on the floor under it.</param>
    /// <param name="width">The body's width: one the file was built for.</param>
    /// <param name="height">The body's height.</param>
    /// <returns>True when it fits.</returns>
    /// <exception cref="ArgumentException">The file holds no ceilings for <paramref name="width"/>.</exception>
    public bool FitsAt(int box, double originZ, float width, float height) =>
        originZ + height <= Ceiling(box, Width(width)) + NavBrush.Epsilon;

    /// <summary>Whether a body stands on a box's floor at a column's centre: the floor is walkable and the body fits from it up.</summary>
    /// <param name="box">The box.</param>
    /// <param name="width">The body's width: one the file was built for.</param>
    /// <param name="height">The body's height.</param>
    /// <returns>True when it stands there.</returns>
    public bool Stands(int box, float width, float height) =>
        (Flags(box) & NavHullFlags.Walkable) != 0 && FitsAt(box, FloorZ(box), width, height);

    /// <summary>Whether a box is a place to climb: a ladder box the body fits in at the centre of its bottom voxel.</summary>
    /// <param name="box">The box.</param>
    /// <param name="width">The body's width: one the file was built for.</param>
    /// <param name="height">The body's height.</param>
    /// <returns>True when it climbs there.</returns>
    public bool Climbs(int box, float width, float height) =>
        (Flags(box) & NavHullFlags.Ladder) != 0 && FitsAt(box, Origin.Z + (Box(box).ZLo * (double)VoxelSize), width, height);

    /// <summary>
    /// <see cref="Stands"/> with one obstacle shut (a door closed, a prop at
    /// its place) and every other open: a box that stands open but not shut
    /// is one the obstacle occupies.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <param name="obstacle">The obstacle.</param>
    /// <param name="width">The body's width: one the file was built for.</param>
    /// <param name="height">The body's height.</param>
    /// <returns>True when it stands there with the obstacle shut.</returns>
    public bool StandsWithShut(int box, int obstacle, float width, float height)
    {
        if ((uint)obstacle >= (uint)ObstacleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(obstacle), obstacle, "no such obstacle.");
        }

        int index = Width(width);
        ReadOnlySpan<byte> key = KeyRecord(box);
        if (((NavHullFlags)BinaryPrimitives.ReadUInt16LittleEndian(key) & NavHullFlags.Walkable) == 0)
        {
            return false;
        }

        double top = BinaryPrimitives.ReadSingleLittleEndian(key[4..]) + (double)height;
        float ceiling = BinaryPrimitives.ReadSingleLittleEndian(key[(16 + (index * 4))..]);
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(key[12..]);
        for (uint i = first; i < first + count; i++)
        {
            ReadOnlySpan<byte> shut = Record(_s.Shuts, (int)i, _s.ShutBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(shut) == (uint)obstacle)
            {
                ceiling = BinaryPrimitives.ReadSingleLittleEndian(shut[(4 + (index * 4))..]);
                break;
            }
        }

        return top <= ceiling + NavBrush.Epsilon;
    }

    /// <summary>A cell's boxes: indices <c>[First, End)</c>, empty for a cell with none.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The range.</returns>
    public (int First, int End) CellBoxes(int cell)
    {
        if ((uint)cell >= (uint)CellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), cell, "no such cell.");
        }

        return ((int)ReadU32(_s.CellStarts, cell), (int)ReadU32(_s.CellStarts, cell + 1));
    }

    /// <summary>The boxes of one voxel column, low to high; empty for a column with none or outside the grid.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="x">The column within the cell, along x.</param>
    /// <param name="y">Along y.</param>
    /// <returns>The boxes; allocation-free.</returns>
    public ReadOnlySpan<int> ColumnBoxes(int cell, int x, int y)
    {
        if ((uint)cell >= (uint)CellCount || (uint)x >= (uint)CellVoxels || (uint)y >= (uint)CellVoxels || _cellColumnBase[cell] < 0)
        {
            return [];
        }

        int column = _cellColumnBase[cell] + (y * CellVoxels) + x;
        return _columnBoxes.AsSpan(_columnStart[column], _columnStart[column + 1] - _columnStart[column]);
    }

    /// <summary>The box holding a point, or -1 in solid, in an empty cell or outside the grid. Voxels are half-open, low side in.</summary>
    /// <param name="point">The point, level coordinates.</param>
    /// <returns>The box.</returns>
    public int FindBox(Vec3 point)
    {
        double lx = point.X - Origin.X;
        double ly = point.Y - Origin.Y;
        double lz = point.Z - Origin.Z;
        if (!(lx >= 0 && ly >= 0 && lz >= 0))
        {
            return -1;
        }

        long gx = (long)Math.Floor(lx / VoxelSize);
        long gy = (long)Math.Floor(ly / VoxelSize);
        long gz = (long)Math.Floor(lz / VoxelSize);
        if (gx >= (long)Columns * CellVoxels || gy >= (long)Rows * CellVoxels || gz >= ColumnVoxels)
        {
            return -1;
        }

        int n = CellVoxels;
        return FindBox((int)((gy / n * Columns) + (gx / n)), (int)(gx % n), (int)(gy % n), (int)gz);
    }

    /// <summary>The box holding a voxel of a cell, or -1.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="x">The voxel within the cell along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="z">Along z, from the grid's floor.</param>
    /// <returns>The box.</returns>
    public int FindBox(int cell, int x, int y, int z)
    {
        foreach (int box in ColumnBoxes(cell, x, y))
        {
            NavHullBox b = Box(box);
            if (z < b.ZLo)
            {
                return -1;
            }

            if (z <= b.ZHi)
            {
                return box;
            }
        }

        return -1;
    }

    /// <summary>The box touching a box from below in one of its columns, or -1.</summary>
    /// <param name="box">The box.</param>
    /// <param name="x">A column of its footprint, within its cell, along x.</param>
    /// <param name="y">Along y.</param>
    /// <returns>The box below.</returns>
    public int Below(int box, int x, int y) => Touching(box, x, y, -1);

    /// <summary>The box touching a box from above in one of its columns, or -1.</summary>
    /// <param name="box">The box.</param>
    /// <param name="x">A column of its footprint, within its cell, along x.</param>
    /// <param name="y">Along y.</param>
    /// <returns>The box above.</returns>
    public int Above(int box, int x, int y) => Touching(box, x, y, 1);

    /// <summary>
    /// A box's neighbours: the boxes overlapping it across each side face
    /// (east, north, west, south, into the next cell too), then those touching
    /// it from above and from below, each group ascending; derived at load.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <returns>The neighbours; allocation-free.</returns>
    public ReadOnlySpan<NavHullNeighbour> Neighbours(int box) =>
        _neighbours.AsSpan(_neighbourStart[box], _neighbourStart[box + 1] - _neighbourStart[box]);

    /// <summary>One jump link.</summary>
    /// <param name="jump">The link.</param>
    /// <returns>Its record.</returns>
    public NavHullJump Jump(int jump)
    {
        ReadOnlySpan<byte> r = Record(_s.Jumps, jump, NavHullFormat.JumpRecordBytes);
        return new NavHullJump(BinaryPrimitives.ReadUInt32LittleEndian(r), BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
            r[8], r[9], r[10], r[11], BinaryPrimitives.ReadSingleLittleEndian(r[12..]), (Nav3dDirection)r[16], r[17]);
    }

    /// <summary>The jump links a box takes part in, at either end, ascending.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The links' indexes; allocation-free.</returns>
    public ReadOnlySpan<int> Jumps(int box) => _jumpIndex.AsSpan(_jumpStart[box], _jumpStart[box + 1] - _jumpStart[box]);

    /// <summary>One dynamic obstacle.</summary>
    /// <param name="obstacle">The obstacle.</param>
    /// <returns>Its record.</returns>
    public Nav3dObstacle Obstacle(int obstacle)
    {
        ReadOnlySpan<byte> r = Record(_s.Obstacles, obstacle, NavHullFormat.ObstacleRecordBytes);
        return new Nav3dObstacle(
            String(BinaryPrimitives.ReadUInt32LittleEndian(r)),
            String(BinaryPrimitives.ReadUInt32LittleEndian(r[4..])) ?? string.Empty,
            BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(r[12..]),
            (Nav3dObstacleKind)r[16],
            new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r[20..]), BinaryPrimitives.ReadSingleLittleEndian(r[24..]), BinaryPrimitives.ReadSingleLittleEndian(r[28..])),
            new Vec3(BinaryPrimitives.ReadSingleLittleEndian(r[32..]), BinaryPrimitives.ReadSingleLittleEndian(r[36..]), BinaryPrimitives.ReadSingleLittleEndian(r[40..])));
    }

    /// <summary>The boxes whose ceilings an obstacle lowers when shut, ascending.</summary>
    /// <param name="obstacle">The obstacle.</param>
    /// <returns>The boxes; allocation-free.</returns>
    public ReadOnlySpan<int> ObstacleBoxes(int obstacle) =>
        _obstacleBoxes.AsSpan(_obstacleStart[obstacle], _obstacleStart[obstacle + 1] - _obstacleStart[obstacle]);

    /// <summary>Reads the whole file into a <see cref="NavHullLevel"/>: for tools and round-trip checks.</summary>
    /// <returns>The level.</returns>
    public NavHullLevel ToLevel()
    {
        int w = _widths.Length;
        return new NavHullLevel
        {
            VoxelSize = VoxelSize,
            CellVoxels = CellVoxels,
            Columns = Columns,
            Rows = Rows,
            Origin = Origin,
            ColumnVoxels = ColumnVoxels,
            FloorNormalZ = FloorNormalZ,
            StepHeight = StepHeight,
            JumpHeight = JumpHeight,
            JumpDistance = JumpDistance,
            Widths = [.. _widths],
            CellStarts = [.. Enumerable.Range(0, CellCount + 1).Select(c => ReadU32(_s.CellStarts, c))],
            Boxes = [.. Enumerable.Range(0, BoxCount).Select(Box)],
            Keys = [.. Enumerable.Range(0, KeyCount).Select(Key)],
            Shuts = [.. Enumerable.Range(0, _s.Shuts.Length / _s.ShutBytes).Select(i =>
            {
                ReadOnlySpan<byte> r = Record(_s.Shuts, i, _s.ShutBytes);
                float[] ceilings = new float[w];
                for (int c = 0; c < w; c++)
                {
                    ceilings[c] = BinaryPrimitives.ReadSingleLittleEndian(r[(4 + (c * 4))..]);
                }

                return new NavHullShut(BinaryPrimitives.ReadUInt32LittleEndian(r), ceilings);
            })],
            Obstacles = [.. Enumerable.Range(0, ObstacleCount).Select(Obstacle)],
            Jumps = [.. Enumerable.Range(0, JumpCount).Select(Jump)],
            Bsp = Bsp,
        };
    }

    private int Width(float width)
    {
        int index = WidthIndex(width);
        return index >= 0
            ? index
            : throw new ArgumentException($"the navigation holds no ceilings for a body {width} wide; it was built for {string.Join(", ", _widths)}.", nameof(width));
    }

    private ReadOnlySpan<byte> KeyRecord(int box) => Record(_s.Keys, (int)BinaryPrimitives.ReadUInt32LittleEndian(Record(_s.Boxes, box, NavHullFormat.BoxRecordBytes)[8..]), _s.KeyBytes);

    private int Touching(int box, int x, int y, int step)
    {
        NavHullBox b = Box(box);
        if (!b.Holds(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"column ({x}, {y}) is not in box {box}'s footprint.");
        }

        ReadOnlySpan<int> column = ColumnBoxes(_boxCell[box], x, y);
        int at = column.IndexOf(box) + step;
        if (at < 0 || at >= column.Length)
        {
            return -1;
        }

        NavHullBox other = Box(column[at]);
        return (step < 0 ? other.ZHi + 1 == b.ZLo : b.ZHi + 1 == other.ZLo) ? column[at] : -1;
    }

    private string? String(uint offset)
    {
        if (offset == NavHullFormat.NoString)
        {
            return null;
        }

        ReadOnlySpan<byte> table = Bytes.Slice(_s.Strings.Offset, _s.Strings.Length);
        ReadOnlySpan<byte> rest = table[(int)offset..];
        return Encoding.UTF8.GetString(rest[..rest.IndexOf((byte)0)]);
    }

    private static Section Require(Dictionary<string, Section> sections, string tag, int record, int count)
    {
        if (!sections.TryGetValue(tag, out Section section))
        {
            throw new InvalidDataException($"the .navhull file has no \"{tag}\" section.");
        }

        if (section.Length % record != 0 || (count >= 0 && section.Length != (long)count * record) || (count < 0 && section.Length == 0))
        {
            throw new InvalidDataException($"the .navhull \"{tag}\" section is {section.Length} bytes, which its counts do not allow.");
        }

        return section;
    }

    /// <summary>Checks every index the file holds, so a query on an opened file never reads outside a section.</summary>
    private static void Validate(ReadOnlySpan<byte> b, Sections s, Header header, int cells)
    {
        uint previous = 0;
        for (int c = 0; c <= cells; c++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(b[(s.CellStarts.Offset + (c * 4))..]);
            if (start < previous || (c == 0 && start != 0) || (c == cells && start != header.BoxCount))
            {
                throw new InvalidDataException($"the cell starts are not a partition of the {header.BoxCount} boxes.");
            }

            previous = start;
        }

        for (int i = 0; i < header.BoxCount; i++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Boxes.Offset + (i * NavHullFormat.BoxRecordBytes), NavHullFormat.BoxRecordBytes);
            int zLo = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(r[6..]);
            if (r[0] >= r[2] || r[1] >= r[3] || r[2] > header.CellVoxels || r[3] > header.CellVoxels || height < 1
                || zLo + height > header.ColumnVoxels || BinaryPrimitives.ReadUInt32LittleEndian(r[8..]) >= header.KeyCount)
            {
                throw new InvalidDataException($"box {i} leaves its cell or names a key out of range.");
            }
        }

        for (int i = 0; i < header.KeyCount; i++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Keys.Offset + (i * s.KeyBytes), s.KeyBytes);
            if ((long)BinaryPrimitives.ReadUInt32LittleEndian(r[8..]) + BinaryPrimitives.ReadUInt32LittleEndian(r[12..]) > header.ShutCount)
            {
                throw new InvalidDataException($"key {i} names shut records out of range.");
            }
        }

        for (int i = 0; i < header.ShutCount; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(b[(s.Shuts.Offset + (i * s.ShutBytes))..]) >= header.ObstacleCount)
            {
                throw new InvalidDataException($"shut record {i} names an obstacle out of range.");
            }
        }

        for (int i = 0; i < header.ObstacleCount; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(b[(s.Obstacles.Offset + (i * NavHullFormat.ObstacleRecordBytes) + 8)..]) >= cells)
            {
                throw new InvalidDataException($"obstacle {i} names a cell out of range.");
            }
        }

        for (int j = 0; j < header.JumpCount; j++)
        {
            ReadOnlySpan<byte> r = b.Slice(s.Jumps.Offset + (j * NavHullFormat.JumpRecordBytes), NavHullFormat.JumpRecordBytes);
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(r);
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            if (a >= c || c >= header.BoxCount || r[16] > 3 || r[17] < 1
                || !InBox(b.Slice(s.Boxes.Offset + ((int)a * NavHullFormat.BoxRecordBytes), NavHullFormat.BoxRecordBytes), r[8], r[9])
                || !InBox(b.Slice(s.Boxes.Offset + ((int)c * NavHullFormat.BoxRecordBytes), NavHullFormat.BoxRecordBytes), r[10], r[11]))
            {
                throw new InvalidDataException($"jump link {j} names boxes {a} and {c} of {header.BoxCount} out of order, or a column outside them.");
            }
        }
    }

    /// <summary>Whether a column is in a box record's footprint.</summary>
    private static bool InBox(ReadOnlySpan<byte> box, byte x, byte y) => x >= box[0] && x < box[2] && y >= box[1] && y < box[3];

    /// <summary>Each cell's columns' boxes, low to high, checking that boxes in one column do not overlap.</summary>
    private (int[] CellBase, int[] Start, int[] Boxes) DeriveColumns()
    {
        int n = CellVoxels;
        int block = n * n;
        int[] cellBase = new int[CellCount];
        int placed = 0;
        for (int c = 0; c < CellCount; c++)
        {
            (int first, int end) = CellBoxes(c);
            cellBase[c] = end > first ? placed++ * block : -1;
        }

        int[] count = new int[(placed * block) + 1];
        for (int box = 0; box < BoxCount; box++)
        {
            NavHullBox b = Box(box);
            int baseColumn = cellBase[_boxCell[box]];
            for (int y = b.Y0; y < b.Y1; y++)
            {
                for (int x = b.X0; x < b.X1; x++)
                {
                    count[baseColumn + (y * n) + x + 1]++;
                }
            }
        }

        for (int j = 0; j < placed * block; j++)
        {
            count[j + 1] += count[j];
        }

        int[] boxes = new int[count[^1]];
        int[] fill = new int[placed * block];
        for (int box = 0; box < BoxCount; box++)
        {
            NavHullBox b = Box(box);
            int baseColumn = cellBase[_boxCell[box]];
            for (int y = b.Y0; y < b.Y1; y++)
            {
                for (int x = b.X0; x < b.X1; x++)
                {
                    int column = baseColumn + (y * n) + x;
                    boxes[count[column] + fill[column]++] = box;
                }
            }
        }

        for (int j = 0; j < placed * block; j++)
        {
            Span<int> column = boxes.AsSpan(count[j], count[j + 1] - count[j]);
            column.Sort((p, q) => Box(p).ZLo.CompareTo(Box(q).ZLo));
            for (int k = 1; k < column.Length; k++)
            {
                if (Box(column[k]).ZLo <= Box(column[k - 1]).ZHi)
                {
                    throw new InvalidDataException($"boxes {column[k - 1]} and {column[k]} overlap in a column.");
                }
            }
        }

        return (cellBase, count, boxes);
    }

    /// <summary>Every box's neighbours, in box order: each side's overlapping boxes, then up, then down, each group ascending.</summary>
    private (int[] Start, NavHullNeighbour[] Entries) DeriveNeighbours()
    {
        int n = CellVoxels;
        int[] start = new int[BoxCount + 1];
        List<NavHullNeighbour> entries = new(BoxCount * 6);
        SortedSet<int> found = [];
        for (int box = 0; box < BoxCount; box++)
        {
            start[box] = entries.Count;
            NavHullBox b = Box(box);
            int cell = _boxCell[box];
            int cx = cell % Columns;
            int cy = cell / Columns;
            for (int d = 0; d < 6; d++)
            {
                found.Clear();
                if (d < 4)
                {
                    (int dx, int dy) = d switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                    int length = dx != 0 ? b.Y1 - b.Y0 : b.X1 - b.X0;
                    for (int i = 0; i < length; i++)
                    {
                        // The column beside the face, in this cell or the next.
                        int x = dx > 0 ? b.X1 : dx < 0 ? b.X0 - 1 : b.X0 + i;
                        int y = dy > 0 ? b.Y1 : dy < 0 ? b.Y0 - 1 : b.Y0 + i;
                        int ncx = cx + (x < 0 ? -1 : x >= n ? 1 : 0);
                        int ncy = cy + (y < 0 ? -1 : y >= n ? 1 : 0);
                        if (ncx < 0 || ncy < 0 || ncx >= Columns || ncy >= Rows)
                        {
                            continue;
                        }

                        foreach (int other in ColumnBoxes((ncy * Columns) + ncx, (x + n) % n, (y + n) % n))
                        {
                            NavHullBox o = Box(other);
                            if (o.ZLo <= b.ZHi && o.ZHi >= b.ZLo)
                            {
                                found.Add(other);
                            }
                        }
                    }
                }
                else
                {
                    for (int y = b.Y0; y < b.Y1; y++)
                    {
                        for (int x = b.X0; x < b.X1; x++)
                        {
                            int other = Touching(box, x, y, d == 4 ? 1 : -1);
                            if (other >= 0)
                            {
                                found.Add(other);
                            }
                        }
                    }
                }

                foreach (int other in found)
                {
                    entries.Add(new NavHullNeighbour(other, (Nav3dDirection)d));
                }
            }
        }

        start[BoxCount] = entries.Count;
        return (start, [.. entries]);
    }

    private (int[] Start, int[] Index) DeriveJumps()
    {
        int[] start = new int[BoxCount + 1];
        for (int j = 0; j < JumpCount; j++)
        {
            NavHullJump jump = Jump(j);
            start[jump.BoxA + 1]++;
            start[jump.BoxB + 1]++;
        }

        for (int b = 0; b < BoxCount; b++)
        {
            start[b + 1] += start[b];
        }

        int[] index = new int[start[BoxCount]];
        int[] fill = new int[BoxCount];
        for (int j = 0; j < JumpCount; j++)
        {
            NavHullJump jump = Jump(j);
            index[start[jump.BoxA] + fill[jump.BoxA]++] = j;
            index[start[jump.BoxB] + fill[jump.BoxB]++] = j;
        }

        return (start, index);
    }

    private (int[] Start, int[] Boxes) DeriveObstacleBoxes()
    {
        List<(int Obstacle, int Box)> pairs = [];
        for (int box = 0; box < BoxCount; box++)
        {
            ReadOnlySpan<byte> key = KeyRecord(box);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(key[12..]);
            for (uint i = first; i < first + count; i++)
            {
                pairs.Add(((int)BinaryPrimitives.ReadUInt32LittleEndian(Record(_s.Shuts, (int)i, _s.ShutBytes)), box));
            }
        }

        int[] start = new int[ObstacleCount + 1];
        foreach ((int obstacle, _) in pairs)
        {
            start[obstacle + 1]++;
        }

        for (int o = 0; o < ObstacleCount; o++)
        {
            start[o + 1] += start[o];
        }

        int[] boxes = new int[pairs.Count];
        int[] fill = new int[ObstacleCount];
        foreach ((int obstacle, int box) in pairs)
        {
            boxes[start[obstacle] + fill[obstacle]++] = box;
        }

        return (start, boxes);
    }

    private ReadOnlySpan<byte> Record(Section section, int index, int size)
    {
        if ((uint)index >= (uint)(section.Length / size))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "no such record.");
        }

        return Bytes.Slice(section.Offset + (index * size), size);
    }

    private uint ReadU32(Section section, int index) => BinaryPrimitives.ReadUInt32LittleEndian(Record(section, index, 4));

    private readonly record struct Section(int Offset, int Length);

    private readonly record struct Sections
    {
        public Section Strings { get; init; }

        public Section Widths { get; init; }

        public Section CellStarts { get; init; }

        public Section Boxes { get; init; }

        public Section Keys { get; init; }

        public Section Shuts { get; init; }

        public Section Obstacles { get; init; }

        public Section Jumps { get; init; }

        public int KeyBytes { get; init; }

        public int ShutBytes { get; init; }
    }

    private readonly record struct Header
    {
        public NavCodec Codec { get; init; }

        public int HeaderBytes { get; init; }

        public int SectionCount { get; init; }

        public float VoxelSize { get; init; }

        public int CellVoxels { get; init; }

        public int Columns { get; init; }

        public int Rows { get; init; }

        public Vec3 Origin { get; init; }

        public int ColumnVoxels { get; init; }

        public int WidthCount { get; init; }

        public float FloorNormalZ { get; init; }

        public float StepHeight { get; init; }

        public float JumpHeight { get; init; }

        public float JumpDistance { get; init; }

        public int BoxCount { get; init; }

        public int KeyCount { get; init; }

        public int ShutCount { get; init; }

        public int ObstacleCount { get; init; }

        public int JumpCount { get; init; }
    }
}
