//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>
/// The <c>.navhull</c> file: a level round-trips through the writer and the
/// reader byte for byte under every codec; the reader lists each column's
/// boxes, finds points, derives neighbours, jump and obstacle indexes, and
/// answers fits from the ceilings without allocating; a broken file is
/// refused saying what is wrong, and an inconsistent level by the writer.
/// </summary>
public sealed class NavHullFileTests
{
    private const NavHullFlags Floor = NavHullFlags.Grounded | NavHullFlags.Walkable;

    /// <summary>
    /// Two cells of 2 x 2 columns, 8 voxels of 16 up, built for widths 16 and
    /// 32. Cell 0: a floor box over its south row (the door lowers its
    /// ceilings when shut), the open box above it (an overhanging brush in it
    /// passed under up to 50 or stood over from 90), and a player-clip column
    /// in its north-west corner. Cell 1: a ladder up its west columns on a
    /// floor at 32, beside the open box, under a brush passed under up to 70.
    /// One jump link from the floor to the ladder's floor.
    /// </summary>
    internal static NavHullLevel Sample() => new()
    {
        VoxelSize = 16,
        CellVoxels = 2,
        Columns = 2,
        Rows = 1,
        Origin = new Vec3(0, 0, 0),
        ColumnVoxels = 8,
        FloorNormalZ = 0.7f,
        StepHeight = 18,
        JumpHeight = 56,
        JumpDistance = 100,
        Widths = [16, 32],
        CellStarts = [0, 3, 4],
        Boxes =
        [
            new NavHullBox(0, 0, 2, 1, 0, 2, 0),
            new NavHullBox(0, 0, 2, 1, 2, 2, 1),
            new NavHullBox(0, 1, 1, 2, 0, 4, 3),
            new NavHullBox(0, 0, 1, 2, 2, 2, 2),
        ],
        Keys =
        [
            new NavHullKey(Floor, 256, 0, [128, 64], 0, 1),
            new NavHullKey(NavHullFlags.None, 256, 0, [128, 64], 0, 0, 0, 1),
            new NavHullKey(NavHullFlags.Ladder | Floor, 384, 32, [100, float.NegativeInfinity], 0, 0, 1, 1),
            new NavHullKey(NavHullFlags.PlayerBlocked, 256, 0, [float.NegativeInfinity, float.NegativeInfinity], 0, 0),
        ],
        Shuts = [new NavHullShut(0, [40, 30])],
        Overhangs =
        [
            new NavHullOverhang([50, 50], [90, 90]),
            new NavHullOverhang([70, 70], [double.PositiveInfinity, double.PositiveInfinity]),
        ],
        Obstacles = [new Nav3dObstacle("gate", "func_door", 0, 7, Nav3dObstacleKind.Door, new Vec3(0, 0, 0), new Vec3(32, 16, 64))],
        Jumps = [new NavHullJump(0, 3, 1, 0, 0, 0, 32, Nav3dDirection.East, 1)],
    };

    private static NavHullReader Open() => NavHullReader.Open(NavHullWriter.Write(Sample()));

    public static TheoryData<string> Codecs => ["none", "deflate:6", "brotli:5"];

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ALevelRoundTripsByteForByte(string codec)
    {
        Assert.True(NavCompression.TryParse(codec, out NavCompression compression));
        byte[] bytes = NavHullWriter.Write(Sample(), compression);
        NavHullReader nav = NavHullReader.Open(bytes);

        Assert.Equal(compression.Codec, nav.Codec);
        Assert.Equal(bytes, NavHullWriter.Write(nav.ToLevel(), compression));
        Assert.Equal(NavHullWriter.Write(Sample()), NavHullWriter.Write(nav.ToLevel()));
    }

    [Fact]
    public void TheHeaderAndRecordsReadBack()
    {
        NavHullReader nav = Open();
        Assert.Equal((16f, 2, 2, 1, 8, 2, 4, 4, 1, 1), (nav.VoxelSize, nav.CellVoxels, nav.Columns, nav.Rows, nav.ColumnVoxels, nav.CellCount, nav.BoxCount, nav.KeyCount, nav.ObstacleCount, nav.JumpCount));
        Assert.Equal((0.7f, 18f, 56f, 100f), (nav.FloorNormalZ, nav.StepHeight, nav.JumpHeight, nav.JumpDistance));
        Assert.Equal([16f, 32f], nav.Widths);
        Assert.Equal((0, 1, -1), (nav.WidthIndex(16), nav.WidthIndex(32), nav.WidthIndex(48)));
        Assert.Equal(Sample().Boxes, Enumerable.Range(0, 4).Select(nav.Box));
        Assert.Equal(Sample().Keys, Enumerable.Range(0, 4).Select(nav.Key));
        Assert.Equal([0, 0, 0, 1], Enumerable.Range(0, 4).Select(nav.BoxCell));
        Assert.Equal(((0, 3), (3, 4)), (nav.CellBoxes(0), nav.CellBoxes(1)));
        Assert.Equal((new Vec3(0, 0, 0), new Vec3(32, 16, 32)), nav.BoxBounds(0));
        Assert.Equal((new Vec3(32, 0, 32), new Vec3(48, 32, 64)), nav.BoxBounds(3));
        Assert.Equal((Floor, 0f, 1f), (nav.Flags(0), nav.FloorZ(0), nav.CostMultiplier(0)));
        Assert.Equal(1.5f, nav.CostMultiplier(3));
        Assert.Equal(("gate", "func_door", 0u, 7), (nav.Obstacle(0).Name, nav.Obstacle(0).ClassName, nav.Obstacle(0).Cell, nav.Obstacle(0).HammerId));
        Assert.Throws<ArgumentOutOfRangeException>(() => nav.CellBoxes(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => nav.Box(4));
    }

    [Fact]
    public void AColumnListsItsBoxesLowToHighAndAPointFindsItsBox()
    {
        NavHullReader nav = Open();
        Assert.Equal([0, 1], nav.ColumnBoxes(0, 0, 0).ToArray());
        Assert.Equal([0, 1], nav.ColumnBoxes(0, 1, 0).ToArray());
        Assert.Equal([2], nav.ColumnBoxes(0, 0, 1).ToArray());
        Assert.Empty(nav.ColumnBoxes(0, 1, 1).ToArray());
        Assert.Equal([3], nav.ColumnBoxes(1, 0, 1).ToArray());
        Assert.Empty(nav.ColumnBoxes(1, 1, 0).ToArray());
        Assert.Empty(nav.ColumnBoxes(5, 0, 0).ToArray());
        Assert.Empty(nav.ColumnBoxes(0, 2, 0).ToArray());

        Assert.Equal(0, nav.FindBox(new Vec3(8, 8, 8)));
        Assert.Equal(1, nav.FindBox(new Vec3(24, 8, 40)));
        Assert.Equal(2, nav.FindBox(new Vec3(8, 24, 8)));
        Assert.Equal(3, nav.FindBox(new Vec3(40, 24, 40)));
        Assert.Equal(-1, nav.FindBox(new Vec3(40, 8, 8)));
        Assert.Equal(-1, nav.FindBox(new Vec3(8, 8, 70)));
        Assert.Equal(-1, nav.FindBox(new Vec3(-1, 8, 8)));
        Assert.Equal(-1, nav.FindBox(new Vec3(8, 8, 128)));
        Assert.Equal(-1, nav.FindBox(new Vec3(64, 8, 8)));
    }

    [Fact]
    public void BelowAboveAndNeighboursAreTheTouchingBoxes()
    {
        NavHullReader nav = Open();
        Assert.Equal((0, 1, -1, -1, -1), (nav.Below(1, 1, 0), nav.Above(0, 0, 0), nav.Below(0, 0, 0), nav.Above(1, 0, 0), nav.Below(3, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => nav.Below(0, 0, 1));

        // The open box: the ladder east across the cell face, the clip
        // column north (its run overlaps), the floor box below.
        Assert.Equal(
            [new NavHullNeighbour(3, Nav3dDirection.East), new NavHullNeighbour(2, Nav3dDirection.North), new NavHullNeighbour(0, Nav3dDirection.Down)],
            nav.Neighbours(1).ToArray());
        Assert.Equal([new NavHullNeighbour(1, Nav3dDirection.West)], nav.Neighbours(3).ToArray());
        Assert.Equal(
            [new NavHullNeighbour(2, Nav3dDirection.North), new NavHullNeighbour(1, Nav3dDirection.Up)],
            nav.Neighbours(0).ToArray());
    }

    [Fact]
    public void AFitIsTheBodysTopAgainstItsWidthsCeiling()
    {
        NavHullReader nav = Open();
        Assert.True(nav.FitsAt(0, 0, 16, 128));
        Assert.False(nav.FitsAt(0, 0, 16, 128.01f));
        Assert.True(nav.FitsAt(0, 10, 32, 54));
        Assert.False(nav.FitsAt(0, 10, 32, 55));
        Assert.False(nav.FitsAt(2, 0, 16, 1));
        Assert.Equal(64f, nav.Ceiling(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => nav.Ceiling(0, 2));
        Assert.Contains("built for 16, 32", Assert.Throws<ArgumentException>(() => nav.FitsAt(0, 0, 48, 1)).Message, StringComparison.Ordinal);

        Assert.True(nav.Stands(0, 32, 64));
        Assert.False(nav.Stands(0, 32, 65));
        Assert.False(nav.Stands(1, 16, 10));

        Assert.True(nav.Climbs(3, 16, 38));
        Assert.False(nav.Climbs(3, 16, 39));
        Assert.False(nav.Climbs(3, 32, 1));
        Assert.False(nav.Climbs(0, 16, 1));

        Assert.True(nav.StandsWithShut(0, 0, 16, 40));
        Assert.False(nav.StandsWithShut(0, 0, 16, 41));
        Assert.True(nav.StandsWithShut(0, 0, 32, 30));
        Assert.False(nav.StandsWithShut(1, 0, 16, 1));
        Assert.True(nav.StandsWithShut(3, 0, 16, 38));
        Assert.False(nav.StandsWithShut(3, 0, 16, 39));
        Assert.Throws<ArgumentOutOfRangeException>(() => nav.StandsWithShut(0, 1, 16, 1));
    }

    /// <summary>
    /// An overhanging brush is cleared by passing under it (the top at most
    /// its under-limit) or standing over it (the feet at least its
    /// over-limit), whatever the ceiling; between, a body meets it.
    /// </summary>
    [Fact]
    public void AnOverhangIsPassedUnderOrStoodOver()
    {
        NavHullReader nav = Open();
        Assert.Equal(new NavHullOverhang([50, 50], [90, 90]), nav.Overhang(0));
        Assert.Equal((0u, 1u), (nav.Key(1).OverhangFirst, nav.Key(1).OverhangCount));

        Assert.True(nav.FitsAt(1, 32, 16, 18));
        Assert.False(nav.FitsAt(1, 32, 16, 19));
        Assert.False(nav.FitsAt(1, 89, 16, 10));
        Assert.True(nav.FitsAt(1, 90, 16, 38));
        Assert.False(nav.FitsAt(1, 90, 16, 39));

        Assert.True(nav.Stands(3, 16, 38));
        Assert.False(nav.Stands(3, 16, 39));

        NavHullOverhang o = nav.Overhang(0);
        Assert.True(o.Clears(0, 32, 50));
        Assert.True(o.Clears(1, 90, 200));
        Assert.False(o.Clears(0, 89, 51));
        Assert.Equal(o.GetHashCode(), new NavHullOverhang([50, 50], [90, 90]).GetHashCode());
        Assert.NotEqual(o, nav.Overhang(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => nav.Overhang(2));
    }

    [Fact]
    public void JumpLinksAndObstaclesAreIndexedByBox()
    {
        NavHullReader nav = Open();
        Assert.Equal([0], nav.Jumps(0).ToArray());
        Assert.Equal([0], nav.Jumps(3).ToArray());
        Assert.Empty(nav.Jumps(1).ToArray());
        Assert.Equal(Sample().Jumps[0], nav.Jump(0));
        Assert.Equal([0], nav.ObstacleBoxes(0).ToArray());
    }

    [Fact]
    public void TheMapBindingIsCheckedWhenTheFileHasOne()
    {
        NavHullReader unbound = Open();
        Assert.Null(unbound.Bsp);
        Assert.False(unbound.MatchesBsp([1, 2, 3]));
        Assert.False(unbound.MatchesBsp(new Nav3dBspBinding(1, new string('a', 64))));

        NavHullLevel level = Sample();
        Nav3dBspBinding binding = new(7, new string('b', 64));
        NavHullReader bound = NavHullReader.Open(NavHullWriter.Write(new NavHullLevel
        {
            VoxelSize = level.VoxelSize, CellVoxels = level.CellVoxels, Columns = level.Columns, Rows = level.Rows, ColumnVoxels = level.ColumnVoxels,
            Widths = level.Widths, CellStarts = level.CellStarts, Boxes = level.Boxes, Keys = level.Keys, Shuts = level.Shuts, Overhangs = level.Overhangs,
            Obstacles = level.Obstacles, Jumps = level.Jumps, Bsp = binding,
        }));
        Assert.Equal(binding, bound.Bsp);
        Assert.True(bound.MatchesBsp(binding));
        Assert.False(bound.MatchesBsp([1, 2, 3]));
    }

    [Fact]
    public void AnUnnamedObstacleReadsBackUnnamed()
    {
        NavHullLevel s = Sample();
        NavHullReader nav = NavHullReader.Open(NavHullWriter.Write(Copy(s, obstacles: [s.Obstacles[0] with { Name = null }])));
        Assert.Null(nav.Obstacle(0).Name);
        Assert.Equal("func_door", nav.Obstacle(0).ClassName);
    }

    [Fact]
    public void TheHotQueriesDoNotAllocate()
    {
        NavHullReader nav = Open();
        Vec3 point = new(8, 8, 8);
        _ = nav.FindBox(point);
        _ = nav.Stands(0, 16, 20);
        _ = nav.StandsWithShut(0, 0, 16, 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += nav.FindBox(point) + nav.ColumnBoxes(0, 0, 0).Length + nav.Neighbours(1).Length + nav.Jumps(0).Length;
            sum += nav.FitsAt(0, 0, 16, 20) ? 1 : 0;
            sum += nav.Stands(0, 32, 20) ? 1 : 0;
            sum += nav.StandsWithShut(0, 0, 16, 20) ? 1 : 0;
            sum += nav.Climbs(3, 16, 20) ? 1 : 0;
            sum += nav.Below(1, 0, 0) + nav.Above(0, 0, 0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sum > 0);
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "magic", "magic is missing" },
        { "version", "this build reads version 2" },
        { "stored", "stored bytes" },
        { "raw", "stored and image lengths differ" },
        { "counts", "counts are out of range" },
        { "grid", "describes no valid grid" },
        { "directory", "section directory runs past" },
        { "section", "lies outside the file" },
        { "duplicate", "two \"STRS\" sections" },
        { "missing", "has no \"STRS\" section" },
        { "size", "\"WDTH\" section is" },
        { "strings", "string table must start with the empty string" },
        { "widths", "widths are not finite, above 0 and rising" },
        { "cells", "not a partition of the 4 boxes" },
        { "box", "box 1 leaves its cell" },
        { "key", "key 0 names shut records out of range" },
        { "overhangs", "key 1 names overhang records out of range" },
        { "overhang size", "\"OVHG\" section is" },
        { "shut", "shut record 0 names an obstacle out of range" },
        { "obstacle", "obstacle 0 names a cell out of range" },
        { "jump", "jump link 0 names boxes 0 and 3" },
        { "overlap", "overlap in a column" },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void ABrokenFileIsRefusedSayingWhatIsWrong(string fault, string expected)
    {
        byte[] bytes = NavHullWriter.Write(Sample());
        int image = NavHullFormat.EnvelopeBytes;
        int Entry(string tag)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(image + 4));
            for (int i = 0; i < count; i++)
            {
                int at = image + NavHullFormat.HeaderBytes + (i * NavHullFormat.DirectoryEntryBytes);
                if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == tag)
                {
                    return at;
                }
            }

            throw new InvalidOperationException(tag);
        }

        int At(string tag) => image + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Entry(tag) + 8));
        switch (fault)
        {
            case "magic": bytes[0] = (byte)'X'; break;
            case "version": bytes[8] = 1; break;
            case "stored": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 5); break;
            case "raw": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), bytes.Length); break;
            case "counts": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 40), 0); break;
            case "grid": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 12), 0); break;
            case "directory": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 4), 100000); break;
            case "section": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Entry("BOXS") + 12), 100000); break;
            case "duplicate": "STRS"u8.CopyTo(bytes.AsSpan(Entry("WDTH"))); break;
            case "missing": "XXXX"u8.CopyTo(bytes.AsSpan(Entry("STRS"))); break;
            case "size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Entry("WDTH") + 12), 4); break;
            case "strings": bytes[At("STRS")] = (byte)'x'; break;
            case "widths": BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(At("WDTH") + 4), 8); break;
            case "cells": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("CELB") + 8), 3); break;
            case "box": bytes[At("BOXS") + NavHullFormat.BoxRecordBytes + 2] = 3; break;
            case "key": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("KEYS") + 12), 2); break;
            case "overhangs": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("KEYS") + NavHullFormat.KeyRecordBytes(2) + 20), 3); break;
            case "overhang size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Entry("OVHG") + 12), 40); break;
            case "shut": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("SHUT")), 1); break;
            case "obstacle": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("DYNO") + 8), 2); break;
            case "jump": bytes[At("JUMP") + 10] = 1; break;
            case "overlap": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(At("BOXS") + NavHullFormat.BoxRecordBytes + 4), 1); break;
            default: throw new InvalidOperationException(fault);
        }

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => NavHullReader.Open(bytes));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Inconsistencies => new()
    {
        { "grid", "describes no valid grid" },
        { "no widths", "built for 0 widths" },
        { "widths", "finite, above 0 and rising" },
        { "starts", "cell starts are not 3 entries" },
        { "backwards", "cell 1's boxes end before they start" },
        { "box", "leaves its cell or names a key out of range" },
        { "key", "a ceiling per width and shut and overhang records in range" },
        { "key overhangs", "a ceiling per width and shut and overhang records in range" },
        { "overhang", "two limits per width, neither NaN" },
        { "overhang width", "two limits per width, neither NaN" },
        { "shut", "a ceiling per width and an obstacle in range" },
        { "obstacle", "names cell 5 of 2" },
        { "jump", "out of order or range" },
        { "nul", "holds a NUL" },
    };

    [Theory]
    [MemberData(nameof(Inconsistencies))]
    public void AnInconsistentLevelIsRefusedByTheWriter(string fault, string expected)
    {
        NavHullLevel s = Sample();
        NavHullLevel broken = fault switch
        {
            "grid" => Copy(s, voxels: 0),
            "no widths" => Copy(s, widths: []),
            "widths" => Copy(s, widths: [32, 16]),
            "starts" => Copy(s, starts: [0, 4]),
            "backwards" => Copy(s, starts: [0, 5, 4]),
            "box" => Copy(s, boxes: [.. s.Boxes.Select((b, i) => i == 1 ? b with { X1 = 3 } : b)]),
            "key" => Copy(s, keys: [s.Keys[0] with { ShutCount = 2 }, .. s.Keys.Skip(1)]),
            "key overhangs" => Copy(s, keys: [.. s.Keys.Take(2), s.Keys[2] with { OverhangCount = 2 }, s.Keys[3]]),
            "overhang" => Copy(s, overhangs: [s.Overhangs[0], new NavHullOverhang([1, double.NaN], [2, 3])]),
            "overhang width" => Copy(s, overhangs: [s.Overhangs[0], new NavHullOverhang([1], [2])]),
            "shut" => Copy(s, shuts: [new NavHullShut(3, [1, 2])]),
            "obstacle" => Copy(s, obstacles: [s.Obstacles[0] with { Cell = 5 }]),
            "jump" => Copy(s, jumps: [s.Jumps[0] with { BoxA = 3, BoxB = 0 }]),
            "nul" => Copy(s, obstacles: [s.Obstacles[0] with { Name = "a\0b" }]),
            _ => throw new InvalidOperationException(fault),
        };

        ArgumentException refused = Assert.Throws<ArgumentException>(() => NavHullWriter.Write(broken));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => NavHullWriter.Write(null!));
    }

    private static NavHullLevel Copy(
        NavHullLevel s, int? voxels = null, IReadOnlyList<float>? widths = null, uint[]? starts = null, NavHullBox[]? boxes = null,
        IReadOnlyList<NavHullKey>? keys = null, IReadOnlyList<NavHullShut>? shuts = null, IReadOnlyList<Nav3dObstacle>? obstacles = null,
        IReadOnlyList<NavHullJump>? jumps = null, IReadOnlyList<NavHullOverhang>? overhangs = null) => new()
        {
            VoxelSize = s.VoxelSize,
            CellVoxels = voxels ?? s.CellVoxels,
            Columns = s.Columns,
            Rows = s.Rows,
            ColumnVoxels = s.ColumnVoxels,
            Widths = widths ?? s.Widths,
            CellStarts = starts ?? s.CellStarts,
            Boxes = boxes ?? s.Boxes,
            Keys = keys ?? s.Keys,
            Shuts = shuts ?? s.Shuts,
            Overhangs = overhangs ?? s.Overhangs,
            Obstacles = obstacles ?? s.Obstacles,
            Jumps = jumps ?? s.Jumps,
        };
}
