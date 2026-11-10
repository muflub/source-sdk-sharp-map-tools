//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The jump search on several threads (<see cref="LevelNavLinker.FindJumps(LevelNavLinker.Grid, float, float, float, int, int, CancellationToken)"/>):
/// the same links, in the same order, as the one-thread search, whatever the
/// chunk and the degree.
/// </summary>
public sealed class NavJumpSearchTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    /// <summary>
    /// The level's links, found again over its own leaves at one thread in
    /// one chunk and at four threads in chunks of one, three and 64 leaves,
    /// are the same list each time; the map has links of both directions to
    /// find.
    /// </summary>
    [Fact]
    public void AnyChunkAndDegreeFindTheSameLinks()
    {
        Nav3dLevel level = map.Build().Level;
        Assert.True(level.Jumps.Count > 0, "the hall has jump links");
        Assert.True(level.Leaves.Length > 64, $"{level.Leaves.Length} leaves");

        List<Nav3dJump> one = Find(level, 1, int.MaxValue);
        Assert.Equal(level.Jumps, one);
        foreach (int chunk in new[] { 1, 3, 64 })
        {
            Assert.Equal(one, Find(level, 4, chunk));
        }
    }

    /// <summary>A degree or a chunk below one is refused.</summary>
    [Fact]
    public void ADegreeOrChunkBelowOneIsRefused()
    {
        Nav3dLevel level = map.Build().Level;
        Assert.Throws<ArgumentOutOfRangeException>(() => Find(level, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Find(level, 1, 0));
    }

    /// <summary>The search over a built level's own leaves and records, at a degree and chunk.</summary>
    private static List<Nav3dJump> Find(Nav3dLevel level, int degree, int chunk) =>
        LevelNavLinker.FindJumps(
            new LevelNavLinker.Grid(
                level.Columns, level.Rows, level.CellVoxels, level.VoxelSize, level.Roots, [.. level.ColumnStarts], [.. level.Leaves],
                new NavRecordTable(), Offsets(level.Clearance)),
            level.StepHeight, level.JumpHeight, level.JumpDistance, degree, chunk, CancellationToken.None);

    /// <summary>Each record's offset in a clearance blob, in order: the blocked record first, as the build numbers them.</summary>
    private static uint[] Offsets(byte[] clearance)
    {
        List<uint> offsets = [];
        for (int at = 0; at < clearance.Length;)
        {
            offsets.Add((uint)at);
            ReadOnlySpan<byte> header = clearance.AsSpan(at);
            at += Nav3dClearance.HeaderBytes
                + (BinaryPrimitives.ReadUInt16LittleEndian(header) * Nav3dClearance.CornerBytes)
                + (BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) * Nav3dClearance.DynamicBytes)
                + (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) * Nav3dClearance.BrushBytes);
        }

        return [.. offsets];
    }
}
