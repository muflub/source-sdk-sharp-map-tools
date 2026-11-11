//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A map laid out as a jump map is: a room with the spawn, and a second room
/// 5,000 units above it that only a teleport reaches (its
/// <c>info_teleport_destination</c> puts it in the play space), each sealed
/// on its own, compiled through vbsp and vvis. The play space is 332 voxels
/// of 16 tall, more than a version 3 column holds.
/// </summary>
public sealed class JumpTowerMap : IAsyncLifetime
{
    /// <summary>The low room's inside: the spawn's.</summary>
    public static readonly Box Low = new(new Vec3(0, 0, 0), new Vec3(512, 512, 256));

    /// <summary>The high room's inside: the teleport destination's.</summary>
    public static readonly Box High = new(new Vec3(0, 0, 5000), new Vec3(256, 256, 5256));

    /// <summary>The compiled map's bytes, with vis.</summary>
    public byte[] Bytes { get; private set; } = [];

    /// <summary>The map as read back from <see cref="Bytes"/>.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        VmfDocument document = TallSkyboxMap.Rooms([(Low.Mins, Low.Maxs), (High.Mins, High.Maxs)]);
        document.Chunks.Add(TallSkyboxMap.Entity("info_player_teamspawn", 9001, ("origin", "128 128 0"), ("TeamNum", "2")));
        document.Chunks.Add(TallSkyboxMap.Entity("info_teleport_destination", 9002, ("origin", "128 128 5000"), ("targetname", "up")));
        (Bytes, Bsp) = await TallSkyboxMap.CompileAsync(document);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// A compiled map taller than a byte of voxels builds at 16 units as a
/// version 4 <c>.nav3d</c>, its high room's runs past voxel 255, and the
/// space between the rooms, which no play leaf's box reaches, is skipped
/// without changing a byte.
/// </summary>
public sealed class NavTallMapTests(JumpTowerMap map) : IClassFixture<JumpTowerMap>
{
    private BspNavResult Build(BspNavOptions? options = null) =>
        LevelNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), null, options);

    [Fact]
    public void BothRoomsAreThePlaySpaceAndEachLeafBoxIsInItsBounds()
    {
        NavPlaySpace space = Assert.IsType<NavPlaySpace>(NavPlayableSpace.Find(map.Bsp));

        Assert.Equal(space.LeafInPlay.Count(p => p), space.LeafBounds.Count);
        Assert.All(space.LeafBounds, b =>
        {
            Assert.InRange(b.Mins.Z, space.Bounds.Mins.Z, space.Bounds.Maxs.Z);
            Assert.InRange(b.Maxs.Z, space.Bounds.Mins.Z, space.Bounds.Maxs.Z);
        });
        Assert.Equal(space.Bounds.Mins.Z, space.LeafBounds.Min(b => b.Mins.Z));
        Assert.Equal(space.Bounds.Maxs.Z, space.LeafBounds.Max(b => b.Maxs.Z));
        Assert.InRange(space.Bounds.Maxs.Z, High.Maxs.Z, High.Maxs.Z + 1);
        Assert.DoesNotContain(space.LeafBounds, b => b.Mins.Z > Low.Maxs.Z + 1 && b.Maxs.Z < High.Mins.Z - 1);
    }

    private static Box High => JumpTowerMap.High;

    private static Box Low => JumpTowerMap.Low;

    [Fact]
    public void TheMapBuildsAsVersion4WithTheHighRoomsRunsPastVoxel255()
    {
        Nav3dLevel level = Build().Level;
        Assert.True(level.CellHeights!.Max() > Nav3dFormat.MaxShapedColumnVoxels, $"{level.CellHeights!.Max()} voxels up");

        byte[] bytes = Nav3dWriter.Write(level);
        Assert.Equal(Nav3dFormat.Version, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Nav3dReader nav = Nav3dReader.Open(bytes);

        int low = nav.FindLeaf(new Vec3(128, 128, 8));
        int high = nav.FindLeaf(new Vec3(128, 128, High.Mins.Z + 8));
        Assert.True(low >= 0 && high >= 0, $"leaves {low} and {high}");
        Assert.True(nav.Leaf(high).ZLo > Nav3dFormat.MaxShapedColumnVoxels, $"the high room's floor run starts at voxel {nav.Leaf(high).ZLo}");
        Assert.True(nav.StandableAtCentre(high, 32, 72, Nav3dClipClass.Player));
        Assert.Equal(High.Mins.Z, nav.Leaf(high).PlayerFloorZ);
        Assert.Equal(-1, nav.FindLeaf(new Vec3(128, 128, 2500)));
    }

    [Fact]
    public void SkippingWhatNoPlayLeafReachesChangesNoByte()
    {
        byte[] skipped = Nav3dWriter.Write(Build().Level);
        byte[] walked = Nav3dWriter.Write(Build(new BspNavOptions { SkipOutsidePlay = false }).Level);

        Assert.Equal(walked, skipped);
    }
}
