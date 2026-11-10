//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// Points of interest on a floor whose top is not on a voxel face: a kit of
/// wall_depth 8 under nav_voxel_size 16 puts the floor slab from z 0 to 8,
/// so the voxel from 0 to 16 is blocked and the grid stands agents in the
/// voxel from 16 on a floor recorded at 8. A marker standing on that floor
/// (z 8) is checked in the voxel the grid stands it in; one sunk into the
/// floor or inside a solid is still refused.
/// </summary>
public sealed class RoomNavThinFloorTests
{
    private static RoomDefinition Thin => new(
        "thin", RoomHarness.Cell, new SocketKit(96f, RoomHarness.Cell - 16f, 8f),
        [.. Enum.GetValues<RoomFacing>().Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);

    private static async Task<RoomNav> BuildAsync(params VmfChunk[] entities)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Thin);
        library.GetChunk("world")!.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(180, 180, 8), new Vec3(220, 220, 100), 6200));
        foreach (VmfChunk entity in entities)
        {
            library.Chunks.Add(entity);
        }

        LibraryRoom room = RoomLibraryVmf.SplitLibrary(library).Rooms.Single();
        (VmfDocument stripped, IReadOnlyList<AuthoredPoi> pois) = RoomPois.Extract(room.Document);
        RoomObject compiled = await RoomCompiler.CompileAsync(stripped, room.Definition, await RoomPropHarness.ContextAsync(room.Definition.Name));
        return RoomNavBuilder.Build(room.Definition, compiled.Bsp, pois, RoomRole.None, NavSettings.Default);
    }

    private static VmfChunk Poi(int id, Vec3 at, params (string Key, string Value)[] keys) =>
        RoomPropHarness.Entity(RoomPois.Entity, id, at, [("poi_type", "landmark"), .. keys]);

    /// <summary>
    /// A landmark and an arrival standing on the floor at z 8 build, kept
    /// where the author put them; the door points stand on the same floor.
    /// </summary>
    [Fact]
    public async Task MarkersOnAFloorInsideAVoxelBuild()
    {
        Assert.Equal(16f, NavSettings.Default.VoxelSize);
        RoomNav nav = await BuildAsync(
            Poi(700, new Vec3(128, 64, 8), ("targetname", "lookout")),
            RoomPropHarness.Entity(RoomPois.Entity, 701, new Vec3(64, 128, 8), ("poi_type", RoomPois.ArrivalType), ("angles", "0 90 0")));
        Assert.Equal([new Vec3(128, 64, 8), new Vec3(64, 128, 8)], nav.Pois.Select(p => p.Position));
        Assert.All(nav.SocketData, s => Assert.Equal(8f, s.DoorPoint.Z));
    }

    /// <summary>A marker sunk into the floor slab (z 4, below the floor's top at 8) is refused, as it was.</summary>
    [Fact]
    public async Task AMarkerSunkIntoTheFloorIsRefused()
    {
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => BuildAsync(Poi(702, new Vec3(128, 64, 4))));
        Assert.Contains("info_poi 702 at (128 64 4) is where agent", refused.Message, StringComparison.Ordinal);
        Assert.Contains("(voxel 8 4 0 is blocked for it)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A marker inside the pillar standing on the floor is refused, as it was.</summary>
    [Fact]
    public async Task AMarkerInsideASolidIsRefused()
    {
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => BuildAsync(Poi(703, new Vec3(200, 200, 8))));
        Assert.Contains("info_poi 703 at (200 200 8) is where agent", refused.Message, StringComparison.Ordinal);
        Assert.Contains("is blocked for it", refused.Message, StringComparison.Ordinal);
    }
}
