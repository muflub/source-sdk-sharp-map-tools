//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.Tests.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Props;

/// <summary>
/// <see cref="BspTreeView.LeavesAlong"/>: a vertical line's points walked
/// down the tree together land in the leaf <see cref="BspTreeView.LeafOf"/>
/// gives each.
/// </summary>
public sealed class BspTreeViewTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    /// <summary>
    /// A hand-made tree: a plane rising with z, under it one falling with z,
    /// a vertical one and a level one, leaves 0 to 4; columns across it at
    /// heights that include points exactly on the planes (which go front)
    /// and repeated heights land each point where its own walk does, and
    /// every leaf is reached.
    /// </summary>
    [Fact]
    public void EveryPointOfAColumnLandsWhereItsOwnWalkDoes()
    {
        Vec3 up = new Vec3(0.3f, -0.2f, 1f).Normalise().Normalised;
        Vec3 down = new Vec3(-0.5f, 0.1f, -1f).Normalise().Normalised;
        DPlane[] planes =
        [
            new() { Normal = up, Dist = 40 },
            new() { Normal = down, Dist = -10 },
            new() { Normal = new Vec3(1, 0, 0), Dist = 8 },
            new() { Normal = new Vec3(0, 0, 1), Dist = 96 },
        ];
        BspTreeView tree = new([Node(0, 2, 1), Node(1, -1, -2), Node(2, -3, 3), Node(3, -4, -5)], planes, [0, 0, 0, 0, 0]);

        HashSet<int> reached = [];
        float[] heights = [-50, -20, -20, 0, 7.5f, 10, 31.25f, 40, 40, 55, 96, 96, 100, 150];
        int[] leaves = new int[heights.Length];
        for (float x = -40; x <= 40; x += 2.5f)
        {
            for (float y = -30; y <= 30; y += 7.5f)
            {
                tree.LeavesAlong(x, y, heights, leaves);
                for (int i = 0; i < heights.Length; i++)
                {
                    Assert.True(tree.LeafOf(new Vec3(x, y, heights[i])) == leaves[i], $"({x}, {y}, {heights[i]}): {leaves[i]}");
                    reached.Add(leaves[i]);
                }
            }
        }

        Assert.Equal([0, 1, 2, 3, 4], reached.Order());
    }

    /// <summary>A compiled map's tree gives every voxel centre of many columns the leaf its own walk does.</summary>
    [Fact]
    public void AMapsColumnsLandWhereTheirOwnWalksDo()
    {
        BspTreeView tree = BspTreeView.FromBsp(map.Bsp);
        float[] heights = [.. Enumerable.Range(0, 40).Select(z => -40 + (z * 8f) + 4)];
        int[] leaves = new int[heights.Length];
        HashSet<int> reached = [];
        for (float x = -60; x <= 600; x += 13)
        {
            for (float y = -60; y <= 760; y += 17)
            {
                tree.LeavesAlong(x, y, heights, leaves);
                for (int i = 0; i < heights.Length; i++)
                {
                    Assert.Equal(tree.LeafOf(new Vec3(x, y, heights[i])), leaves[i]);
                    reached.Add(leaves[i]);
                }
            }
        }

        Assert.True(reached.Count > 10, $"{reached.Count} leaves reached");
    }

    /// <summary>The heights must rise and match the leaves one for one.</summary>
    [Fact]
    public void HeightsThatFallOrDoNotMatchAreRefused()
    {
        BspTreeView tree = BspTreeView.FromBsp(map.Bsp);
        Assert.Throws<ArgumentException>(() => tree.LeavesAlong(0, 0, [1, 2], new int[3]));
        Assert.Throws<ArgumentException>(() => tree.LeavesAlong(0, 0, [1, 3, 2], new int[3]));
        tree.LeavesAlong(0, 0, [], []);
    }

    private static DNode Node(int plane, int front, int back)
    {
        DNode node = new() { PlaneNum = plane };
        node.Children[0] = front;
        node.Children[1] = back;
        return node;
    }
}
